using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
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
    public async Task Key_rotation_retains_reference_tombstones_until_original_expiry()
    {
        await Derive(Deriver(_provider));
        using var client = Client();
        var expiry = DateTimeOffset.UtcNow.AddHours(12).UtcTicks;
        var row = new UpstreamReferenceOwner { ClientScope = "client", LookupDigest = new string('a', 64),
            OwnerProviderKey = "codex", OwnerAccountId = Guid.NewGuid(), UpstreamNamespace = "https://example.test|responses",
            IsNative = true, ExpiresAtUtcTicks = expiry };
        await client.Insertable(row).ExecuteCommandAsync();
        SetCiphertext("corrupt");
        await Derive(Deriver(_provider));
        var saved = (await client.Queryable<UpstreamReferenceOwner>().ToListAsync()).Single();
        saved.IsConflict.Should().BeTrue();
        saved.ExpiresAtUtcTicks.Should().Be(expiry);
        saved.OwnerAccountId.Should().Be(row.OwnerAccountId);
    }
    [Fact]
    public async Task Stale_legacy_upgrade_adopts_current_key_without_overwriting_it()
    {
        var oldKey = RandomNumberGenerator.GetBytes(32);
        InsertKey(Protector(_provider).Protect(oldKey));
        var before = Row();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(before.ProtectedKey)));
        var winner = RandomNumberGenerator.GetBytes(32);
        SetRaw(Convert.ToBase64String(winner));
        using var client = Client();
        var store = new SessionIsolationKeyStore(_provider.GetRequiredService<IServiceScopeFactory>(), _provider.GetRequiredService<IDataProtectionProvider>());
        var actual = await store.TryUpgradeToRawAsync(client, oldKey, before.ProtectedKey, fingerprint, default);
        actual.Should().Equal(winner);
        Row().ProtectedKey.Should().Be(Convert.ToBase64String(winner));
    }
    [Fact]
    public async Task Initialization_persists_raw_v2_key_directly_in_the_database()
    {
        var deriver = Deriver(_provider);
        var first = await Derive(deriver);
        first.Should().MatchRegex("^[0-9a-f]{64}$");
        var row = Row();
        row.Id.Should().Be(1);
        row.Version.Should().Be(SessionIsolationKey.CurrentVersion);
        // v2 = 裸 base64 直存：数据库文件本身就是这个密钥的持久化单元，不再有任何外部依赖。
        var key = Convert.FromBase64String(row.ProtectedKey);
        try
        {
            key.Should().HaveCount(32);
            row.ProtectedKey.Should().Be(Convert.ToBase64String(key));
            (await Derive(deriver)).Should().Be(first);
            Row().ProtectedKey.Should().Be(row.ProtectedKey);
            Rows().Should().ContainSingle();
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    [Fact]
    public async Task Startup_bootstrap_is_idempotent_and_corrupt_persisted_state_rotates_automatically()
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
        // A deployment that lost its Data Protection key ring must come back up on its own:
        // startup repairs the state by rotation instead of crash-looping until an operator
        // restores files that may no longer exist anywhere.
        SetCiphertext("corrupted");
        await restartedStore.EnsureInitializedAsync();
        Rows().Should().ContainSingle();
        Row().Version.Should().Be(SessionIsolationKey.CurrentVersion);
        Row().ProtectedKey.Should().NotBe("corrupted").And.NotBe(ciphertext);
        Convert.FromBase64String(Row().ProtectedKey).Should().HaveCount(32);
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
            // 经 legacy v1 密文行喂入密钥：既钉 HMAC 契约，也钉 v1 兼容解码路径。
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
            Row().Version.Should().Be(SessionIsolationKey.CurrentVersion, "v1 读取顺带原地升级为 v2");
            Row().ProtectedKey.Should().Be(Convert.ToBase64String(key));
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
    public async Task Restart_with_same_database_preserves_output_and_storage()
    {
        var first = await Derive(Deriver(_provider));
        var stored = Row().ProtectedKey;
        var restarted = CreateProvider();
        (await Derive(Deriver(restarted))).Should().Be(first);
        Row().ProtectedKey.Should().Be(stored);
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
    public async Task Corrupt_or_missing_secret_rotates_atomically_and_derivation_recovers(string corrupted)
    {
        var deriver = Deriver(_provider);
        var original = await Derive(deriver);
        SetCiphertext(corrupted);
        var repaired = await Derive(deriver);
        repaired.Should().HaveLength(64).And.NotBe(original);
        Rows().Should().ContainSingle();
        Row().ProtectedKey.Should().NotBe(corrupted);
        Convert.FromBase64String(Row().ProtectedKey).Should().HaveCount(32);
        (await Derive(Deriver(CreateProvider()))).Should().Be(repaired);
        Rows().Should().ContainSingle("the replacement commits as exactly one marker and key pair");
    }

    [Fact]
    public async Task Legacy_v1_row_upgrades_in_place_to_raw_v2_without_rotating()
    {
        // 部署库从 v1 密文升级：原地改写为裸 v2，密钥材料与全部派生标识保持不变。
        var legacyKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            InsertKey(Protector(_provider).Protect(legacyKey));
            var original = await Derive(Deriver(_provider));
            Row().Version.Should().Be(SessionIsolationKey.CurrentVersion);
            Row().ProtectedKey.Should().Be(Convert.ToBase64String(legacyKey));
            Rows().Should().ContainSingle();
            (await Derive(Deriver(_provider))).Should().Be(original, "升级幂等：第二次读不再改行");
            // v2 与 Data Protection 圈彻底无关：换一个全新圈照样直接读，不轮换。
            var otherRing = CreateProvider(keyRingPath: Path.Combine(_directory, "other-ring"));
            (await Derive(Deriver(otherRing))).Should().Be(original);
            Row().ProtectedKey.Should().Be(Convert.ToBase64String(legacyKey));
        }
        finally { CryptographicOperations.ZeroMemory(legacyKey); }
    }

    [Fact]
    public async Task Legacy_v1_row_under_lost_ring_or_wrong_purpose_rotates_to_v2_once()
    {
        // 钥匙圈真丢了（别机重部署）：v1 密文解不开 → 只轮换一次，产出 v2 裸存；
        // 之后任何圈（包括原来的）读的都是同一 v2 值，不再反复翻转。
        var legacyKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            InsertKey(Protector(_provider).Protect(legacyKey));
            var otherRing = CreateProvider(keyRingPath: Path.Combine(_directory, "other-ring"));
            var rotated = await Derive(Deriver(otherRing));
            rotated.Should().HaveLength(64);
            Row().Version.Should().Be(SessionIsolationKey.CurrentVersion);
            var rotatedRaw = Row().ProtectedKey;
            Convert.FromBase64String(rotatedRaw).Should().HaveCount(32);
            (await Derive(Deriver(otherRing))).Should().Be(rotated);
            (await Derive(Deriver(_provider))).Should().Be(rotated, "原圈回来后读到的也是 v2 裸值");
            Row().ProtectedKey.Should().Be(rotatedRaw);
        }
        finally { CryptographicOperations.ZeroMemory(legacyKey); }
        // 错 purpose 的 v1 密文（指纹同步）同样解不开 → 轮换一次到 v2。
        var wrongProtector = _provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("unrelated-purpose");
        var wrongCiphertext = Convert.ToBase64String(wrongProtector.Protect(RandomNumberGenerator.GetBytes(32)));
        SetLegacy(wrongCiphertext);
        var healed = await Derive(Deriver(_provider));
        healed.Should().HaveLength(64);
        Row().Version.Should().Be(SessionIsolationKey.CurrentVersion);
        Convert.FromBase64String(Row().ProtectedKey).Should().HaveCount(32);
        Rows().Should().ContainSingle();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public async Task Valid_v1_protection_of_wrong_key_length_rotates_instead_of_failing(int length)
    {
        InsertKey(Protector(_provider).Protect(new byte[length]));
        var healed = await Derive(Deriver(_provider));
        healed.Should().HaveLength(64);
        Convert.FromBase64String(Row().ProtectedKey).Should().HaveCount(32);
        Rows().Should().ContainSingle();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public async Task Raw_v2_key_of_wrong_length_rotates_instead_of_failing(int length)
    {
        SetRaw(Convert.ToBase64String(RandomNumberGenerator.GetBytes(length)));
        var healed = await Derive(Deriver(_provider));
        healed.Should().HaveLength(64);
        Convert.FromBase64String(Row().ProtectedKey).Should().HaveCount(32);
        Rows().Should().ContainSingle();
    }

    [Fact]
    public async Task Unknown_version_or_wrong_singleton_row_rotates_back_to_current_version()
    {
        await Derive(Deriver(_provider));
        using var client = Client();
        client.Ado.ExecuteCommand("UPDATE SessionIsolationKeys SET Version = 3;");
        await Derive(Deriver(_provider));
        Rows().Should().ContainSingle().Which.Version.Should().Be(SessionIsolationKey.CurrentVersion);
        client.Ado.ExecuteCommand("UPDATE SessionIsolationKeys SET Id = 2, Version = 1;");
        await Derive(Deriver(_provider));
        Rows().Should().ContainSingle().Which.Id.Should().Be(SessionIsolationKey.SingletonId);
        Rows().Should().ContainSingle("rotation never leaves a second row behind");
    }

    [Fact]
    public async Task Deleted_key_rows_self_heal_and_full_deletion_reinitializes()
    {
        var deriver = Deriver(_provider);
        var original = await Derive(deriver);
        using var client = Client();
        client.Deleteable<SessionIsolationKey>().ExecuteCommand();
        // Key row deleted while the marker remains: not a first deployment — the rotation
        // transaction restores the pair together instead of crash-looping forever.
        var healed = await Derive(deriver);
        healed.Should().HaveLength(64).And.NotBe(original);
        Rows().Should().ContainSingle();
        client.Queryable<SessionIsolationKeyMetadata>().ToList().Should().ContainSingle();
        var ciphertext = Row().ProtectedKey;
        (await Derive(Deriver(CreateProvider()))).Should().Be(healed);
        Row().ProtectedKey.Should().Be(ciphertext);
        // Deleting BOTH the marker and the key is indistinguishable from a first deployment;
        // the store reinitializes transparently instead of refusing to start.
        client.Deleteable<SessionIsolationKeyMetadata>().ExecuteCommand();
        client.Deleteable<SessionIsolationKey>().ExecuteCommand();
        var reinitialized = await Derive(Deriver(_provider));
        reinitialized.Should().HaveLength(64);
        Rows().Should().ContainSingle();
        Row().ProtectedKey.Should().NotBe(ciphertext);
    }

    [Fact]
    public async Task Missing_metadata_or_replaced_ciphertext_rotates_automatically()
    {
        var deriver = Deriver(_provider);
        var original = await Derive(deriver);
        using var client = Client();
        client.Deleteable<SessionIsolationKeyMetadata>().ExecuteCommand();
        var healedMarker = await Derive(Deriver(CreateProvider()));
        healedMarker.Should().HaveLength(64).And.NotBe(original);
        Rows().Should().ContainSingle();
        var ciphertext = Row().ProtectedKey;
        SetCiphertext(Convert.ToBase64String(Protector(_provider).Protect(RandomNumberGenerator.GetBytes(32))));
        var healedReplaced = await Derive(Deriver(_provider));
        healedReplaced.Should().HaveLength(64).And.NotBe(healedMarker);
        Row().ProtectedKey.Should().NotBe(ciphertext);
        Rows().Should().ContainSingle();
        (await Derive(Deriver(CreateProvider()))).Should().Be(healedReplaced);
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
    public async Task Cancellation_while_unlocking_legacy_row_is_observed_before_upgrade_write()
    {
        InsertKey(Protector(_provider).Protect(RandomNumberGenerator.GetBytes(32)));
        using var cancellation = new CancellationTokenSource();
        var provider = new CancelOnUnprotectProvider(_provider.GetRequiredService<IDataProtectionProvider>(), cancellation);
        var deriver = new SessionIsolationDeriver(new SessionIsolationKeyStore(
            _provider.GetRequiredService<IServiceScopeFactory>(), provider));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            deriver.DeriveAsync("client", "provider", _accountId, "session", "seed", cancellation.Token));
        provider.Unprotected.Should().NotBeNull().And.OnlyContain(b => b == 0, "取消后必须清理已解密的密钥");
        Rows().Should().ContainSingle().Which.Version.Should().Be(SessionIsolationKey.LegacyCiphertextVersion,
            "取消发生在解码之后、升级写回之前，行保持原样");
        (await Derive(Deriver(_provider))).Should().HaveLength(64);
        Row().Version.Should().Be(SessionIsolationKey.CurrentVersion);
        Rows().Should().ContainSingle();
    }

    private sealed class CancelOnUnprotectProvider(IDataProtectionProvider inner, CancellationTokenSource cancellation)
        : IDataProtectionProvider
    {
        public byte[]? Unprotected { get; private set; }
        public IDataProtector CreateProtector(string purpose) => new CancelOnUnprotect(inner.CreateProtector(purpose), cancellation, bytes => Unprotected = bytes);
    }

    private sealed class CancelOnUnprotect(IDataProtector inner, CancellationTokenSource cancellation, Action<byte[]> capture) : IDataProtector
    {
        public byte[]? Unprotected { get; private set; }
        public IDataProtector CreateProtector(string purpose) => new CancelOnUnprotect(inner.CreateProtector(purpose), cancellation, bytes => Unprotected = bytes);
        public byte[] Protect(byte[] plaintext) => inner.Protect(plaintext);
        public byte[] Unprotect(byte[] protectedData)
        {
            var result = inner.Unprotect(protectedData);
            capture(result);
            cancellation.Cancel();
            return result;
        }
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
        // Rotation clears derived owner identities in the same transaction, so the full
        // schema rotation touches must exist here exactly as production InitTables creates it.
        client.CodeFirst.InitTables(typeof(SessionIsolationKey), typeof(SessionIsolationKeyMetadata),
            typeof(UpstreamReferenceOwner));
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
    /// <summary>Seeds a legacy v1 row (Data Protection ciphertext) with a matching marker.</summary>
    private void InsertKey(byte[] protectedBytes)
    {
        using var client = Client();
        var ciphertext = Convert.ToBase64String(protectedBytes);
        client.Insertable(new SessionIsolationKey { Version = SessionIsolationKey.LegacyCiphertextVersion, ProtectedKey = ciphertext }).ExecuteCommand();
        client.Insertable(new SessionIsolationKeyMetadata
        {
            Version = SessionIsolationKey.LegacyCiphertextVersion,
            ProtectedKeyFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ciphertext)))
        }).ExecuteCommand();
    }
    /// <summary>Rewrites the existing row as a legacy v1 pair (marker fingerprint resynced).</summary>
    private void SetLegacy(string ciphertext)
    {
        using var client = Client();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ciphertext)));
        client.Ado.ExecuteCommand("UPDATE SessionIsolationKeys SET Version = 1, ProtectedKey = @k WHERE Id = 1;",
            new SugarParameter("@k", ciphertext));
        client.Ado.ExecuteCommand("UPDATE SessionIsolationKeyMetadata SET Version = 1, ProtectedKeyFingerprint = @f WHERE Id = 1;",
            new SugarParameter("@f", fingerprint));
    }
    /// <summary>Rewrites the existing row as a raw v2 pair (marker fingerprint resynced).</summary>
    private void SetRaw(string raw)
    {
        using var client = Client();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        client.Ado.ExecuteCommand("UPDATE SessionIsolationKeys SET Version = 2, ProtectedKey = @k WHERE Id = 1;",
            new SugarParameter("@k", raw));
        client.Ado.ExecuteCommand("UPDATE SessionIsolationKeyMetadata SET Version = 2, ProtectedKeyFingerprint = @f WHERE Id = 1;",
            new SugarParameter("@f", fingerprint));
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
