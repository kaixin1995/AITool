using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Operations;
using AITool.Domain.Operations;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Controllers.Admin;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using SqlSugar;
using Xunit;

namespace AITool.IntegrationTests.Developer;

/// <summary>Only isolated SQLite files and handler inspection; no host startup or real network.</summary>
public sealed class ProxyProfileCredentialTests
{
    [Fact]
    public async Task LegacyCrud_StillUsesSameResponseShapes_AndNeverReturnsCredentials()
    {
        using var fixture = new Fixture();
        var created = Json(await fixture.Controller.Create(new ProxyProfilePayload
        {
            Key = "legacy", Name = "Legacy proxy", ProxyUrl = "http://alice:p%40ss%3Asecret@127.0.0.1:7890"
        }, default));
        var data = created.GetProperty("data");
        var id = data.GetProperty("id").GetGuid();
        data.GetProperty("key").GetString().Should().Be("legacy");
        data.GetProperty("hasCredentials").GetBoolean().Should().BeTrue();
        data.GetProperty("revision").GetInt64().Should().Be(1);
        AssertSafe(data);

        var stored = await fixture.Db.ProxyProfiles.InSingleAsync(id);
        stored.ProxyUrl.Should().Be("http://127.0.0.1:7890");
        stored.ProtectedUsername.Should().NotBeNullOrEmpty().And.NotBe("alice");
        stored.ProtectedPassword.Should().NotBeNullOrEmpty().And.NotContain("secret");
        fixture.Secrets.ReadCredentials(stored)!.Password.Should().Be("p@ss:secret");

        var list = Json(await fixture.Controller.List(default));
        list.ValueKind.Should().Be(JsonValueKind.Array);
        AssertSafe(list[0]);
        var detail = Json(await fixture.Controller.Get(id, default));
        detail.GetProperty("success").GetBoolean().Should().BeTrue();
        AssertSafe(detail.GetProperty("data"));

        // An old client sends only the public URL back: keep encrypted credentials.
        var updated = Json(await fixture.Controller.Update(id, new ProxyProfilePayload
        {
            Name = "Renamed", ProxyUrl = "socks5://127.0.0.1:10808", IsEnabled = false
        }, default));
        updated.GetProperty("success").GetBoolean().Should().BeTrue();
        var after = await fixture.Db.ProxyProfiles.InSingleAsync(id);
        after.Id.Should().Be(id);
        after.Revision.Should().Be(2);
        after.IsEnabled.Should().BeFalse();
        after.ProtectedPassword.Should().Be(stored.ProtectedPassword);
        fixture.Secrets.ReadCredentials(after)!.UserName.Should().Be("alice");
        Json(await fixture.Controller.Delete(id, default)).GetProperty("success").GetBoolean().Should().BeTrue();
        (await fixture.Controller.Get(id, default)).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Update_ExplicitKeepReplaceClear_AndStaleRevisionAreSafe()
    {
        using var fixture = new Fixture();
        var profile = new ProxyProfile { Key = "actions", Name = "Actions" };
        fixture.Secrets.TryApplyForSave(profile, "http://127.0.0.1:7890", "Replace", "original", "old-secret", out _).Should().BeTrue();
        await fixture.Db.InsertAsync(profile);
        var keep = new ProxyProfilePayload
        {
            Name = "Kept", ProxyUrl = "http://ignored:ignored@127.0.0.1:7890", CredentialAction = "Keep", Revision = 1
        };
        (await fixture.Controller.Update(profile.Id, keep, default)).Should().BeOfType<OkObjectResult>();
        var kept = await fixture.Db.ProxyProfiles.InSingleAsync(profile.Id);
        fixture.Secrets.ReadCredentials(kept)!.Password.Should().Be("old-secret");
        kept.ProxyUrl.Should().NotContain("@");
        (await fixture.Controller.Update(profile.Id, keep, default)).Should().BeOfType<ConflictObjectResult>();

        var replace = new ProxyProfilePayload
        {
            Name = "Replaced", ProxyUrl = kept.ProxyUrl, CredentialAction = "Replace",
            Username = "new-user", Password = "new-secret", Revision = 2
        };
        (await fixture.Controller.Update(profile.Id, replace, default)).Should().BeOfType<OkObjectResult>();
        var replaced = await fixture.Db.ProxyProfiles.InSingleAsync(profile.Id);
        fixture.Secrets.ReadCredentials(replaced)!.Password.Should().Be("new-secret");
        replaced.Revision.Should().Be(3);
        (await fixture.Controller.Update(profile.Id, new ProxyProfilePayload
        {
            Name = "Cleared", ProxyUrl = replaced.ProxyUrl, CredentialAction = "Clear", Revision = 3
        }, default)).Should().BeOfType<OkObjectResult>();
        var cleared = await fixture.Db.ProxyProfiles.InSingleAsync(profile.Id);
        cleared.ProtectedUsername.Should().BeNull();
        cleared.ProtectedPassword.Should().BeNull();
        fixture.Secrets.ReadCredentials(cleared).Should().BeNull();
        Json(await fixture.Controller.Get(profile.Id, default)).GetProperty("data").GetProperty("hasCredentials")
            .GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("socks5://user%3Aname:p%40ss%3Aword%252F+plus@localhost:1080", "user:name", "p@ss:word%2F+plus")]
    [InlineData("http://%E7%94%A8%E6%88%B7:%E5%AF%86%E7%A0%81@localhost:8080", "用户", "密码")]
    [InlineData("http://user@localhost:8080", "user", "")]
    [InlineData("http://user:@[::1]:8080", "user", "")]
    public void Parser_SplitsBeforePercentDecoding_AndDecodesOnlyOnce(string input, string username, string password)
    {
        ProxyProfileSecretService.TryNormalizeProxyUrl(input, out var endpoint, out var credentials, out var error).Should().BeTrue();
        error.Should().BeNull();
        endpoint.Should().NotContain("@");
        credentials!.UserName.Should().Be(username);
        credentials.Password.Should().Be(password);
        new Uri(endpoint).UserInfo.Should().BeEmpty();
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("None")]
    [InlineData("ftp://alice:secret@localhost:21")]
    [InlineData("http://alice:secret@localhost:0")]
    [InlineData("http://alice:secret@localhost:99999")]
    [InlineData("http://alice:secret@localhost:8080/path")]
    [InlineData("http://alice:secret@localhost:8080?secret=1")]
    [InlineData("http://alice:secret@localhost:8080#secret")]
    [InlineData("http://alice:p%ZZsecret@localhost:8080")]
    [InlineData("http://alice:p%0Asecret@localhost:8080")]
    [InlineData("http://:secret@localhost:8080")]
    [InlineData("http://alice:secret@localhost:8080\\path")]
    public async Task InvalidUrls_AreRejectedWithoutEchoingCredentials(string input)
    {
        using var fixture = new Fixture();
        var result = await fixture.Controller.Create(new ProxyProfilePayload { Key = "bad", Name = "Bad", ProxyUrl = input }, default);
        result.Should().BeOfType<BadRequestObjectResult>();
        Json(result).GetRawText().Should().NotContain("secret").And.NotContain("alice");
        (await fixture.Db.ProxyProfiles.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("Replace", "user", null)]
    [InlineData("Replace", null, "secret")]
    [InlineData("Replace", "", "secret")]
    [InlineData("invalid", null, null)]
    [InlineData(null, "user", "secret")]
    public void InvalidCredentialOperations_DoNotMutateProfile(string? action, string? username, string? password)
    {
        using var fixture = new Fixture();
        var profile = new ProxyProfile { ProxyUrl = "http://localhost:8080" };
        fixture.Secrets.TryApplyForSave(profile, "http://localhost:9090", action, username, password, out var error).Should().BeFalse();
        profile.ProxyUrl.Should().Be("http://localhost:8080");
        profile.ProtectedPassword.Should().BeNull();
        error.Should().NotContain("secret");
    }

    [Fact]
    public async Task StartupMigration_IsEncryptedIdempotent_AndReadableAfterRestartWithSameKeyRing()
    {
        using var fixture = new Fixture();
        var legacy = new ProxyProfile
        {
            Key = "old", Name = "Old", ProxyUrl = "http://old%3Auser:old%40secret@localhost:7890", Revision = 1
        };
        await fixture.Db.InsertAsync(legacy);
        (await fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db)).Should().Be(1);
        var migrated = await fixture.Db.ProxyProfiles.InSingleAsync(legacy.Id);
        migrated.ProxyUrl.Should().Be("http://localhost:7890");
        migrated.Revision.Should().Be(2);
        var protectedPassword = migrated.ProtectedPassword;
        (await fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db)).Should().Be(0);
        var repeated = await fixture.Db.ProxyProfiles.InSingleAsync(legacy.Id);
        repeated.Revision.Should().Be(2);
        repeated.ProtectedPassword.Should().Be(protectedPassword);
        var restarted = new ProxyProfileSecretService(DataProtectionProvider.Create(new DirectoryInfo(fixture.KeyDirectory)));
        restarted.ReadCredentials(repeated)!.UserName.Should().Be("old:user");
        restarted.ReadCredentials(repeated)!.Password.Should().Be("old@secret");
        Json(await fixture.Controller.Get(legacy.Id, default)).GetRawText().Should().NotContain("old%40secret").And.NotContain("old@secret");
    }

    [Fact]
    public async Task Migration_PreservesAlreadyProtectedCredentialsWhenLegacyUserinfoRemains()
    {
        using var fixture = new Fixture();
        var profile = new ProxyProfile { Key = "protected", Name = "Protected" };
        fixture.Secrets.TryApplyForSave(profile, "http://localhost:7890", "Replace", "current", "current-secret", out _).Should().BeTrue();
        profile.ProxyUrl = "http://stale:stale-secret@localhost:7890";
        await fixture.Db.InsertAsync(profile);
        await fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db);
        var migrated = await fixture.Db.ProxyProfiles.InSingleAsync(profile.Id);
        migrated.ProxyUrl.Should().NotContain("@");
        fixture.Secrets.ReadCredentials(migrated)!.Password.Should().Be("current-secret");
    }

    [Theory]
    [InlineData("direct", "")]
    [InlineData("None", "")]
    [InlineData("not a URL secret", "")]
    [InlineData("http://repair:repair%40secret@localhost:7890/private-secret", "http://localhost:7890")]
    [InlineData("http://repair:repair%40secret@localhost:7890?token=query-secret", "http://localhost:7890")]
    [InlineData("http://repair:repair%40secret@localhost:7890#fragment-secret", "http://localhost:7890")]
    [InlineData("ftp://repair:repair%40secret@localhost:21/private-secret", "")]
    public async Task Migration_QuarantinesInvalidLegacyProfilesWithoutBlockingStartup(string original, string endpoint)
    {
        using var fixture = new Fixture();
        var profile = new ProxyProfile { Key = "invalid-old", Name = "Invalid old", ProxyUrl = original, IsEnabled = original != "direct" };
        await fixture.Db.InsertAsync(profile);
        var healthy = new ProxyProfile { Key = "healthy-old", Name = "Healthy old", ProxyUrl = "http://healthy:healthy-secret@localhost:7891" };
        await fixture.Db.InsertAsync(healthy);
        (await fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db)).Should().Be(2);
        var healthyAfter = await fixture.Db.ProxyProfiles.InSingleAsync(healthy.Id);
        healthyAfter.IsEnabled.Should().BeTrue();
        healthyAfter.ConfigurationError.Should().BeNull();
        healthyAfter.ProxyUrl.Should().Be("http://localhost:7891");
        fixture.Secrets.ReadCredentials(healthyAfter)!.Password.Should().Be("healthy-secret");
        var quarantined = await fixture.Db.ProxyProfiles.InSingleAsync(profile.Id);
        quarantined.IsEnabled.Should().BeFalse();
        quarantined.ConfigurationError.Should().Be("invalid_legacy_proxy_endpoint");
        quarantined.ProxyUrl.Should().Be(endpoint);
        quarantined.Revision.Should().Be(2);
        quarantined.ProtectedLegacyConfiguration.Should().NotBeNullOrEmpty().And.NotBe(original);
        // The exact rejected input is recoverable only with the host's key ring and profile-bound purpose.
        var provider = DataProtectionProvider.Create(new DirectoryInfo(fixture.KeyDirectory));
        provider.CreateProtector("AITool.ProxyProfile.Credentials.v1").CreateProtector(profile.Id.ToString("N"))
            .CreateProtector("legacy-configuration").Unprotect(quarantined.ProtectedLegacyConfiguration!).Should().Be(original);
        if (original.Contains("repair:"))
        {
            fixture.Secrets.ReadCredentials(quarantined)!.UserName.Should().Be("repair");
            fixture.Secrets.ReadCredentials(quarantined)!.Password.Should().Be("repair@secret");
        }
        var response = Json(await fixture.Controller.Get(profile.Id, default));
        response.GetProperty("data").GetProperty("configurationError").GetString().Should().Be("invalid_legacy_proxy_endpoint");
        response.GetRawText().Should().NotContain("secret").And.NotContain("repair%40").And.NotContain("protectedLegacyConfiguration");
        (await fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db)).Should().Be(0);
        (await fixture.Db.ProxyProfiles.InSingleAsync(profile.Id)).ProtectedLegacyConfiguration.Should().Be(quarantined.ProtectedLegacyConfiguration);
        var blocked = Json(await fixture.Controller.TestConnectivity(new TestProxyRequest { ProfileId = profile.Id }, default));
        blocked.GetProperty("errorCode").GetString().Should().Be("invalid_legacy_proxy_endpoint");

        (await fixture.Controller.Update(profile.Id, new ProxyProfilePayload
        {
            Name = "Repaired", ProxyUrl = "http://localhost:7890", IsEnabled = true, Revision = 2
        }, default)).Should().BeOfType<OkObjectResult>();
        var repaired = await fixture.Db.ProxyProfiles.InSingleAsync(profile.Id);
        repaired.ConfigurationError.Should().BeNull();
        repaired.IsEnabled.Should().BeTrue();
        repaired.Revision.Should().Be(3);
        repaired.ProtectedLegacyConfiguration.Should().Be(quarantined.ProtectedLegacyConfiguration);
    }

    [Fact]
    public async Task Migration_EmptyLegacyPayload_PreservesInvalidOriginalAndIsIdempotent()
    {
        using var fixture = new Fixture();
        const string original = "http://a:b@localhost:7101/invalid-path";
        var profile = new ProxyProfile { Key = "empty-payload", ProxyUrl = original, ProtectedLegacyConfiguration = "" };
        await fixture.Db.InsertAsync(profile);
        (await fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db)).Should().Be(1);
        var stored = await fixture.Db.ProxyProfiles.InSingleAsync(profile.Id);
        stored.ConfigurationError.Should().Be("invalid_legacy_proxy_endpoint");
        stored.IsEnabled.Should().BeFalse();
        stored.ProtectedLegacyConfiguration.Should().NotBeNullOrEmpty();
        var provider = DataProtectionProvider.Create(new DirectoryInfo(fixture.KeyDirectory));
        var recovered = provider.CreateProtector("AITool.ProxyProfile.Credentials.v1")
            .CreateProtector(profile.Id.ToString("N")).CreateProtector("legacy-configuration")
            .Unprotect(stored.ProtectedLegacyConfiguration!);
        (recovered == original).Should().BeTrue();
        (await fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db)).Should().Be(0);
        var repeated = await fixture.Db.ProxyProfiles.InSingleAsync(profile.Id);
        (repeated.ProtectedLegacyConfiguration == stored.ProtectedLegacyConfiguration).Should().BeTrue();
    }

    [Fact]
    public async Task Migration_InterruptedAfterFirstWrite_RollsBackAllRowsAndCanRetry()
    {
        using var fixture = new Fixture();
        var first = new ProxyProfile { Key = "interrupt-a", ProxyUrl = "http://a:b@localhost:7101" };
        var second = new ProxyProfile { Key = "interrupt-b", ProxyUrl = "http://c:d@localhost:7102" };
        await fixture.Db.InsertAsync(first);
        await fixture.Db.InsertAsync(second);
        // Abort the second update regardless of query ordering, after a real first write.
        fixture.Db.Client.Ado.ExecuteCommand("""
            CREATE TRIGGER abort_second_proxy_migration BEFORE UPDATE ON ProxyProfiles
            WHEN EXISTS (SELECT 1 FROM ProxyProfiles WHERE ProtectedUsername IS NOT NULL AND Id <> NEW.Id)
            BEGIN SELECT RAISE(ABORT, 'interrupted'); END;
            """);
        Func<Task> migrate = () => fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db);
        await migrate.Should().ThrowAsync<Exception>();
        var rows = await fixture.Db.ProxyProfiles.ToListAsync();
        rows.Should().HaveCount(2);
        rows.All(x => x.ProtectedUsername is null && x.ProtectedPassword is null && x.Revision == 1 && x.ProxyUrl.Contains('@'))
            .Should().BeTrue();
        fixture.Db.Client.Ado.ExecuteCommand("DROP TRIGGER abort_second_proxy_migration;");
        (await fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db)).Should().Be(2);
        (await fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db)).Should().Be(0);
    }

    [Fact]
    public async Task Migration_CancelledAfterFirstUpdate_RollsBack()
    {
        using var fixture = new Fixture();
        await fixture.Db.InsertAsync(new ProxyProfile { Key = "cancel-a", ProxyUrl = "http://a:b@localhost:7101" });
        await fixture.Db.InsertAsync(new ProxyProfile { Key = "cancel-b", ProxyUrl = "http://c:d@localhost:7102" });
        using var cancellation = new CancellationTokenSource();
        fixture.Db.Client.Aop.OnLogExecuted = (sql, _) =>
        {
            if (sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("ProxyProfiles", StringComparison.OrdinalIgnoreCase)
                && !sql.Contains("WHERE 1=0", StringComparison.OrdinalIgnoreCase))
                cancellation.Cancel();
        };
        Func<Task> migrate = () => fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db, cancellation.Token);
        await migrate.Should().ThrowAsync<OperationCanceledException>();
        var rows = await fixture.Db.ProxyProfiles.ToListAsync();
        rows.All(x => x.ProtectedUsername is null && x.Revision == 1 && x.ProxyUrl.Contains('@')).Should().BeTrue();
    }

    [Theory]
    [InlineData("credentials")]
    [InlineData("username")]
    [InlineData("password")]
    [InlineData("quarantine")]
    public async Task Migration_EmptyOrWrongRing_QuarantinesUnreadableRows_AndOtherRowsMigrate(string kind)
    {
        using var fixture = new Fixture();
        var protectedRow = new ProxyProfile { Key = "existing", ProxyUrl = "http://localhost:7101", IsEnabled = true };
        fixture.Secrets.TryApplyForSave(protectedRow, protectedRow.ProxyUrl, "Replace", "a", "b", out _).Should().BeTrue();
        if (kind == "username") protectedRow.ProtectedPassword = null;
        if (kind == "password") protectedRow.ProtectedUsername = null;
        if (kind == "quarantine")
        {
            protectedRow.ProtectedUsername = null;
            protectedRow.ProtectedPassword = null;
            protectedRow.ProxyUrl = "";
            protectedRow.IsEnabled = false;
            protectedRow.ConfigurationError = "invalid_legacy_proxy_endpoint";
            protectedRow.ProtectedLegacyConfiguration = DataProtectionProvider.Create(new DirectoryInfo(fixture.KeyDirectory))
                .CreateProtector("AITool.ProxyProfile.Credentials.v1").CreateProtector(protectedRow.Id.ToString("N"))
                .CreateProtector("legacy-configuration").Protect("invalid input");
        }
        var remaining = new ProxyProfile { Key = "remaining", ProxyUrl = "http://c:d@localhost:7102" };
        await fixture.Db.InsertAsync(protectedRow);
        await fixture.Db.InsertAsync(remaining);
        var emptyRing = Path.Combine(fixture.DirectoryPath, "empty-ring");
        Directory.CreateDirectory(emptyRing);
        var restarted = new ProxyProfileSecretService(DataProtectionProvider.Create(new DirectoryInfo(emptyRing)));
        // A lost/replaced key ring must not block startup: unreadable rows are quarantined
        // with their payloads untouched while other rows migrate normally under this ring.
        (await restarted.MigrateLegacyCredentialsAsync(fixture.Db)).Should().Be(1);
        var quarantined = await fixture.Db.ProxyProfiles.InSingleAsync(protectedRow.Id);
        quarantined.IsEnabled.Should().BeFalse();
        quarantined.ConfigurationError.Should().Be(ProxyProfileSecretService.UndecryptableReason);
        quarantined.ProtectedUsername.Should().Be(protectedRow.ProtectedUsername);
        quarantined.ProtectedPassword.Should().Be(protectedRow.ProtectedPassword);
        quarantined.ProtectedLegacyConfiguration.Should().Be(protectedRow.ProtectedLegacyConfiguration);
        var migrated = await fixture.Db.ProxyProfiles.InSingleAsync(remaining.Id);
        (migrated.ProtectedUsername is not null && migrated.Revision >= 1).Should().BeTrue();
        // Re-running under the same broken ring is an idempotent no-op.
        (await restarted.MigrateLegacyCredentialsAsync(fixture.Db)).Should().Be(0);
        quarantined.Revision.Should().Be((await fixture.Db.ProxyProfiles.InSingleAsync(protectedRow.Id)).Revision);
        // The original ring still reads the untouched payloads, but the explicit re-save via
        // the update endpoint remains the only path that clears the quarantine marker.
        var originalRing = new ProxyProfileSecretService(DataProtectionProvider.Create(new DirectoryInfo(fixture.KeyDirectory)));
        (await originalRing.MigrateLegacyCredentialsAsync(fixture.Db)).Should().Be(0);
        var preserved = await fixture.Db.ProxyProfiles.InSingleAsync(protectedRow.Id);
        preserved.ConfigurationError.Should().Be(ProxyProfileSecretService.UndecryptableReason);
        if (kind != "quarantine")
        {
            var username = preserved.ProtectedUsername is null ? null
                : DataProtectionProvider.Create(new DirectoryInfo(fixture.KeyDirectory))
                    .CreateProtector("AITool.ProxyProfile.Credentials.v1").CreateProtector(preserved.Id.ToString("N"))
                    .CreateProtector("username").Unprotect(preserved.ProtectedUsername);
            username.Should().Be(kind == "password" ? null : "a");
        }
    }

    [Fact]
    public async Task Migration_lost_ring_also_sanitizes_legacy_url_userinfo()
    {
        using var fixture = new Fixture();
        var profile = new ProxyProfile { Key = "mixed", Name = "Mixed", IsEnabled = true };
        fixture.Secrets.TryApplyForSave(profile, "http://localhost:7890", "Replace", "original", "encrypted", out _).Should().BeTrue();
        profile.ProxyUrl = "http://legacy-user:legacy-secret@localhost:7890";
        await fixture.Db.InsertAsync(profile);
        var otherRing = new ProxyProfileSecretService(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(fixture.DirectoryPath, "other-ring"))));
        await otherRing.MigrateLegacyCredentialsAsync(fixture.Db);
        var result = await fixture.Db.ProxyProfiles.InSingleAsync(profile.Id);
        result.IsEnabled.Should().BeFalse();
        result.ProxyUrl.Should().Be("http://localhost:7890");
        result.ProtectedUsername.Should().Be(profile.ProtectedUsername);
        result.ProtectedPassword.Should().Be(profile.ProtectedPassword);
        result.ProtectedLegacyConfiguration.Should().NotBeNullOrEmpty();
        Json(await fixture.Controller.Get(profile.Id, default)).GetRawText().Should().NotContain("legacy-secret");
        await otherRing.MigrateLegacyCredentialsAsync(fixture.Db);
        (await fixture.Db.ProxyProfiles.InSingleAsync(profile.Id)).ProxyUrl.Should().Be("http://localhost:7890");
    }
    [Fact]
    public async Task Migration_LostRing_QuarantinesProtectedProfilesInsteadOfBlockingStartup()
    {
        using var fixture = new Fixture();
        var profile = new ProxyProfile { Key = "lost-ring", Name = "Lost ring", IsEnabled = true };
        fixture.Secrets.TryApplyForSave(profile, "http://localhost:7890", "Replace", "user", "lost-secret", out _).Should().BeTrue();
        await fixture.Db.InsertAsync(profile);
        var healthy = new ProxyProfile { Key = "healthy", Name = "Healthy", ProxyUrl = "http://healthy:healthy-secret@localhost:7891" };
        await fixture.Db.InsertAsync(healthy);
        // A deployment restored without its Data Protection key ring: startup migration
        // quarantines the unreadable profile and proceeds instead of crash-looping.
        var otherRing = new ProxyProfileSecretService(DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(fixture.DirectoryPath, "other-ring"))));
        (await otherRing.MigrateLegacyCredentialsAsync(fixture.Db)).Should().Be(1, "the healthy legacy row still migrates");
        var quarantined = await fixture.Db.ProxyProfiles.InSingleAsync(profile.Id);
        quarantined.IsEnabled.Should().BeFalse();
        quarantined.ConfigurationError.Should().Be(ProxyProfileSecretService.UndecryptableReason);
        quarantined.ProxyUrl.Should().Be("http://localhost:7890");
        quarantined.ProtectedUsername.Should().Be(profile.ProtectedUsername);
        quarantined.ProtectedPassword.Should().Be(profile.ProtectedPassword);
        quarantined.Revision.Should().Be(2, "TryApplyForSave already set Revision 1 and quarantine bumps it once");
        var healthyAfter = await fixture.Db.ProxyProfiles.InSingleAsync(healthy.Id);
        healthyAfter.IsEnabled.Should().BeTrue();
        healthyAfter.ConfigurationError.Should().BeNull();
        otherRing.ReadCredentials(healthyAfter)!.Password.Should().Be("healthy-secret");
        // Re-running is an idempotent no-op, and the API still hides the quarantined payloads.
        (await otherRing.MigrateLegacyCredentialsAsync(fixture.Db)).Should().Be(0);
        var response = Json(await fixture.Controller.Get(profile.Id, default));
        response.GetRawText().Should().NotContain("lost-secret");
    }

    [Theory]
    [InlineData("username")]
    [InlineData("password")]
    [InlineData("quarantine")]
    public async Task Migration_CorruptExistingField_QuarantinesRow_AndOtherRowsMigrate(string field)
    {
        using var fixture = new Fixture();
        var existing = new ProxyProfile { Key = "corrupt", ProxyUrl = "http://localhost:7101", IsEnabled = true };
        fixture.Secrets.TryApplyForSave(existing, existing.ProxyUrl, "Replace", "a", "b", out _).Should().BeTrue();
        if (field == "username") existing.ProtectedUsername = "invalid";
        if (field == "password") existing.ProtectedPassword = "invalid";
        if (field == "quarantine") existing.ProtectedLegacyConfiguration = "invalid";
        var remaining = new ProxyProfile { Key = "remaining", ProxyUrl = "http://c:d@localhost:7102" };
        await fixture.Db.InsertAsync(existing);
        await fixture.Db.InsertAsync(remaining);
        // Corrupt stored payloads quarantine their own row (payloads preserved untouched)
        // instead of preventing the host — and every unrelated row — from starting.
        (await fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db)).Should().Be(1);
        var quarantined = await fixture.Db.ProxyProfiles.InSingleAsync(existing.Id);
        quarantined.IsEnabled.Should().BeFalse();
        quarantined.ConfigurationError.Should().Be(ProxyProfileSecretService.UndecryptableReason);
        quarantined.ProtectedUsername.Should().Be(existing.ProtectedUsername);
        quarantined.ProtectedPassword.Should().Be(existing.ProtectedPassword);
        quarantined.ProtectedLegacyConfiguration.Should().Be(existing.ProtectedLegacyConfiguration);
        var migrated = await fixture.Db.ProxyProfiles.InSingleAsync(remaining.Id);
        (migrated.ProtectedUsername is not null && fixture.Secrets.ReadCredentials(migrated) is not null).Should().BeTrue();
        (await fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db)).Should().Be(0);
    }

    [Fact]
    public async Task Migration_ConcurrentMigrators_SerializeAndAreIdempotent()
    {
        using var fixture = new Fixture();
        await fixture.Db.InsertAsync(new ProxyProfile { Key = "race-a", ProxyUrl = "http://a:b@localhost:7101" });
        await fixture.Db.InsertAsync(new ProxyProfile { Key = "race-b", ProxyUrl = "http://c:d@localhost:7102" });
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            return await fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db);
        })).ToArray();
        start.Set();
        var counts = await Task.WhenAll(tasks);
        counts.OrderBy(x => x).Should().Equal(0, 2);
        var rows = await fixture.Db.ProxyProfiles.ToListAsync();
        rows.All(x => x.Revision == 2 && fixture.Secrets.ReadCredentials(x) is not null).Should().BeTrue();
    }

    [Fact]
    public async Task Migration_CancellationWhileWaitingForWriter_CompletesBeforeWriterReleases()
    {
        using var fixture = new Fixture();
        var profile = new ProxyProfile { Key = "locked", ProxyUrl = "http://a:b@localhost:7101" };
        await fixture.Db.InsertAsync(profile);
        using var writer = fixture.Db.Client.CopyNew();
        writer.Ado.BeginTran();
        using var attempted = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
        {
            if (sql.TrimStart().StartsWith("BEGIN IMMEDIATE", StringComparison.OrdinalIgnoreCase)) attempted.Set();
        };
        fixture.Db.Client.Aop.CheckConnectionExecuted = (connection, _) =>
        {
            var sqlite = (Microsoft.Data.Sqlite.SqliteConnection)connection;
            SQLitePCL.raw.sqlite3_trace(sqlite.Handle!, trace, null);
        };
        Task<int>? migration = null;
        try
        {
            migration = Task.Run(() => fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db, cancellation.Token));
            attempted.Wait(TimeSpan.FromSeconds(3)).Should().BeTrue();
            cancellation.Cancel();
            var completed = await Task.WhenAny(migration, Task.Delay(TimeSpan.FromSeconds(2)));
            completed.Should().BeSameAs(migration, "cancellation must not wait for the writer to release its lock");
            Func<Task> completion = async () => await migration;
            await completion.Should().ThrowAsync<OperationCanceledException>();
            // The original transaction still owns the writer at the point cancellation is observed.
            writer.Ado.RollbackTran();
            var stored = await fixture.Db.ProxyProfiles.InSingleAsync(profile.Id);
            (stored.ProxyUrl == profile.ProxyUrl && stored.ProtectedUsername is null && stored.Revision == 1).Should().BeTrue();
            (await fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db)).Should().Be(1);
        }
        finally
        {
            try { writer.Ado.RollbackTran(); } catch { }
            if (migration is not null) { try { await migration; } catch { } }
            GC.KeepAlive(trace);
        }
    }

    [Fact]
    public async Task Migration_TransactionCommand_NonLockErrorFailsImmediatelyWithoutSqlText()
    {
        using var fixture = new Fixture(sharedCache: true);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(fixture.Db.Client.CurrentConnectionConfig.ConnectionString);
        connection.Open();
        SQLitePCL.raw.sqlite3_busy_timeout(connection.Handle!, 0);
        var method = typeof(ProxyProfileSecretService).GetMethod("ExecuteSqliteTransactionCommandAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var task = (Task)method.Invoke(null, new object[]
        {
            connection.Handle!, "not_valid_sql secret_marker", "Safe error.", CancellationToken.None
        })!;
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
        completed.Should().BeSameAs(task, "a non-lock SQLite error must not be retried until the deadline");
        Func<Task> completion = async () => await task;
        var failure = await completion.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Be($"Safe error. SQLite result code: {SQLitePCL.raw.SQLITE_ERROR}.");
    }

    [Theory]
    [InlineData("release")]
    [InlineData("cancel")]
    [InlineData("deadline")]
    public async Task Migration_SharedCacheWriterContention_RetriesUntilReleasedCancelledOrTimedOut(string outcome)
    {
        using var fixture = new Fixture(sharedCache: true);
        var profile = new ProxyProfile { Key = "shared", ProxyUrl = "http://a:b@localhost:7101" };
        await fixture.Db.InsertAsync(profile);
        using var writer = fixture.Db.Client.CopyNew();
        writer.Ado.BeginTran();
        // Prove that this is SQLite's shared-cache LOCKED path, not just ordinary BUSY.
        using (var probe = new Microsoft.Data.Sqlite.SqliteConnection(fixture.Db.Client.CurrentConnectionConfig.ConnectionString))
        {
            probe.Open();
            SQLitePCL.raw.sqlite3_busy_timeout(probe.Handle!, 0);
            var result = SQLitePCL.raw.sqlite3_exec(probe.Handle!, "BEGIN IMMEDIATE;");
            if (result == SQLitePCL.raw.SQLITE_OK) SQLitePCL.raw.sqlite3_exec(probe.Handle!, "ROLLBACK;");
            (result & 0xff).Should().Be(SQLitePCL.raw.SQLITE_LOCKED);
        }
        var attempts = 0;
        var retried = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
        {
            if (sql.TrimStart().StartsWith("BEGIN IMMEDIATE", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref attempts) >= 2) retried.TrySetResult(true);
        };
        fixture.Db.Client.Aop.CheckConnectionExecuted = (connection, _) =>
        {
            var sqlite = (Microsoft.Data.Sqlite.SqliteConnection)connection;
            SQLitePCL.raw.sqlite3_trace(sqlite.Handle!, trace, null);
        };
        using var cancellation = new CancellationTokenSource();
        Task<int>? migration = null;
        var released = false;
        try
        {
            migration = Task.Run(() => fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db, cancellation.Token));
            await retried.Task.WaitAsync(TimeSpan.FromSeconds(3));
            migration.IsCompleted.Should().BeFalse();
            if (outcome == "release")
            {
                writer.Ado.CommitTran();
                released = true;
                (await migration.WaitAsync(TimeSpan.FromSeconds(3))).Should().Be(1);
            }
            else
            {
                if (outcome == "cancel") cancellation.Cancel();
                var completed = await Task.WhenAny(migration, Task.Delay(TimeSpan.FromSeconds(outcome == "cancel" ? 2 : 8)));
                completed.Should().BeSameAs(migration, "the migration must finish while the other writer still owns the lock");
                Func<Task> completion = async () => await migration;
                if (outcome == "cancel")
                    await completion.Should().ThrowAsync<OperationCanceledException>();
                else
                    await completion.Should().ThrowAsync<InvalidOperationException>()
                        .WithMessage("Could not acquire the SQLite writer for proxy profile migration. SQLite result code: *.");
                writer.Ado.RollbackTran();
                released = true;
                var unchanged = await fixture.Db.ProxyProfiles.InSingleAsync(profile.Id);
                (unchanged.ProxyUrl == profile.ProxyUrl && unchanged.ProtectedUsername is null && unchanged.Revision == 1).Should().BeTrue();
                (await fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db)).Should().Be(1);
            }
            var migrated = await fixture.Db.ProxyProfiles.InSingleAsync(profile.Id);
            (migrated.Revision == 2 && fixture.Secrets.ReadCredentials(migrated) is not null).Should().BeTrue();
        }
        finally
        {
            if (!released) { try { writer.Ado.RollbackTran(); } catch { } }
            cancellation.Cancel();
            if (migration is not null) { try { await migration; } catch { } }
            GC.KeepAlive(trace);
        }
    }

    [Fact]
    public async Task Migration_WaitsForConcurrentWriter_ThenPreflightsItsCommittedState()
    {
        using var fixture = new Fixture();
        var profile = new ProxyProfile { Key = "concurrent", ProxyUrl = "http://a:b@localhost:7101" };
        await fixture.Db.InsertAsync(profile);
        var otherRing = Path.Combine(fixture.DirectoryPath, "other-ring");
        Directory.CreateDirectory(otherRing);
        var other = new ProxyProfileSecretService(DataProtectionProvider.Create(new DirectoryInfo(otherRing)));
        var concurrent = new ProxyProfile { Id = profile.Id, Key = profile.Key, ProxyUrl = "http://localhost:7101" };
        other.TryApplyForSave(concurrent, concurrent.ProxyUrl, "Replace", "c", "d", out _).Should().BeTrue();
        using var writer = fixture.Db.Client.CopyNew();
        writer.Ado.BeginTran();
        using var attempted = new ManualResetEventSlim();
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
        {
            if (sql.TrimStart().StartsWith("BEGIN IMMEDIATE", StringComparison.OrdinalIgnoreCase)) attempted.Set();
        };
        fixture.Db.Client.Aop.CheckConnectionExecuted = (connection, _) =>
        {
            var sqlite = (Microsoft.Data.Sqlite.SqliteConnection)connection;
            SQLitePCL.raw.sqlite3_trace(sqlite.Handle!, trace, null);
        };
        Task<int>? migration = null;
        try
        {
        await writer.Updateable(concurrent).Where(x => x.Id == profile.Id).ExecuteCommandAsync();
        migration = Task.Run(() => fixture.Secrets.MigrateLegacyCredentialsAsync(fixture.Db));
        attempted.Wait(TimeSpan.FromSeconds(3)).Should().BeTrue();
        writer.Ado.CommitTran();
        // The preflight still reads the COMMITTED state after the writer releases. The
        // concurrently written payloads are unreadable under this ring, so the row is
        // quarantined untouched (never re-protected, never blocking startup) instead of
        // throwing and abandoning the whole migration.
        (await migration).Should().Be(0);
        var stored = await fixture.Db.ProxyProfiles.InSingleAsync(profile.Id);
        (stored.ProtectedUsername == concurrent.ProtectedUsername && stored.ProxyUrl == concurrent.ProxyUrl).Should().BeTrue();
        stored.IsEnabled.Should().BeFalse();
        stored.ConfigurationError.Should().Be(ProxyProfileSecretService.UndecryptableReason);
        }
        finally
        {
            // Release a blocked contender even if an assertion fails.
            try { writer.Ado.RollbackTran(); } catch { }
            if (migration is not null) { try { await migration; } catch { } }
            GC.KeepAlive(trace);
        }
    }

    [Fact]
    public void Protection_IsBoundToProfileId_AndTamperedOrPartialSecretsFailClosed()
    {
        using var fixture = new Fixture();
        var profile = new ProxyProfile();
        fixture.Secrets.TryApplyForSave(profile, "http://localhost:7890", "Replace", "user", "secret", out _).Should().BeTrue();
        var originalId = profile.Id;
        profile.Id = Guid.NewGuid();
        Action moved = () => fixture.Secrets.ReadCredentials(profile);
        moved.Should().Throw<CryptographicException>();
        profile.Id = originalId;
        profile.ProtectedPassword = "tampered-secret";
        Action tampered = () => fixture.Secrets.ReadCredentials(profile);
        tampered.Should().Throw<CryptographicException>().WithMessage("Proxy credentials could not be decrypted.");
        profile.ProtectedPassword = null;
        Action partial = () => fixture.Secrets.ReadCredentials(profile);
        partial.Should().Throw<CryptographicException>();
        var serialized = JsonSerializer.Serialize(profile);
        serialized.Should().NotContain("ProtectedUsername").And.NotContain("ProtectedPassword");
    }

    [Fact]
    public async Task Delete_ReferencedProfileReturns409AndReferences_ThenSucceedsAfterUnbinding()
    {
        using var fixture = new Fixture();
        var profile = new ProxyProfile { Key = "referenced", Name = "Referenced", ProxyUrl = "http://localhost:7890" };
        await fixture.Db.InsertAsync(profile);
        var accountId = Guid.NewGuid();
        var policy = new AccountNetworkPolicy
        {
            ProviderKey = "codex", AccountId = accountId, Mode = NetworkMode.ProxyProfile,
            ProxyProfileId = profile.Id, Revision = Guid.NewGuid().ToString("D")
        };
        await fixture.Db.InsertAsync(policy);
        var blocked = await fixture.Controller.Delete(profile.Id, default);
        blocked.Should().BeOfType<ConflictObjectResult>();
        var json = Json(blocked);
        json.GetProperty("errorCode").GetString().Should().Be("proxy_profile_in_use");
        json.GetProperty("data").GetProperty("references")[0].GetProperty("accountId").GetGuid().Should().Be(accountId);
        (await fixture.Db.ProxyProfiles.InSingleAsync(profile.Id)).Should().NotBeNull();
        await fixture.Db.DeleteAsync(policy);
        (await fixture.Controller.Delete(profile.Id, default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public void ProbeHandler_UsesExplicitNetworkCredentialAndNormalTlsVerification()
    {
        var credential = new NetworkCredential("username", "secret");
        using var handler = ProxyProfileSecretService.CreateProxyHandler("socks5://localhost:1080", credential);
        var proxy = handler.Proxy.Should().BeOfType<WebProxy>().Which;
        proxy.Address!.UserInfo.Should().BeEmpty();
        proxy.Credentials.Should().BeSameAs(credential);
        handler.UseProxy.Should().BeTrue();
        handler.UseDefaultCredentials.Should().BeFalse();
        handler.ServerCertificateCustomValidationCallback.Should().BeNull();
        handler.AllowAutoRedirect.Should().BeFalse();
        Action embedded = () => ProxyProfileSecretService.CreateProxyHandler("http://username:secret@localhost:8080");
        embedded.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(204, true)]
    [InlineData(302, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(407, false)]
    public async Task Probe_ByProfileIdUsesStoredCredentials_AndOnly2xxIsSuccess(int status, bool success)
    {
        using var fixture = new Fixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var expected = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("probe-user:probe-secret"));
        var server = ServeProxyAsync(listener, expected, status, timeout.Token);
        try
        {
            var profile = new ProxyProfile { Key = "authenticated-probe", Name = "Authenticated probe" };
            fixture.Secrets.TryApplyForSave(profile, $"http://127.0.0.1:{port}", "Replace", "probe-user", "probe-secret", out _).Should().BeTrue();
            await fixture.Db.InsertAsync(profile, timeout.Token);
            var result = Json(await fixture.Controller.TestConnectivity(new TestProxyRequest
            {
                ProfileId = profile.Id,
                // Deliberately invalid URL: stable profile identity must take precedence.
                ProxyUrl = "ignored", TargetUrl = "http://target.invalid/probe"
            }, timeout.Token));
            var data = result.GetProperty("data");
            data.GetProperty("isSuccess").GetBoolean().Should().Be(success);
            data.GetProperty("httpResponseReceived").GetBoolean().Should().BeTrue();
            data.GetProperty("statusCode").GetInt32().Should().Be(status);
            result.GetRawText().Should().NotContain("probe-secret").And.NotContain("probe-user");
            var request = await server.WaitAsync(timeout.Token);
            request.Should().ContainEquivalentOf(expected);
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            try { await server; } catch (OperationCanceledException) { } catch (SocketException) { }
        }
    }

    private static async Task<string> ServeProxyAsync(TcpListener listener, string expectedAuthorization, int status, CancellationToken ct)
    {
        // Fake proxy only: never forwards or resolves the target host. Basic authentication
        // may require an initial challenge and a new connection, both handled locally.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var connection = await listener.AcceptTcpClientAsync(ct);
            await using var stream = connection.GetStream();
            var header = new StringBuilder();
            var buffer = new byte[1];
            while (header.Length < 16384 && !header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(buffer, ct) == 0) break;
                header.Append((char)buffer[0]);
            }
            var request = header.ToString();
            var authenticated = request.Contains(expectedAuthorization, StringComparison.OrdinalIgnoreCase);
            var returnedStatus = authenticated ? status : 407;
            var challenge = returnedStatus == 407 ? "Proxy-Authenticate: Basic realm=\"test\"\r\n" : string.Empty;
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {returnedStatus} Test\r\n{challenge}Content-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, ct);
            await stream.FlushAsync(ct);
            if (authenticated) return request;
        }
        throw new InvalidOperationException("The local fake proxy never received the expected authentication.");
    }

    [Fact]
    public async Task Probe_RejectsDisabledProfileAndTargetUserinfoBeforeAnyNetworkCall()
    {
        using var fixture = new Fixture();
        await fixture.Db.InsertAsync(new ProxyProfile
        {
            Key = "disabled", Name = "Disabled", ProxyUrl = "http://localhost:7890", IsEnabled = false
        });
        Json(await fixture.Controller.TestConnectivity(new TestProxyRequest { ProxyUrl = "disabled" }, default))
            .GetProperty("errorCode").GetString().Should().Be("proxy_disabled");
        var target = await fixture.Controller.TestConnectivity(new TestProxyRequest
        {
            ProxyUrl = "http://localhost:7890", TargetUrl = "https://user:secret@example.invalid"
        }, default);
        target.Should().BeOfType<BadRequestObjectResult>();
        Json(target).GetRawText().Should().NotContain("secret");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static JsonElement Json(IActionResult result) => JsonSerializer.SerializeToElement(
        result.Should().BeAssignableTo<ObjectResult>().Which.Value, JsonOptions);

    private static void AssertSafe(JsonElement data)
    {
        data.GetProperty("proxyUrl").GetString().Should().Be("http://127.0.0.1:7890");
        foreach (var name in new[] { "username", "password", "protectedUsername", "protectedPassword" })
            data.TryGetProperty(name, out _).Should().BeFalse();
        data.GetRawText().Should().NotContain("alice").And.NotContain("secret").And.NotContain("p%40ss");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "aitool-proxy-secrets-" + Guid.NewGuid().ToString("N"));
        private readonly SqlSugarClient _client;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        public AppDbContext Db { get; }
        public ProxyProfileSecretService Secrets { get; }
        public ProxyProfilesApiController Controller { get; }
        public string DirectoryPath => _directory;
        public string KeyDirectory => Path.Combine(_directory, "keyring");

        public Fixture(bool sharedCache = false)
        {
            Directory.CreateDirectory(_directory);
            Directory.CreateDirectory(KeyDirectory);
            _client = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(_directory, "test.db"),
                    Cache = sharedCache ? Microsoft.Data.Sqlite.SqliteCacheMode.Shared : Microsoft.Data.Sqlite.SqliteCacheMode.Default
                }.ToString(),
                DbType = DbType.Sqlite, IsAutoCloseConnection = true, InitKeyType = InitKeyType.Attribute
            });
            _client.CodeFirst.InitTables<ProxyProfile, AccountNetworkPolicy>();
            Db = new AppDbContext(_client, _semaphore);
            Secrets = new ProxyProfileSecretService(DataProtectionProvider.Create(new DirectoryInfo(KeyDirectory)));
            Controller = new ProxyProfilesApiController(Db, new EnabledSettings(), Secrets);
        }

        public void Dispose()
        {
            _client.Dispose();
            _semaphore.Dispose();
            // SQLite pooling can temporarily retain a handle on Windows; never touch non-test paths.
            try { Directory.Delete(_directory, true); } catch (IOException) { }
        }
    }

    private sealed class EnabledSettings : ISystemRuntimeSettingsService
    {
        public Task<SystemRuntimeSettings> GetOrCreateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemRuntimeSettings { Id = 1, DeveloperFeaturesEnabled = true, DeveloperProxyProfilesEnabled = true });
        public Task<SystemRuntimeSettings> UpdateAsync(UpdateSystemRuntimeSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ClearUsageLogsAsync(ClearUsageLogsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
