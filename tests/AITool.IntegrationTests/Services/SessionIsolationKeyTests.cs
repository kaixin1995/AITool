using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using Xunit;

namespace AITool.IntegrationTests.Services;

public sealed class SessionIsolationKeyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"aitool-session-key-{Guid.NewGuid():N}");
    private readonly List<ServiceProvider> _providers = [];
    private readonly Guid _accountId = Guid.Parse("9437c1aa-14bc-4652-a31c-1f2b5ead983b");
    private readonly ServiceProvider _provider;
    private string DatabasePath => Path.Combine(_directory, "isolation.db");
    private string KeyRingPath => Path.Combine(_directory, "key-ring");

    public SessionIsolationKeyTests()
    {
        Directory.CreateDirectory(_directory);
        _provider = CreateProvider();
        Initialize(_provider);
    }

    [Fact]
    public async Task Initialization_persists_only_protected_random_256_bit_key_and_fixed_version()
    {
        var deriver = Deriver(_provider);
        var first = await Derive(deriver);
        first.Should().MatchRegex("^[0-9a-f]{64}$");
        var row = Row();
        row.Id.Should().Be(1);
        row.Version.Should().Be(1);
        var key = Protector(_provider).Unprotect(Convert.FromBase64String(row.ProtectedKey));
        try
        {
            key.Should().HaveCount(32);
            row.ProtectedKey.Should().NotBe(Convert.ToBase64String(key));
            (await Derive(deriver)).Should().Be(first);
            Row().ProtectedKey.Should().Be(row.ProtectedKey);
            Rows().Should().ContainSingle();
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    [Fact]
    public async Task Startup_bootstrap_is_idempotent_and_validates_existing_key_instead_of_rotating()
    {
        var store = new SessionIsolationKeyStore(_provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<IDataProtectionProvider>());
        await store.EnsureInitializedAsync();
        var ciphertext = Row().ProtectedKey;
        await store.EnsureInitializedAsync();
        var restarted = CreateProvider();
        var restartedStore = new SessionIsolationKeyStore(restarted.GetRequiredService<IServiceScopeFactory>(),
            restarted.GetRequiredService<IDataProtectionProvider>());
        await restartedStore.EnsureInitializedAsync();
        Row().ProtectedKey.Should().Be(ciphertext);
        SetCiphertext("corrupted");
        await Assert.ThrowsAsync<CryptographicException>(() => restartedStore.EnsureInitializedAsync());
        Row().ProtectedKey.Should().Be("corrupted");
    }

    [Fact]
    public async Task Every_identity_component_and_field_domain_separate_sessions_including_shared_workspace_accounts()
    {
        var deriver = Deriver(_provider);
        // These are distinct account rows, even if the external workspace and original seed match.
        const string sharedWorkspaceSeed = "shared-workspace/session";
        var baseline = await deriver.DeriveAsync("client", "provider", _accountId, "session", sharedWorkspaceSeed);
        var values = new[]
        {
            baseline,
            await deriver.DeriveAsync("other-client", "provider", _accountId, "session", sharedWorkspaceSeed),
            await deriver.DeriveAsync("client", "other-provider", _accountId, "session", sharedWorkspaceSeed),
            await deriver.DeriveAsync("client", "provider", Guid.NewGuid(), "session", sharedWorkspaceSeed),
            await deriver.DeriveAsync("client", "provider", _accountId, "conversation", sharedWorkspaceSeed),
            await deriver.DeriveAsync("client", "provider", _accountId, "session", "different-seed")
        };
        values.Distinct().Should().HaveCount(values.Length);
        (await deriver.DeriveAsync("client", "provider", _accountId, "session", sharedWorkspaceSeed)).Should().Be(baseline);
    }

    [Fact]
    public async Task Length_prefixes_exact_utf8_and_no_normalization_prevent_boundary_collisions()
    {
        var deriver = Deriver(_provider);
        var values = new[]
        {
            await deriver.DeriveAsync("ab", "c", _accountId, "field", "seed"),
            await deriver.DeriveAsync("a", "bc", _accountId, "field", "seed"),
            await deriver.DeriveAsync("a|b", "c", _accountId, "field", "seed"),
            await deriver.DeriveAsync("a", "b|c", _accountId, "field", "seed"),
            await deriver.DeriveAsync("a", "p", _accountId, "x", "y\0z"),
            await deriver.DeriveAsync("a", "p", _accountId, "x\0y", "z"),
            await deriver.DeriveAsync("Client", "p", _accountId, "field", "seed"),
            await deriver.DeriveAsync("client", "p", _accountId, "field", "seed"),
            await deriver.DeriveAsync(" client", "p", _accountId, "field", "seed"),
            await deriver.DeriveAsync("é", "p", _accountId, "field", "seed"),
            await deriver.DeriveAsync("e\u0301", "p", _accountId, "field", "seed")
        };
        values.Distinct().Should().HaveCount(values.Length);
    }

    [Fact]
    public async Task Derivation_matches_explicit_versioned_length_prefixed_hmac_contract()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        try
        {
            InsertKey(Protector(_provider).Protect(key));
            string[] fields = ["1", "client/雪", "provider", _accountId.ToString("D"), "session", "seed\0😀"];
            using var encoded = new MemoryStream();
            foreach (var field in fields)
            {
                var bytes = Encoding.UTF8.GetBytes(field);
                var length = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
                encoded.Write(length);
                encoded.Write(bytes);
            }
            var expected = Convert.ToHexString(HMACSHA256.HashData(key, encoded.ToArray())).ToLowerInvariant();
            (await Deriver(_provider).DeriveAsync(fields[1], fields[2], _accountId, fields[4], fields[5]))
                .Should().Be(expected);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    [Fact]
    public async Task Concurrent_instances_claim_one_key_and_all_use_the_persisted_winner()
    {
        // Warm the shared key ring before racing independent DB clients/service containers.
        Protector(_provider).Protect(new byte[32]);
        var instances = Enumerable.Range(0, 12).Select(_ => Deriver(CreateProvider())).ToArray();
        var results = await Task.WhenAll(instances.Select(instance => Task.Run(() => Derive(instance))));
        results.Distinct().Should().ContainSingle();
        Rows().Should().ContainSingle();
        (await Derive(Deriver(CreateProvider()))).Should().Be(results[0]);
    }

    [Fact]
    public async Task Restart_with_same_database_and_key_ring_preserves_output_and_ciphertext()
    {
        var first = await Derive(Deriver(_provider));
        var ciphertext = Row().ProtectedKey;
        var restarted = CreateProvider();
        (await Derive(Deriver(restarted))).Should().Be(first);
        Row().ProtectedKey.Should().Be(ciphertext);
    }

    [Fact]
    public async Task Separate_deployments_have_independent_random_salts_even_with_same_key_ring()
    {
        var first = await Derive(Deriver(_provider));
        var other = CreateProvider(Path.Combine(_directory, "separate.db"));
        Initialize(other);
        (await Derive(Deriver(other))).Should().NotBe(first);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64-ciphertext")]
    [InlineData("AQIDBA==")]
    public async Task Corrupt_or_missing_secret_fails_without_replacement_and_restore_recovers_original(string corrupted)
    {
        var deriver = Deriver(_provider);
        var original = await Derive(deriver);
        var ciphertext = Row().ProtectedKey;
        SetCiphertext(corrupted);
        await Assert.ThrowsAsync<CryptographicException>(() => Derive(deriver));
        await Assert.ThrowsAsync<CryptographicException>(() => Derive(Deriver(CreateProvider())));
        Row().ProtectedKey.Should().Be(corrupted);
        SetCiphertext(ciphertext); // Simulate restoring the previous DB backup, not rotating.
        (await Derive(deriver)).Should().Be(original);
    }

    [Fact]
    public async Task Unavailable_key_ring_and_wrong_purpose_fail_without_modifying_database()
    {
        var original = await Derive(Deriver(_provider));
        var ciphertext = Row().ProtectedKey;
        var otherRing = CreateProvider(keyRingPath: Path.Combine(_directory, "other-ring"));
        await Assert.ThrowsAsync<CryptographicException>(() => Derive(Deriver(otherRing)));
        Row().ProtectedKey.Should().Be(ciphertext);
        var wrongProtector = _provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("unrelated-purpose");
        var wrongCiphertext = Convert.ToBase64String(wrongProtector.Protect(new byte[32]));
        SetCiphertext(wrongCiphertext);
        SetFingerprint(wrongCiphertext); // Reach purpose validation, not just fingerprint validation.
        await Assert.ThrowsAsync<CryptographicException>(() => Derive(Deriver(_provider)));
        SetFingerprint(ciphertext);
        SetCiphertext(ciphertext);
        (await Derive(Deriver(CreateProvider()))).Should().Be(original);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public async Task Valid_protection_of_wrong_key_length_is_not_initialization_authority(int length)
    {
        InsertKey(Protector(_provider).Protect(new byte[length]));
        var ciphertext = Row().ProtectedKey;
        await Assert.ThrowsAsync<CryptographicException>(() => Derive(Deriver(_provider)));
        Row().ProtectedKey.Should().Be(ciphertext);
    }

    [Fact]
    public async Task Unknown_version_or_wrong_singleton_row_is_not_silently_reinitialized()
    {
        await Derive(Deriver(_provider));
        var ciphertext = Row().ProtectedKey;
        using var client = Client();
        client.Ado.ExecuteCommand("UPDATE SessionIsolationKeys SET Version = 2;");
        await Assert.ThrowsAsync<CryptographicException>(() => Derive(Deriver(_provider)));
        Row().Version.Should().Be(2);
        client.Ado.ExecuteCommand("UPDATE SessionIsolationKeys SET Id = 2, Version = 1;");
        await Assert.ThrowsAsync<CryptographicException>(() => Derive(Deriver(_provider)));
        Rows().Should().ContainSingle().Which.Id.Should().Be(2);
        Row().ProtectedKey.Should().Be(ciphertext);
    }

    [Fact]
    public async Task Deleted_key_on_live_and_restarted_stores_fails_closed_instead_of_rotating()
    {
        var deriver = Deriver(_provider);
        await Derive(deriver);
        using var client = Client();
        client.Deleteable<SessionIsolationKey>().ExecuteCommand();
        await Assert.ThrowsAsync<CryptographicException>(() => Derive(deriver));
        await Assert.ThrowsAsync<CryptographicException>(() => Derive(Deriver(CreateProvider())));
        Rows().Should().BeEmpty();
        client.Queryable<SessionIsolationKeyMetadata>().ToList().Should().ContainSingle();
        // Deleting BOTH the marker and key is indistinguishable from a first deployment;
        // operators must restore the complete DB together with its Data Protection key ring.
    }

    [Fact]
    public async Task Missing_metadata_or_replaced_ciphertext_is_not_silently_blessed()
    {
        var original = await Derive(Deriver(_provider));
        var ciphertext = Row().ProtectedKey;
        using var client = Client();
        var marker = client.Queryable<SessionIsolationKeyMetadata>().ToList().Single();
        client.Deleteable<SessionIsolationKeyMetadata>().ExecuteCommand();
        await Assert.ThrowsAsync<CryptographicException>(() => Derive(Deriver(CreateProvider())));
        Row().ProtectedKey.Should().Be(ciphertext);
        client.Queryable<SessionIsolationKeyMetadata>().ToList().Should().BeEmpty();
        client.Insertable(marker).ExecuteCommand();
        SetCiphertext(Convert.ToBase64String(Protector(_provider).Protect(RandomNumberGenerator.GetBytes(32))));
        await Assert.ThrowsAsync<CryptographicException>(() => Derive(Deriver(CreateProvider())));
        SetCiphertext(ciphertext);
        (await Derive(Deriver(CreateProvider()))).Should().Be(original);
    }

    [Fact]
    public async Task Failed_atomic_insert_leaves_no_half_key_and_can_be_retried_after_rollback()
    {
        using var client = Client();
        client.Ado.ExecuteCommand("""
            CREATE TRIGGER reject_session_key BEFORE INSERT ON SessionIsolationKeys
            BEGIN SELECT RAISE(ABORT, 'synthetic initialization failure'); END;
            """);
        var deriver = Deriver(_provider);
        await Assert.ThrowsAnyAsync<Exception>(() => Derive(deriver));
        Rows().Should().BeEmpty();
        client.Queryable<SessionIsolationKeyMetadata>().ToList().Should().BeEmpty("the marker and key roll back together");
        client.Ado.ExecuteCommand("DROP TRIGGER reject_session_key;");
        var first = await Derive(deriver);
        (await Derive(Deriver(CreateProvider()))).Should().Be(first);
        Rows().Should().ContainSingle();
    }

    [Fact]
    public async Task Independent_read_of_startup_initialized_key_does_not_inherit_request_transaction_rollback()
    {
        // Bootstrap precedes request transactions: SQLite cannot initialize through an
        // independent writer while another connection already holds its writer lock.
        var store = new SessionIsolationKeyStore(_provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<IDataProtectionProvider>());
        await store.EnsureInitializedAsync();
        var ciphertext = Row().ProtectedKey;
        var deriver = new SessionIsolationDeriver(store);
        var original = await Derive(deriver);
        using var requestScope = _provider.CreateScope();
        var requestDb = requestScope.ServiceProvider.GetRequiredService<AppDbContext>();
        requestDb.Client.Ado.BeginTran();
        string output;
        try
        {
            // Explicitly hold the request connection's writer lock. The deriver only reads
            // its already initialized key using its own connection under WAL.
            requestDb.Client.Ado.ExecuteCommand("UPDATE SessionIsolationKeys SET Version = Version WHERE Id = 1;");
            output = await Derive(deriver);
        }
        finally { requestDb.Client.Ado.RollbackTran(); }
        output.Should().Be(original);
        Rows().Should().ContainSingle();
        Row().ProtectedKey.Should().Be(ciphertext);
        (await Derive(Deriver(CreateProvider()))).Should().Be(original);
    }

    [Fact]
    public async Task Numeric_format_is_invariant_positive_int63_and_stable()
    {
        var deriver = Deriver(_provider);
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            for (var i = 0; i < 32; i++)
            {
                var seed = i.ToString(CultureInfo.InvariantCulture);
                var value = await deriver.DerivePositiveInt63Async("client", "google", _accountId, "session", seed);
                value.Should().MatchRegex("^[1-9][0-9]{0,18}$");
                long.Parse(value, CultureInfo.InvariantCulture).Should().BeInRange(1, long.MaxValue);
                var hex = await deriver.DeriveAsync("client", "google", _accountId, "session", seed);
                var positive = BinaryPrimitives.ReadUInt64BigEndian(Convert.FromHexString(hex)) & (ulong)long.MaxValue;
                value.Should().Be((positive == 0 ? 1UL : positive).ToString(CultureInfo.InvariantCulture));
                (await deriver.DerivePositiveInt63Async("client", "google", _accountId, "session", seed)).Should().Be(value);
            }
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public async Task Long_inputs_stream_to_fixed_output_and_over_limit_input_is_rejected_not_truncated()
    {
        var deriver = Deriver(_provider);
        var seed = new string('a', SessionIsolationDeriver.MaxFieldUtf8Bytes - 4) + "😀";
        var output = await deriver.DeriveAsync("client", "provider", _accountId, "session", seed);
        output.Should().HaveLength(64);
        (await deriver.DeriveAsync("client", "provider", _accountId, "session", seed)).Should().Be(output);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            deriver.DeriveAsync("client", "provider", _accountId, "session", seed + "a"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            deriver.DeriveAsync("client", "provider", _accountId, "session", new string('雪', 400000)));
        await Assert.ThrowsAsync<EncoderFallbackException>(() =>
            deriver.DeriveAsync("client", "provider", _accountId, "session", "\ud800"));
    }

    [Fact]
    public async Task Empty_seed_is_valid_but_missing_identity_is_rejected_before_key_creation()
    {
        var deriver = Deriver(_provider);
        await Assert.ThrowsAsync<ArgumentException>(() => deriver.DeriveAsync(" ", "p", _accountId, "f", "s"));
        await Assert.ThrowsAsync<ArgumentException>(() => deriver.DeriveAsync("c", "", _accountId, "f", "s"));
        await Assert.ThrowsAsync<ArgumentException>(() => deriver.DeriveAsync("c", "p", Guid.Empty, "f", "s"));
        await Assert.ThrowsAsync<ArgumentException>(() => deriver.DeriveAsync("c", "p", _accountId, " ", "s"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => deriver.DeriveAsync("c", "p", _accountId, "f", null!));
        Rows().Should().BeEmpty();
        (await deriver.DeriveAsync("c", "p", _accountId, "f", "")).Should().HaveLength(64);
    }

    [Fact]
    public async Task Precancelled_call_does_not_initialize_or_modify_key_and_can_be_retried()
    {
        var deriver = Deriver(_provider);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            deriver.DeriveAsync("client", "provider", _accountId, "session", "seed", cancellation.Token));
        Rows().Should().BeEmpty();
        var output = await Derive(deriver);
        var ciphertext = Row().ProtectedKey;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            deriver.DeriveAsync("client", "provider", _accountId, "session", "seed", cancellation.Token));
        Row().ProtectedKey.Should().Be(ciphertext);
        (await Derive(deriver)).Should().Be(output);
    }

    [Fact]
    public async Task Cancellation_after_protection_is_observed_before_atomic_insert()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new CancelOnProtectProvider(_provider.GetRequiredService<IDataProtectionProvider>(), cancellation);
        var deriver = new SessionIsolationDeriver(new SessionIsolationKeyStore(
            _provider.GetRequiredService<IServiceScopeFactory>(), provider));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            deriver.DeriveAsync("client", "provider", _accountId, "session", "seed", cancellation.Token));
        Rows().Should().BeEmpty();
        (await Derive(Deriver(_provider))).Should().HaveLength(64);
        Rows().Should().ContainSingle();
    }

    private sealed class CancelOnProtectProvider(IDataProtectionProvider inner, CancellationTokenSource cancellation)
        : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => new CancelOnProtect(inner.CreateProtector(purpose), cancellation);
    }

    private sealed class CancelOnProtect(IDataProtector inner, CancellationTokenSource cancellation) : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => new CancelOnProtect(inner.CreateProtector(purpose), cancellation);
        public byte[] Protect(byte[] plaintext)
        {
            var result = inner.Protect(plaintext);
            cancellation.Cancel();
            return result;
        }
        public byte[] Unprotect(byte[] protectedData) => inner.Unprotect(protectedData);
    }

    private ServiceProvider CreateProvider(string? databasePath = null, string? keyRingPath = null)
    {
        var services = new ServiceCollection();
        // All fixture/restart instances own unpooled connections. Never clear global pools:
        // other parallel suites keep shared in-memory databases alive through those pools.
        services.AddSqlSugar($"Data Source={databasePath ?? DatabasePath};Pooling=False");
        services.AddDataProtection().SetApplicationName("SessionIsolationKeyTests")
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath ?? KeyRingPath));
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    private static void Initialize(ServiceProvider provider)
    {
        var client = provider.GetRequiredService<ISqlSugarClient>();
        client.Ado.ExecuteCommand("PRAGMA journal_mode=WAL;");
        client.CodeFirst.InitTables(typeof(SessionIsolationKey), typeof(SessionIsolationKeyMetadata));
    }

    private static SessionIsolationDeriver Deriver(ServiceProvider provider) => new(new SessionIsolationKeyStore(
        provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IDataProtectionProvider>()));
    private Task<string> Derive(SessionIsolationDeriver deriver) =>
        deriver.DeriveAsync("client", "provider", _accountId, "session", "seed");
    private static IDataProtector Protector(ServiceProvider provider) =>
        provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("AITool.SessionIsolation.KeyStorage.v1");
    private ISqlSugarClient Client() => _provider.GetRequiredService<ISqlSugarClient>().CopyNew();
    private List<SessionIsolationKey> Rows()
    {
        using var client = Client();
        return client.Queryable<SessionIsolationKey>().ToList();
    }
    private SessionIsolationKey Row() => Rows().Single();
    private void InsertKey(byte[] protectedBytes)
    {
        using var client = Client();
        var ciphertext = Convert.ToBase64String(protectedBytes);
        client.Insertable(new SessionIsolationKey { ProtectedKey = ciphertext }).ExecuteCommand();
        client.Insertable(new SessionIsolationKeyMetadata
        {
            ProtectedKeyFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ciphertext)))
        }).ExecuteCommand();
    }
    private void SetFingerprint(string ciphertext)
    {
        using var client = Client();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ciphertext)));
        client.Updateable<SessionIsolationKeyMetadata>().SetColumns(row => row.ProtectedKeyFingerprint == fingerprint)
            .Where(row => row.Id == 1).ExecuteCommand();
    }
    private void SetCiphertext(string ciphertext)
    {
        using var client = Client();
        client.Updateable<SessionIsolationKey>().SetColumns(row => row.ProtectedKey == ciphertext).Where(row => row.Id == 1)
            .ExecuteCommand();
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            // AddSqlSugar registers an externally created singleton, so explicitly dispose
            // this fixture's root client as well as its container. CopyNew clients are scoped
            // to individual operations and are already disposed before teardown.
            provider.GetRequiredService<ISqlSugarClient>().Dispose();
            provider.Dispose();
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(_directory, true);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < 2) Thread.Sleep(50);
            }
        }
    }
}
