using System.Security.Cryptography;
using AITool.Domain.Codex;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit;

namespace AITool.IntegrationTests.Services;

/// <summary>Production options/schema initialization, isolated temporary SQLite/files only; no Web host or network.</summary>
public sealed class NetworkSecurityStartupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"aitool-network-startup-{Guid.NewGuid():N}");
    private readonly List<ServiceProvider> _providers = [];
    private readonly HashSet<string> _memoryKeyDirectories = new(StringComparer.Ordinal);
    private const string ApplicationName = "AITool.NetworkSecurity.v1";
    private string DatabasePath => Path.Combine(_directory, "accounts.db");

    public NetworkSecurityStartupTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Default_key_ring_follows_effective_SQL_DI_replacement_not_configuration_connection_string()
    {
        var configuredDatabase = Path.Combine(_directory, "configured-but-unused.db");
        var effectiveDatabase = Path.Combine(_directory, "actual-test.db");
        var provider = CreateProvider(effectiveDatabase, configuredDatabasePath: configuredDatabase);

        var repository = Repository(provider);

        repository.Directory.FullName.Should().Be(DefaultKeyRing(effectiveDatabase));
        Directory.Exists(DefaultKeyRing(configuredDatabase)).Should().BeFalse();
        File.Exists(configuredDatabase).Should().BeFalse();
        provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator
            .Should().Be(ApplicationName);
        provider.GetServices<IConfigureOptions<KeyManagementOptions>>()
            .Should().ContainSingle(x => x is NetworkKeyRingConfiguration);
    }

    [Fact]
    public void Different_effective_database_files_get_different_default_rings()
    {
        var first = Repository(CreateProvider(DatabasePath));
        var secondPath = Path.Combine(_directory, "second.db");
        var second = Repository(CreateProvider(secondPath));

        first.Directory.FullName.Should().Be(DefaultKeyRing(DatabasePath));
        second.Directory.FullName.Should().Be(DefaultKeyRing(secondPath));
        second.Directory.FullName.Should().NotBe(first.Directory.FullName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configured_relative_or_absolute_override_is_resolved_against_content_root(bool absolute)
    {
        var expected = Path.Combine(_directory, "private", "network-keys");
        var configured = absolute ? expected : Path.Combine("private", "network-keys");
        var provider = CreateProvider(DatabasePath, keyRingPath: configured);

        Repository(provider).Directory.FullName.Should().Be(Path.GetFullPath(expected));
        Directory.Exists(expected).Should().BeTrue();
        Directory.Exists(DefaultKeyRing(DatabasePath)).Should().BeFalse();
    }

    [Fact]
    public void Relative_database_source_resolves_beside_content_root_not_process_working_directory()
    {
        var provider = CreateProvider("relative.db");

        Repository(provider).Directory.FullName.Should().Be(DefaultKeyRing(Path.Combine(_directory, "relative.db")));
        // Options resolution inspects the connection configuration without opening this relative DB.
        File.Exists(Path.Combine(_directory, "relative.db")).Should().BeFalse();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void Memory_database_without_override_fails_outside_Testing(string environment)
    {
        var provider = CreateProvider(":memory:", environment: environment);
        Action configure = () => provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.ToString();

        configure.Should().Throw<InvalidOperationException>()
            .WithMessage("*persistent database or NetworkSecurity:KeyRingPath*");
        Directory.Exists(Path.Combine(_directory, ".aitool-secrets")).Should().BeFalse();
    }

    [Fact]
    public void Memory_database_with_explicit_persistent_override_is_allowed_outside_Testing()
    {
        var provider = CreateProvider(":memory:", keyRingPath: "explicit-keys");

        Repository(provider).Directory.FullName.Should().Be(Path.Combine(_directory, "explicit-keys"));
    }

    [Fact]
    public void Testing_memory_database_gets_stable_per_container_but_isolated_key_directory()
    {
        var firstProvider = CreateProvider(":memory:", environment: "Testing");
        var secondProvider = CreateProvider(":memory:", environment: "Testing");
        var first = Repository(firstProvider);
        var second = Repository(secondProvider);

        Repository(firstProvider).Directory.FullName.Should().Be(first.Directory.FullName);
        second.Directory.FullName.Should().NotBe(first.Directory.FullName);
        var protectedValue = firstProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("startup-isolation-test").Protect("container-one");
        Action decryptInOtherContainer = () => secondProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("startup-isolation-test").Unprotect(protectedValue);
        decryptInOtherContainer.Should().Throw<CryptographicException>();
        first.Directory.EnumerateFiles("key-*.xml").Should().NotBeEmpty();
    }

    [Fact]
    public void Key_directory_is_owner_only_on_Unix_including_preexisting_permissive_directory()
    {
        if (OperatingSystem.IsWindows()) return; // Windows ACLs are an operator requirement, not Unix modes.
        var keyDirectory = Path.Combine(_directory, "permissions");
        Directory.CreateDirectory(keyDirectory);
        File.SetUnixFileMode(keyDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        Repository(CreateProvider(DatabasePath, keyRingPath: keyDirectory));

        File.GetUnixFileMode(keyDirectory).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task Restart_with_same_database_and_production_options_preserves_credentials_and_session_derivation()
    {
        var accountId = Guid.NewGuid();
        var first = CreateProvider(DatabasePath);
        var firstClient = first.GetRequiredService<ISqlSugarClient>();
        SqlSugarSetup.InitializeDatabase(firstClient);
        var profile = new ProxyProfile { Key = "restart", Name = "Restart" };
        first.GetRequiredService<ProxyProfileSecretService>()
            .TryApplyForSave(profile, "http://127.0.0.1:7890", "Replace", "proxy-user", "proxy-password", out _)
            .Should().BeTrue();
        firstClient.Insertable(profile).ExecuteCommand();
        await first.GetRequiredService<SessionIsolationKeyStore>().EnsureInitializedAsync();
        var original = await Derive(first, accountId);
        var protectedKey = firstClient.Queryable<SessionIsolationKey>().ToList().Single().ProtectedKey;
        var fingerprint = firstClient.Queryable<SessionIsolationKeyMetadata>().ToList().Single().ProtectedKeyFingerprint;
        var keyRing = Repository(first).Directory.FullName;
        first.Dispose();

        var restarted = CreateProvider(DatabasePath);
        var restartedClient = restarted.GetRequiredService<ISqlSugarClient>();
        SqlSugarSetup.InitializeDatabase(restartedClient);
        await restarted.GetRequiredService<SessionIsolationKeyStore>().EnsureInitializedAsync();
        var stored = restartedClient.Queryable<ProxyProfile>().InSingle(profile.Id);
        var credentials = restarted.GetRequiredService<ProxyProfileSecretService>().ReadCredentials(stored);

        Repository(restarted).Directory.FullName.Should().Be(keyRing);
        credentials.Should().NotBeNull();
        credentials!.UserName.Should().Be("proxy-user");
        credentials.Password.Should().Be("proxy-password");
        stored.ProtectedUsername.Should().Be(profile.ProtectedUsername);
        stored.ProtectedPassword.Should().Be(profile.ProtectedPassword);
        (await Derive(restarted, accountId)).Should().Be(original);
        restartedClient.Queryable<SessionIsolationKey>().ToList().Single().ProtectedKey.Should().Be(protectedKey);
        restartedClient.Queryable<SessionIsolationKeyMetadata>().ToList().Single().ProtectedKeyFingerprint.Should().Be(fingerprint);
        restartedClient.Queryable<SessionIsolationKey>().Count().Should().Be(1);
        restartedClient.Queryable<SessionIsolationKeyMetadata>().Count().Should().Be(1);
    }

    [Fact]
    public async Task Production_CodeFirst_upgrades_populated_legacy_profiles_and_preserves_account_credentials_and_cache()
    {
        var provider = CreateProvider(DatabasePath);
        var client = provider.GetRequiredService<ISqlSugarClient>();
        // Seed a pre-network-security profile table, not a current ProxyProfile CodeFirst table.
        client.Ado.ExecuteCommand("""
            CREATE TABLE ProxyProfiles (
                Id TEXT PRIMARY KEY,
                Key TEXT NOT NULL,
                Name TEXT NOT NULL,
                ProxyUrl TEXT NOT NULL,
                Description TEXT NULL,
                IsEnabled INTEGER NOT NULL,
                SortOrder INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NULL
            );
            INSERT INTO ProxyProfiles (Id, Key, Name, ProxyUrl, Description, IsEnabled, SortOrder, CreatedAt)
            VALUES ('11111111-2222-3333-4444-555555555555', 'legacy', 'Legacy proxy',
                'http://alice:p%40ss@127.0.0.1:7890', 'keep-description', 1, 7, '2026-01-01 00:00:00');
            """);
        client.CodeFirst.InitTables(typeof(CodexAccount));
        var account = new CodexAccount
        {
            DisplayName = "Existing account", LinkedSiteId = Guid.NewGuid(),
            AccessToken = "existing-access-token", RefreshToken = "existing-refresh-token", IdToken = "existing-id-token",
            LastQuotaRawJson = "{\"remaining\":42}",
            LastQuotaCheckedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        client.Insertable(account).ExecuteCommand();
        var before = client.Queryable<CodexAccount>().InSingle(account.Id);

        SqlSugarSetup.InitializeDatabase(client);
        SqlSugarSetup.InitializeDatabase(client); // Startup schema initialization must be repeatable.

        var profile = client.Queryable<ProxyProfile>().ToList().Single();
        profile.Id.Should().Be(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        profile.ProxyUrl.Should().Be("http://alice:p%40ss@127.0.0.1:7890", "CodeFirst adds columns; secret migration is a separate startup step");
        profile.Revision.Should().Be(1);
        profile.ProtectedUsername.Should().BeNull();
        profile.ProtectedPassword.Should().BeNull();
        profile.ConfigurationError.Should().BeNull();
        profile.ProtectedLegacyConfiguration.Should().BeNull();
        profile.Description.Should().Be("keep-description");
        profile.IsEnabled.Should().BeTrue();
        profile.SortOrder.Should().Be(7);
        foreach (var table in new[] { "AccountNetworkPolicies", "SessionIsolationKeys", "SessionIsolationKeyMetadata", "UpstreamReferenceOwners" })
            client.Ado.GetInt("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;", new SugarParameter("@name", table))
                .Should().Be(1, $"production initialization must create {table}");

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await provider.GetRequiredService<ProxyProfileSecretService>().MigrateLegacyCredentialsAsync(db)).Should().Be(1);
            (await provider.GetRequiredService<ProxyProfileSecretService>().MigrateLegacyCredentialsAsync(db)).Should().Be(0);
        }
        profile = client.Queryable<ProxyProfile>().ToList().Single();
        profile.ProxyUrl.Should().Be("http://127.0.0.1:7890");
        profile.Revision.Should().Be(2);
        profile.ProtectedUsername.Should().NotBeNullOrEmpty().And.NotBe("alice");
        profile.ProtectedPassword.Should().NotBeNullOrEmpty().And.NotBe("p@ss");
        provider.GetRequiredService<ProxyProfileSecretService>().ReadCredentials(profile)!.Password.Should().Be("p@ss");
        await provider.GetRequiredService<SessionIsolationKeyStore>().EnsureInitializedAsync();
        client.Queryable<SessionIsolationKey>().Count().Should().Be(1);
        client.Queryable<SessionIsolationKeyMetadata>().Count().Should().Be(1);

        var policy = new AccountNetworkPolicy
        {
            ProviderKey = "codex", AccountId = account.Id, Mode = NetworkMode.ProxyProfile,
            ProxyProfileId = profile.Id, Revision = Guid.NewGuid().ToString("D"),
            CreatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks, UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks
        };
        client.Insertable(policy).ExecuteCommand();
        var owner = new UpstreamReferenceOwner
        {
            ClientScope = "test-client", Kind = UpstreamReferenceKind.Response, LookupDigest = new string('a', 64),
            OwnerProviderKey = "codex", OwnerAccountId = account.Id, UpstreamNamespace = "test-namespace",
            CreatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks, ExpiresAtUtcTicks = DateTimeOffset.UtcNow.AddHours(1).UtcTicks,
            IsNative = true, ExposureStatus = UpstreamReferenceExposureStatus.CommittedForExposure
        };
        client.Insertable(owner).ExecuteCommand();
        client.Queryable<AccountNetworkPolicy>().ToList().Single().ProxyProfileId.Should().Be(profile.Id);
        client.Queryable<UpstreamReferenceOwner>().ToList().Single().OwnerAccountId.Should().Be(account.Id);
        var after = client.Queryable<CodexAccount>().InSingle(account.Id);
        after.DisplayName.Should().Be(before.DisplayName);
        after.LinkedSiteId.Should().Be(before.LinkedSiteId);
        after.AccessToken.Should().Be(before.AccessToken);
        after.RefreshToken.Should().Be(before.RefreshToken);
        after.IdToken.Should().Be(before.IdToken);
        after.LastQuotaRawJson.Should().Be(before.LastQuotaRawJson);
        after.LastQuotaCheckedAt.Should().Be(before.LastQuotaCheckedAt);
        after.IsEnabled.Should().Be(before.IsEnabled);
    }

    private ServiceProvider CreateProvider(string databasePath, string? keyRingPath = null,
        string environment = "Production", string? configuredDatabasePath = null)
    {
        var configuredPath = configuredDatabasePath ?? databasePath;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = ConnectionString(configuredPath),
            ["NetworkSecurity:KeyRingPath"] = keyRingPath
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(environment, _directory));
        services.AddSqlSugar(ConnectionString(configuredPath));
        if (configuredDatabasePath is not null)
        {
            // Mirror integration-host replacements: final SQL DI, not IConfiguration, is authoritative.
            services.RemoveAll<ISqlSugarClient>();
            services.AddSqlSugar(ConnectionString(databasePath));
        }
        services.AddDataProtection().SetApplicationName(ApplicationName);
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>, NetworkKeyRingConfiguration>();
        services.AddSingleton<ProxyProfileSecretService>();
        services.AddSingleton<SessionIsolationKeyStore>();
        services.AddSingleton<SessionIsolationDeriver>();
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    private FileSystemXmlRepository Repository(ServiceProvider provider)
    {
        var repository = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository
            .Should().BeOfType<FileSystemXmlRepository>().Which;
        if (provider.GetRequiredService<ISqlSugarClient>().CurrentConnectionConfig.ConnectionString.Contains(":memory:", StringComparison.Ordinal)
            && provider.GetRequiredService<IConfiguration>()["NetworkSecurity:KeyRingPath"] is null)
            _memoryKeyDirectories.Add(repository.Directory.FullName);
        return repository;
    }

    private static string ConnectionString(string source) => new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = source, Pooling = false }.ToString();
    private static string DefaultKeyRing(string databasePath) => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(databasePath))!, ".aitool-secrets", Path.GetFileName(databasePath), "keyring");
    private static Task<string> Derive(ServiceProvider provider, Guid accountId) => provider.GetRequiredService<SessionIsolationDeriver>()
        .DeriveAsync("startup-client", "codex", accountId, "session", "same-seed");

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
        foreach (var directory in _memoryKeyDirectories)
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class TestEnvironment(string environmentName, string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "AITool.Web";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
