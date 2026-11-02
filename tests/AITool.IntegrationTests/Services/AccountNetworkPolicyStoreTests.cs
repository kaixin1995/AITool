using AITool.Application.Networking;
using AITool.Application.Operations;
using AITool.Infrastructure.Operations;
using AITool.Web.Controllers.Admin;
using Microsoft.AspNetCore.Mvc;
using AITool.Domain.Codex;
using AITool.Domain.Google;
using AITool.Domain.Kimi;
using AITool.Domain.OAuth;
using AITool.Domain.Operations;
using AITool.Domain.Sites;
using AITool.Domain.Xai;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;

namespace AITool.IntegrationTests.Services;

/// <summary>Independent temporary SQLite databases; no production database or network access.</summary>
public sealed class AccountNetworkPolicyStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aitool-network-policy-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly ISqlSugarClient _client;
    private readonly Clock _clock = new();
    private readonly ProxyProfileSecretService _secrets = new(new EphemeralDataProtectionProvider());
    private readonly AccountNetworkPolicyStore _store;

    public AccountNetworkPolicyStoreTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_path};Pooling=False");
        _services = services.BuildServiceProvider();
        _client = _services.GetRequiredService<ISqlSugarClient>();
        _client.CodeFirst.InitTables(typeof(AccountNetworkPolicy), typeof(ProxyProfile), typeof(SystemRuntimeSettings),
            typeof(CodexAccount), typeof(GoogleAccount), typeof(KimiAccount), typeof(XaiAccount), typeof(DeviceOAuthAccount));
        _client.Insertable(new SystemRuntimeSettings { Id = 1, DeveloperProxyProfilesEnabled = true }).ExecuteCommand();
        _store = NewStore();
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    [InlineData("device-oauth")]
    public async Task All_provider_tables_support_legacy_direct_and_delete_without_touching_credentials(string provider)
    {
        var account = AddAccount(provider);
        var initial = await _store.GetAsync(account);
        initial.Selection.Mode.Should().Be(NetworkMode.Legacy);
        initial.Revision.Should().BeNull();
        (await _store.ResolveForAccountAsync(account)).Mode.Should().Be(NetworkMode.Legacy);
        var saved = await _store.SetAsync(account, new(NetworkMode.Direct));
        Guid.TryParse(saved.Revision, out _).Should().BeTrue();
        saved.UpdatedAtUtcTicks.Should().Be(_clock.GetUtcNow().UtcTicks);
        (await NewStore().GetAsync(account)).Should().Be(saved, "state survives store recreation");
        (await _store.ResolveForAccountAsync(account)).Mode.Should().Be(NetworkMode.Direct);
        (await _store.ResolveForAccountAsync(account)).Credentials.Should().BeNull();
        await _store.DeleteAsync(account, saved.Revision);
        (await _store.GetAsync(account)).Selection.Mode.Should().Be(NetworkMode.Legacy);
        var table = provider switch { "codex" => "CodexAccounts", "google" => "GoogleAccounts", "kimi" => "KimiAccounts", "xai" => "XaiAccounts", _ => "DeviceOAuthAccounts" };
        _client.Ado.GetString($"SELECT AccessToken FROM {table} WHERE Id=@id", new SugarParameter("@id", account.AccountId))
            .Should().Be("account-secret");
    }

    [Fact]
    public async Task Guid_account_id_is_scoped_by_provider_and_explicit_legacy_can_be_persisted()
    {
        var id = Guid.NewGuid();
        var first = AddAccount("codex", id);
        var second = AddAccount("google", id);
        await _store.SetAsync(first, new(NetworkMode.Direct));
        var legacy = await _store.SetAsync(second, new(NetworkMode.Legacy));
        legacy.Revision.Should().NotBeNull();
        (await _store.GetAsync(first)).Selection.Mode.Should().Be(NetworkMode.Direct);
        (await _store.GetAsync(second)).Selection.Mode.Should().Be(NetworkMode.Legacy);
        _client.Queryable<AccountNetworkPolicy>().Count().Should().Be(2);
    }

    [Fact]
    public async Task Missing_accounts_are_errors_for_reads_writes_deletes_and_resolution()
    {
        var missing = new AccountIdentity("codex", Guid.NewGuid());
        await Fails(() => _store.GetAsync(missing), "account_not_found");
        await Fails(() => _store.SetAsync(missing, new(NetworkMode.Direct)), "account_not_found");
        await Fails(() => _store.DeleteAsync(missing), "account_not_found");
        await Fails(() => _store.ResolveForAccountAsync(missing), "account_not_found");
        _client.Queryable<AccountNetworkPolicy>().Count().Should().Be(0);
    }

    [Theory]
    [InlineData("kimi")]
    [InlineData("xai")]
    [InlineData("device-oauth")]
    public async Task Soft_deleted_accounts_cannot_gain_or_read_a_policy(string provider)
    {
        var account = AddAccount(provider, deleted: true);
        await Fails(() => _store.GetAsync(account), "account_not_found");
        await Fails(() => _store.SetAsync(account, new(NetworkMode.Direct)), "account_not_found");
    }

    [Fact]
    public async Task Revision_conflicts_rollback_and_each_success_renews_revision_with_UTC_ticks()
    {
        var account = AddAccount("codex");
        var first = await _store.SetAsync(account, new(NetworkMode.Direct));
        _clock.Now = _clock.Now.AddMinutes(1);
        var second = await _store.SetAsync(account, new(NetworkMode.Legacy), first.Revision);
        second.Revision.Should().NotBe(first.Revision);
        second.UpdatedAtUtcTicks.Should().Be(_clock.Now.UtcTicks);
        var row = _client.Queryable<AccountNetworkPolicy>().ToList().Single();
        row.CreatedAtUtcTicks.Should().Be(first.UpdatedAtUtcTicks);
        await Fails(() => _store.SetAsync(account, new(NetworkMode.Direct), first.Revision), "account_network_revision_conflict");
        await Fails(() => _store.DeleteAsync(account, first.Revision), "account_network_revision_conflict");
        (await _store.GetAsync(account)).Should().Be(second);
        await _store.DeleteAsync(account, second.Revision);
        await Fails(() => _store.SetAsync(account, new(NetworkMode.Direct), second.Revision), "account_network_revision_conflict");
    }

    [Fact]
    public async Task Profile_binding_uses_stable_id_and_reads_current_endpoint_revision_and_protected_credentials()
    {
        var account = AddAccount("google");
        var profile = AddProfile();
        _secrets.TryApplyForSave(profile, profile.ProxyUrl, "Replace", "proxy-user", "proxy-secret", out _).Should().BeTrue();
        _client.Updateable(profile).ExecuteCommand();
        await _store.SetAsync(account, new(NetworkMode.ProxyProfile, profile.Id));
        var resolved = await _store.ResolveForAccountAsync(account);
        resolved.ProxyProfileId.Should().Be(profile.Id);
        resolved.Credentials!.UserName.Should().Be("proxy-user");
        resolved.Credentials.Password.Should().Be("proxy-secret");
        resolved.ProxyEndpoint!.UserInfo.Should().BeEmpty();
        resolved.ToString().Should().NotContain("proxy-secret");
        profile.Key = "renamed";
        profile.ProxyUrl = "socks5://127.0.0.1:12345";
        profile.Revision++;
        _client.Updateable(profile).ExecuteCommand();
        var current = await _store.ResolveForAccountAsync(account);
        current.ProxyEndpoint!.AbsoluteUri.Should().Be(new Uri(profile.ProxyUrl).AbsoluteUri);
        current.ProfileRevision.Should().Be(profile.Revision);
    }

    [Theory]
    [InlineData("missing", "proxy_profile_not_found")]
    [InlineData("disabled", "proxy_profile_disabled")]
    [InlineData("global", "proxy_profiles_disabled")]
    [InlineData("invalid", "invalid_proxy_profile")]
    [InlineData("userinfo", "invalid_proxy_profile")]
    [InlineData("corrupt", "proxy_credentials_unavailable")]
    public async Task Bound_profiles_fail_closed_after_change_and_invalid_rebinding_rolls_back(string failure, string code)
    {
        var account = AddAccount("kimi");
        var profile = AddProfile();
        var saved = await _store.SetAsync(account, new(NetworkMode.ProxyProfile, profile.Id));
        switch (failure)
        {
            case "missing": _client.Deleteable<ProxyProfile>().Where(p => p.Id == profile.Id).ExecuteCommand(); break;
            case "disabled": profile.IsEnabled = false; _client.Updateable(profile).ExecuteCommand(); break;
            case "global": _client.Updateable<SystemRuntimeSettings>().SetColumns(s => s.DeveloperProxyProfilesEnabled == false).Where(s => s.Id == 1).ExecuteCommand(); break;
            case "invalid": profile.ProxyUrl = "file:///private/secret"; _client.Updateable(profile).ExecuteCommand(); break;
            case "userinfo": profile.ProxyUrl = "http://private:raw-secret@localhost:8888"; _client.Updateable(profile).ExecuteCommand(); break;
            case "corrupt": profile.ProtectedUsername = "corrupt-private-value"; profile.ProtectedPassword = "corrupt-secret-value"; _client.Updateable(profile).ExecuteCommand(); break;
        }
        var error = await Fails(() => _store.ResolveForAccountAsync(account), code);
        error.ToString().Should().NotContain("raw-secret").And.NotContain("corrupt-secret-value");
        error.InnerException.Should().BeNull();
        await Fails(() => _store.SetAsync(account, new(NetworkMode.ProxyProfile, profile.Id), saved.Revision), code);
        (await _store.GetAsync(account)).Should().Be(saved, "failed validation does not alter the policy");
    }

    [Fact]
    public async Task Operation_context_enforces_profile_revision_and_can_resolve_pre_account_login_selection()
    {
        var profile = AddProfile();
        var selection = new AccountNetworkSelection(NetworkMode.ProxyProfile, profile.Id);
        var context = new NetworkOperationContext(null, "login-operation", selection, profile.Revision);
        (await _store.ResolveAsync(context)).ProxyProfileId.Should().Be(profile.Id);
        profile.Revision++;
        _client.Updateable(profile).ExecuteCommand();
        await Fails(() => _store.ResolveAsync(context), "proxy_profile_revision_conflict");
        (await _store.ResolveAsync(new(null, "separate-operation", new(NetworkMode.Direct)))).Mode.Should().Be(NetworkMode.Direct);
    }

    [Theory]
    [InlineData(NetworkMode.ProxyProfile, false)]
    [InlineData(NetworkMode.Direct, true)]
    [InlineData(NetworkMode.Legacy, true)]
    [InlineData((NetworkMode)99, false)]
    public async Task Invalid_selections_do_not_write(NetworkMode mode, bool hasId)
    {
        var account = AddAccount("xai");
        await Fails(() => _store.SetAsync(account, new(mode, hasId ? Guid.NewGuid() : null)), "invalid_network_selection");
        _client.Queryable<AccountNetworkPolicy>().Count().Should().Be(0);
    }

    [Fact]
    public async Task Linked_site_batch_resolves_all_providers_ignores_empty_missing_deleted_and_rejects_ambiguity()
    {
        var expected = new Dictionary<Guid, AccountIdentity>();
        foreach (var provider in new[] { "codex", "google", "kimi", "xai", "device-oauth" })
        {
            var site = Guid.NewGuid();
            expected.Add(site, AddAccount(provider, linkedSiteId: site));
        }
        var deletedSite = Guid.NewGuid();
        AddAccount("device-oauth", linkedSiteId: deletedSite, deleted: true);
        AddAccount("device-oauth", linkedSiteId: Guid.Empty);
        AddAccount("device-oauth", linkedSiteId: Guid.Empty);
        var requested = expected.Keys.Concat(new[] { Guid.Empty, deletedSite, Guid.NewGuid() }).ToArray();
        (await _store.GetLinkedSiteOwnersAsync(requested)).Should().BeEquivalentTo(expected);
        AddAccount("google", linkedSiteId: expected.Keys.First());
        await Fails(() => _store.GetLinkedSiteOwnersAsync(requested), "ambiguous_account_owner");
    }

    [Fact]
    public async Task Cancellation_does_not_write()
    {
        var account = AddAccount("codex");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var action = () => _store.SetAsync(account, new(NetworkMode.Direct), cancellationToken: cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        _client.Queryable<AccountNetworkPolicy>().Count().Should().Be(0);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    [InlineData("device-oauth")]
    public async Task Caller_transaction_can_rollback_new_account_and_policy_together(string provider)
    {
        var account = new AccountIdentity(provider, Guid.NewGuid());
        using var client = _client.CopyNew();
        client.Ado.BeginTran();
        try
        {
            await client.Ado.ExecuteCommandAsync("UPDATE AccountNetworkPolicies SET Revision=Revision WHERE 1=0;");
            InsertAccount(client, account, Guid.NewGuid(), false);
            var state = await _store.SetInTransactionAsync(client, account, new(NetworkMode.Direct));
            state.Selection.Mode.Should().Be(NetworkMode.Direct);
            client.Queryable<AccountNetworkPolicy>().Count().Should().Be(1,
                "the hook must write on the supplied connection and see its uncommitted account");
        }
        finally
        {
            client.Ado.RollbackTran();
        }
        _client.Queryable<AccountNetworkPolicy>().Count().Should().Be(0);
        await Fails(() => _store.GetAsync(account), "account_not_found");
    }

    [Fact]
    public async Task Caller_can_commit_new_account_and_policy_then_rollback_policy_replacement()
    {
        var account = new AccountIdentity("device-oauth", Guid.NewGuid());
        var profile = AddProfile();
        var selection = new AccountNetworkSelection(NetworkMode.ProxyProfile, profile.Id);
        var frozen = await _store.ResolveAsync(new(null, "login", selection));
        AccountNetworkPolicyState saved;
        using (var client = _client.CopyNew())
        {
            client.Ado.BeginTran();
            try
            {
                await client.Ado.ExecuteCommandAsync("UPDATE AccountNetworkPolicies SET Revision=Revision WHERE 1=0;");
                InsertAccount(client, account, Guid.NewGuid(), false);
                saved = await _store.SetInTransactionAsync(client, account, selection,
                    expectedProfileRevision: frozen.ProfileRevision);
                client.Ado.CommitTran();
            }
            catch
            {
                client.Ado.RollbackTran();
                throw;
            }
        }
        (await _store.GetAsync(account)).Should().Be(saved);
        using (var client = _client.CopyNew())
        {
            client.Ado.BeginTran();
            try
            {
                await client.Ado.ExecuteCommandAsync("UPDATE AccountNetworkPolicies SET Revision=Revision WHERE 1=0;");
                await _store.SetInTransactionAsync(client, account, new(NetworkMode.Direct), saved.Revision);
            }
            finally
            {
                client.Ado.RollbackTran();
            }
        }
        (await _store.GetAsync(account)).Should().Be(saved);
    }

    [Theory]
    [InlineData("deleted", "proxy_profile_not_found")]
    [InlineData("updated", "proxy_profile_revision_conflict")]
    [InlineData("disabled", "proxy_profile_disabled")]
    [InlineData("global", "proxy_profiles_disabled")]
    [InlineData("credentials", "proxy_credentials_unavailable")]
    public async Task Final_binding_revalidates_frozen_profile_inside_caller_transaction_without_leaking_policy(
        string change, string code)
    {
        var profile = AddProfile();
        var selection = new AccountNetworkSelection(NetworkMode.ProxyProfile, profile.Id);
        var frozen = await _store.ResolveAsync(new(null, "login", selection));
        switch (change)
        {
            case "deleted": _client.Deleteable<ProxyProfile>().Where(p => p.Id == profile.Id).ExecuteCommand(); break;
            case "updated": profile.Revision++; _client.Updateable(profile).ExecuteCommand(); break;
            case "disabled": profile.IsEnabled = false; _client.Updateable(profile).ExecuteCommand(); break;
            case "global": _client.Updateable<SystemRuntimeSettings>().SetColumns(s => s.DeveloperProxyProfilesEnabled == false).Where(s => s.Id == 1).ExecuteCommand(); break;
            case "credentials": profile.ProtectedUsername = "corrupt"; profile.ProtectedPassword = "corrupt"; _client.Updateable(profile).ExecuteCommand(); break;
        }
        var account = new AccountIdentity("codex", Guid.NewGuid());
        using (var client = _client.CopyNew())
        {
            client.Ado.BeginTran();
            try
            {
                await client.Ado.ExecuteCommandAsync("UPDATE AccountNetworkPolicies SET Revision=Revision WHERE 1=0;");
                InsertAccount(client, account, Guid.NewGuid(), false);
                await Fails(() => _store.SetInTransactionAsync(client, account, selection,
                    expectedProfileRevision: frozen.ProfileRevision), code);
                client.Queryable<AccountNetworkPolicy>().Count().Should().Be(0);
                client.Queryable<CodexAccount>().Any(a => a.Id == account.AccountId).Should().BeTrue(
                    "the hook must not roll back the caller's account insertion on validation failure");
            }
            finally
            {
                client.Ado.RollbackTran();
            }
        }
        _client.Queryable<AccountNetworkPolicy>().Count().Should().Be(0);
        await Fails(() => _store.GetAsync(account), "account_not_found");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_controls_atomic_account_and_policy_deletion(bool commit)
    {
        var account = AddAccount("codex");
        var saved = await _store.SetAsync(account, new(NetworkMode.Direct));
        using (var client = _client.CopyNew())
        {
            client.Ado.BeginTran();
            try
            {
                await client.Ado.ExecuteCommandAsync("UPDATE AccountNetworkPolicies SET Revision=Revision WHERE 1=0;");
                await _store.DeleteInTransactionAsync(client, account, saved.Revision);
                await client.Deleteable<CodexAccount>().Where(a => a.Id == account.AccountId).ExecuteCommandAsync();
                client.Queryable<AccountNetworkPolicy>().Count().Should().Be(0);
                if (commit) client.Ado.CommitTran();
                else client.Ado.RollbackTran();
            }
            catch
            {
                client.Ado.RollbackTran();
                throw;
            }
        }
        if (commit)
        {
            _client.Queryable<AccountNetworkPolicy>().Count().Should().Be(0);
            await Fails(() => _store.GetAsync(account), "account_not_found");
        }
        else (await _store.GetAsync(account)).Should().Be(saved);
    }

    [Fact]
    public async Task Transaction_hooks_validate_account_and_policy_revision_before_any_mutation()
    {
        var account = AddAccount("google");
        var saved = await _store.SetAsync(account, new(NetworkMode.Direct));
        using var client = _client.CopyNew();
        client.Ado.BeginTran();
        try
        {
            await client.Ado.ExecuteCommandAsync("UPDATE AccountNetworkPolicies SET Revision=Revision WHERE 1=0;");
            await Fails(() => _store.SetInTransactionAsync(client, account, new(NetworkMode.Legacy), "stale"),
                "account_network_revision_conflict");
            await Fails(() => _store.DeleteInTransactionAsync(client, account, "stale"),
                "account_network_revision_conflict");
            var missing = new AccountIdentity("google", Guid.NewGuid());
            await Fails(() => _store.SetInTransactionAsync(client, missing, new(NetworkMode.Direct)), "account_not_found");
            await Fails(() => _store.DeleteInTransactionAsync(client, missing), "account_not_found");
            var row = client.Queryable<AccountNetworkPolicy>().ToList().Single();
            row.Revision.Should().Be(saved.Revision);
            row.Mode.Should().Be(NetworkMode.Direct);
        }
        finally
        {
            client.Ado.RollbackTran();
        }
        (await _store.GetAsync(account)).Should().Be(saved);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Assignment_and_actual_profile_delete_serialize_under_contention_in_both_orderings(bool assignmentFirst)
    {
        // Keep readers concurrent with the winning writer, as in production.
        _client.Ado.ExecuteCommand("PRAGMA journal_mode=WAL;");
        var account = AddAccount("codex");
        var profile = AddProfile();
        _client.Updateable<SystemRuntimeSettings>()
            .SetColumns(s => s.DeveloperFeaturesEnabled == true).Where(s => s.Id == 1).ExecuteCommand();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var releaseWinner = new ManualResetEventSlim();
        using var releaseBusyCallback = new ManualResetEventSlim();
        var winnerHasLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loserObservedBusy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var assignmentClient = NewIndependentClient();
        using var deletionClient = NewIndependentClient();
        using var dbLock = new SemaphoreSlim(1, 1);
        using var deletionDb = new AppDbContext(deletionClient, dbLock);
        var controller = new ProxyProfilesApiController(deletionDb, new EnabledSettings(), _secrets);

        // The test-installed native handler proves the contender's actual BEGIN IMMEDIATE
        // encounters the writer lock. It controls retry ordering; it does not test the
        // production provider's normal busy-wait implementation.
        var busyCalls = 0;
        NativeBusyCallback busyCallback = (_, _) =>
        {
            if (Interlocked.Increment(ref busyCalls) != 1) return 0;
            loserObservedBusy.TrySetResult(true);
            return releaseBusyCallback.Wait(TimeSpan.FromSeconds(10)) && !timeout.IsCancellationRequested ? 1 : 0;
        };
        IntPtr contenderHandle = IntPtr.Zero;
        SQLitePCL.strdelegate_trace contenderTrace = (_, sql) =>
        {
            if (!sql.TrimStart().StartsWith("BEGIN IMMEDIATE", StringComparison.OrdinalIgnoreCase)) return;
            // The handle is recorded on connection open below. Register here because the
            // controller's PRAGMA busy_timeout would otherwise replace the handler.
            var result = SetNativeBusyHandler(contenderHandle, busyCallback, IntPtr.Zero);
            if (result != SQLitePCL.raw.SQLITE_OK)
                loserObservedBusy.TrySetException(new InvalidOperationException($"Could not install SQLite busy handler: {result}"));
        };
        void ObserveContender(System.Data.IDbConnection connection, TimeSpan _)
        {
            var sqlite = (Microsoft.Data.Sqlite.SqliteConnection)connection;
            contenderHandle = sqlite.Handle!.DangerousGetHandle();
            SQLitePCL.raw.sqlite3_trace(sqlite.Handle!, contenderTrace, null);
        }
        if (assignmentFirst) deletionClient.Aop.CheckConnectionExecuted = ObserveContender;
        else assignmentClient.Aop.CheckConnectionExecuted = ObserveContender;

        // The winning delete pauses after its actual UPDATE acquired the writer lock.
        // CopyNew preserves the controller connection's AOP callbacks.

        deletionClient.Aop.OnLogExecuted = (sql, _) =>
        {
            if (assignmentFirst || !IsUpdateOf(sql, "ProxyProfiles")) return;
            winnerHasLock.TrySetResult(true);
            releaseWinner.Wait(timeout.Token);
        };
        async Task<AccountNetworkPolicyState> Assign()
        {
            await assignmentClient.Ado.BeginTranAsync();
            try
            {
                await assignmentClient.Ado.ExecuteCommandAsync("UPDATE AccountNetworkPolicies SET Revision=Revision WHERE 1=0;");
                if (assignmentFirst)
                {
                    winnerHasLock.TrySetResult(true);
                    releaseWinner.Wait(timeout.Token);
                }
                var state = await _store.SetInTransactionAsync(assignmentClient, account,
                    new(NetworkMode.ProxyProfile, profile.Id), expectedProfileRevision: profile.Revision,
                    cancellationToken: timeout.Token);
                await assignmentClient.Ado.CommitTranAsync();
                return state;
            }
            catch
            {
                await assignmentClient.Ado.RollbackTranAsync();
                throw;
            }
        }

        Task<AccountNetworkPolicyState>? assignment = null;
        Task<IActionResult>? deletion = null;
        try
        {
            if (assignmentFirst) assignment = Task.Run(async () =>
            {
                try { return await Assign(); }
                finally { releaseBusyCallback.Set(); }
            });
            else deletion = Task.Run(async () =>
            {
                try { return await controller.Delete(profile.Id, timeout.Token); }
                finally { releaseBusyCallback.Set(); }
            });
            await winnerHasLock.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (assignmentFirst) deletion = Task.Run(() => controller.Delete(profile.Id, timeout.Token));
            else assignment = Task.Run(Assign);
            (await loserObservedBusy.Task.WaitAsync(TimeSpan.FromSeconds(3)))
                .Should().BeTrue("the actual contender BEGIN must invoke SQLite's busy handler while the winner holds its lock");
            releaseWinner.Set();

            if (assignmentFirst)
            {
                await assignment!.WaitAsync(TimeSpan.FromSeconds(8));
                var result = await deletion!.WaitAsync(TimeSpan.FromSeconds(8));
                result.Should().BeOfType<ConflictObjectResult>();
                (await _store.GetAsync(account)).Selection.ProxyProfileId.Should().Be(profile.Id);
                _client.Queryable<ProxyProfile>().Any(p => p.Id == profile.Id).Should().BeTrue();
            }
            else
            {
                (await deletion!.WaitAsync(TimeSpan.FromSeconds(8))).Should().BeOfType<OkObjectResult>();
                await Fails(() => assignment!.WaitAsync(TimeSpan.FromSeconds(8)), "proxy_profile_not_found");
                _client.Queryable<AccountNetworkPolicy>().Count().Should().Be(0);
                _client.Queryable<ProxyProfile>().Any(p => p.Id == profile.Id).Should().BeFalse();
                (await _store.GetAsync(account)).Selection.Mode.Should().Be(NetworkMode.Legacy);
            }
        }
        finally
        {
            timeout.Cancel();
            releaseWinner.Set();
            releaseBusyCallback.Set();
            // Drain both workers before releasing connections or native callback delegates.
            // Cleanup failures must not replace the test's original assertion/exception.
            try
            {
                foreach (var worker in new Task?[] { assignment, deletion })
                {
                    if (worker is null) continue;
                    try { await worker; }
                    catch (Exception) { }
                }
            }
            finally
            {
                GC.KeepAlive(contenderTrace);
                GC.KeepAlive(busyCallback);
            }
        }
    }

    [Theory]
    [InlineData("codex", false)]
    [InlineData("google", false)]
    [InlineData("kimi", false)]
    [InlineData("xai", false)]
    [InlineData("device-oauth", false)]
    [InlineData("codex", true)]
    [InlineData("google", true)]
    [InlineData("kimi", true)]
    [InlineData("xai", true)]
    [InlineData("device-oauth", true)]
    public async Task Account_and_policy_read_has_one_snapshot_across_committed_deletion(string provider, bool resolve)
    {
        // WAL allows deletion to commit while a completed SELECT is paused at the AOP
        // boundary. The old two-query implementation pauses after account existence and
        // then incorrectly reads the deleted policy as Legacy.
        _client.Ado.ExecuteCommand("PRAGMA journal_mode=WAL;");
        var account = AddAccount(provider);
        await _store.SetAsync(account, new(NetworkMode.Direct));
        using var release = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var queryExecuted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        var accountTable = AccountTable(provider);
        _client.Aop.OnLogExecuted = (sql, _) =>
        {
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains(accountTable, StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref first, 1) == 0)
            {
                queryExecuted.TrySetResult(true);
                release.Wait(timeout.Token);
            }
        };
        Task<NetworkMode>? read = null;
        try
        {
            read = Task.Run(async () => resolve
                ? (await _store.ResolveForAccountAsync(account, timeout.Token)).Mode
                : (await _store.GetAsync(account, timeout.Token)).Selection.Mode);
            await queryExecuted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await DeleteAccountAndPolicyAsync(account, timeout.Token).WaitAsync(TimeSpan.FromSeconds(5));
            release.Set();
            (await read.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(NetworkMode.Direct,
                "a read already holding the old snapshot must not combine it with a deleted policy");
            await Fails(() => _store.GetAsync(account), "account_not_found");
            await Fails(() => _store.ResolveForAccountAsync(account), "account_not_found");
        }
        finally
        {
            release.Set();
            timeout.Cancel();
            _client.Aop.OnLogExecuted = null;
            if (read is not null)
            {
                try { await read.WaitAsync(TimeSpan.FromSeconds(8)); }
                catch (Exception) when (read.IsCompleted) { }
            }
        }
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    [InlineData("device-oauth")]
    public async Task Delete_during_inflight_send_does_not_change_it_but_next_send_fails_closed(string provider)
    {
        var account = AddAccount(provider);
        await _store.SetAsync(account, new(NetworkMode.Direct));
        using var transport = new BlockingTransport();
        using var http = new AccountNetworkClientFactory(_store, transport).CreateForAccount(account, "test");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<HttpResponseMessage>? first = null;
        try
        {
            first = http.GetAsync("https://example.invalid/first", timeout.Token);
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            transport.Modes.Should().Equal(NetworkMode.Direct);
            // This must commit while the fake transport is blocked: no DB transaction
            // may span the network operation or streaming response lifetime.
            await DeleteAccountAndPolicyAsync(account, timeout.Token).WaitAsync(TimeSpan.FromSeconds(5));
            await Fails(() => http.GetAsync("https://example.invalid/next", timeout.Token), "account_not_found");
            transport.Modes.Should().Equal(new[] { NetworkMode.Direct }, "a later send must never acquire the default outlet");
            transport.Release.TrySetResult(true);
            using var response = await first.WaitAsync(TimeSpan.FromSeconds(5));
            response.IsSuccessStatusCode.Should().BeTrue();
        }
        finally
        {
            transport.Release.TrySetResult(true);
            timeout.Cancel();
            if (first is not null)
            {
                try { (await first.WaitAsync(TimeSpan.FromSeconds(8))).Dispose(); }
                catch (Exception) when (first.IsCompleted) { }
            }
        }
    }

    private async Task DeleteAccountAndPolicyAsync(AccountIdentity account, CancellationToken ct)
    {
        using var client = NewIndependentClient();
        client.Ado.BeginTran();
        try
        {
            await client.Ado.ExecuteCommandAsync("UPDATE AccountNetworkPolicies SET Revision=Revision WHERE 1=0;");
            await _store.DeleteInTransactionAsync(client, account, cancellationToken: ct);
            switch (account.ProviderKey)
            {
                case "codex": await client.Deleteable<CodexAccount>().Where(a => a.Id == account.AccountId).ExecuteCommandAsync(ct); break;
                case "google": await client.Deleteable<GoogleAccount>().Where(a => a.Id == account.AccountId).ExecuteCommandAsync(ct); break;
                case "kimi": await client.Updateable<KimiAccount>().SetColumns(a => a.IsDeleted == true).Where(a => a.Id == account.AccountId).ExecuteCommandAsync(ct); break;
                case "xai": await client.Updateable<XaiAccount>().SetColumns(a => a.IsDeleted == true).Where(a => a.Id == account.AccountId).ExecuteCommandAsync(ct); break;
                case "device-oauth": await client.Updateable<DeviceOAuthAccount>().SetColumns(a => a.IsDeleted == true).Where(a => a.Id == account.AccountId).ExecuteCommandAsync(ct); break;
                default: throw new ArgumentOutOfRangeException(nameof(account));
            }
            client.Ado.CommitTran();
        }
        catch
        {
            client.Ado.RollbackTran();
            throw;
        }
    }

    private static string AccountTable(string provider) => provider switch
    {
        "codex" => "CodexAccounts", "google" => "GoogleAccounts", "kimi" => "KimiAccounts",
        "xai" => "XaiAccounts", "device-oauth" => "DeviceOAuthAccounts",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private sealed class BlockingTransport : INetworkTransport, IDisposable
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Collections.Concurrent.ConcurrentQueue<NetworkMode> Modes { get; } = new();
        private readonly HttpClient _client;
        public BlockingTransport() => _client = new HttpClient(new Handler(this));
        public ValueTask<INetworkTransportLease> AcquireAsync(ResolvedNetworkConfiguration configuration,
            string isolationScope, string purpose, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Modes.Enqueue(configuration.Mode);
            return ValueTask.FromResult<INetworkTransportLease>(new Lease(_client));
        }
        public void Dispose() => _client.Dispose();
        private sealed class Lease(HttpClient client) : INetworkTransportLease
        {
            public HttpClient Client => client;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
        private sealed class Handler(BlockingTransport owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                owner.Started.TrySetResult(true);
                await owner.Release.Task.WaitAsync(ct);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("ok") };
            }
        }
    }

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate int NativeBusyCallback(IntPtr state, int attempts);

    [System.Runtime.InteropServices.DllImport("e_sqlite3", EntryPoint = "sqlite3_busy_handler",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern int SetNativeBusyHandler(IntPtr database, NativeBusyCallback callback, IntPtr state);

    private static bool IsUpdateOf(string sql, string table) =>
        sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
        && sql.Contains(table, StringComparison.OrdinalIgnoreCase);

    private SqlSugarClient NewIndependentClient()
    {
        var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_path};Pooling=False",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = true
        });
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        return client;
    }

    private AccountNetworkPolicyStore NewStore() => new(_services.GetRequiredService<IServiceScopeFactory>(), _clock, _secrets);

    private AccountIdentity AddAccount(string provider, Guid? id = null, Guid? linkedSiteId = null, bool deleted = false)
    {
        var accountId = id ?? Guid.NewGuid();
        var site = linkedSiteId ?? Guid.NewGuid();
        var account = new AccountIdentity(provider, accountId);
        InsertAccount(_client, account, site, deleted);
        return account;
    }

    private static void InsertAccount(ISqlSugarClient client, AccountIdentity account, Guid site, bool deleted)
    {
        switch (account.ProviderKey)
        {
            case "codex": client.Insertable(new CodexAccount { Id = account.AccountId, LinkedSiteId = site, AccessToken = "account-secret" }).ExecuteCommand(); break;
            case "google": client.Insertable(new GoogleAccount { Id = account.AccountId, LinkedSiteId = site, AccessToken = "account-secret" }).ExecuteCommand(); break;
            case "kimi": client.Insertable(new KimiAccount { Id = account.AccountId, LinkedSiteId = site, IsDeleted = deleted, AccessToken = "account-secret" }).ExecuteCommand(); break;
            case "xai": client.Insertable(new XaiAccount { Id = account.AccountId, LinkedSiteId = site, IsDeleted = deleted, AccessToken = "account-secret" }).ExecuteCommand(); break;
            case "device-oauth": client.Insertable(new DeviceOAuthAccount { Id = account.AccountId, LinkedSiteId = site, IsDeleted = deleted, VendorKey = "test-vendor", AccessToken = "account-secret" }).ExecuteCommand(); break;
            default: throw new ArgumentOutOfRangeException(nameof(account));
        }
    }

    private ProxyProfile AddProfile()
    {
        var profile = new ProxyProfile { Key = Guid.NewGuid().ToString("N"), Name = "Test proxy", ProxyUrl = "http://127.0.0.1:12345", Revision = 1 };
        _client.Insertable(profile).ExecuteCommand();
        return profile;
    }

    private static async Task<NetworkPolicyException> Fails(Func<Task> action, string code)
    {
        var assertion = await action.Should().ThrowAsync<NetworkPolicyException>();
        assertion.Which.Code.Should().Be(code);
        return assertion.Which;
    }

    public void Dispose()
    {
        _services.Dispose();
        _client.Dispose();
        // Only this fixture's connections/files are ours. Clearing all pools would close
        // unrelated shared-memory databases used by tests running in parallel.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Delete(_path + suffix);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(25);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(25);
                }
            }
        }
    }

    // Avoid opening the controller's original connection for its feature check: the
    // first connection callback must be the CopyNew transaction connection under test.
    private sealed class EnabledSettings : ISystemRuntimeSettingsService
    {
        public Task<SystemRuntimeSettings> GetOrCreateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemRuntimeSettings { DeveloperFeaturesEnabled = true, DeveloperProxyProfilesEnabled = true });
        public Task<SystemRuntimeSettings> UpdateAsync(UpdateSystemRuntimeSettingsRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ClearUsageLogsAsync(ClearUsageLogsRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
