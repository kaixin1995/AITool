using System.Security.Cryptography;
using System.Text;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace AITool.Web.Services;

/// <summary>
/// Owns the deployment's independent random 256-bit derivation key, stored raw in the
/// database (v2) so it travels with the database file through redeploys and restores. The
/// key is an HMAC salt for session identifier isolation, not storage encryption — the same
/// database already holds OAuth tokens in plaintext, so a Data Protection wrapper would add
/// a loss-prone external dependency (a key ring directory) without protecting anything.
/// Legacy v1 rows (DP ciphertext) are upgraded in place at first read without rotating.
/// Absent or unreadable state self-heals by atomic rotation: a fresh key, its marker, and
/// dependent persisted state (upstream reference ownership, whose legacy owner identities
/// are derived from this key) are committed together in one transaction.
/// </summary>
public sealed class SessionIsolationKeyStore
{
    internal const int KeySizeBytes = 32;
    internal const string ProtectionPurpose = "AITool.SessionIsolation.KeyStorage.v1";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector; // Legacy v1 unprotect only; new keys are raw.
    private readonly ILogger<SessionIsolationKeyStore>? _logger;

    public SessionIsolationKeyStore(IServiceScopeFactory scopeFactory, IDataProtectionProvider protectionProvider,
        ILogger<SessionIsolationKeyStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(protectionProvider);
        _scopeFactory = scopeFactory;
        _protector = protectionProvider.CreateProtector(ProtectionPurpose);
        _logger = logger;
    }

    /// <summary>
    /// Initializes or repairs the persisted key during application startup, before any
    /// request can hold a SQLite writer transaction. Bootstrap cannot acquire a second
    /// writer while another connection already holds that database's writer lock.
    /// </summary>
    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        var key = await ReadKeyAsync(ct);
        CryptographicOperations.ZeroMemory(key);
    }

    // The caller owns and must zero the returned temporary key. Request consumers use the
    // deriver, not raw deployment secrets. No plaintext cache masks DB damage: every read
    // revalidates, so damage self-heals on the next derive after it appears.
    internal async Task<byte[]> ReadKeyAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var scope = _scopeFactory.CreateScope();
        using var client = scope.ServiceProvider.GetRequiredService<AppDbContext>().Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var (rows, metadata) = await ReadStateAsync(client, ct);
        var (key, reason, legacy) = Unlock(rows, metadata);
        if (key is null)
        {
            LogRotation(reason);
            key = await RotateAsync(client, ct);
        }
        else if (legacy)
        {
            await TryUpgradeToRawAsync(client, key, ct);
        }
        ct.ThrowIfCancellationRequested();
        return key;
    }

    private void LogRotation(RepairReason reason)
    {
        if (reason == RepairReason.FreshDeployment)
        {
            _logger?.LogInformation("Session isolation key initialized for this deployment.");
            return;
        }
        _logger?.LogWarning(
            reason == RepairReason.UndecryptablePersistedKey
                ? "The persisted session isolation key is unreadable. Rotating automatically: derived session "
                + "identifiers change and persisted upstream reference ownership is cleared. Legacy v1 rows from a "
                + "deployment that lost its Data Protection key ring rotate once, then persist as raw v2 forever."
                : "The persisted session isolation key metadata is invalid. Rotating automatically: derived session "
                + "identifiers change and persisted upstream reference ownership is cleared.");
    }

    /// <summary>
    /// Rewrites a decryptable legacy v1 row as raw v2 in one transaction (key and marker
    /// together). The key material itself is unchanged, so derived session identifiers stay
    /// stable. Concurrent upgraders race on UPDATE ... WHERE Version=1; the loser simply
    /// observes the winner's identical result. Failure never blocks the current read: the
    /// v1 row stays decryptable and the upgrade is retried on the next startup.
    /// </summary>
    private async Task TryUpgradeToRawAsync(ISqlSugarClient client, byte[] key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var raw = Convert.ToBase64String(key);
        client.Ado.BeginTran();
        try
        {
            var upgraded = await client.Ado.ExecuteCommandAsync(
                "UPDATE SessionIsolationKeys SET Version = 2, ProtectedKey = @raw WHERE Id = 1 AND Version = 1;",
                new SugarParameter("@raw", raw));
            if (upgraded == 1)
                await client.Ado.ExecuteCommandAsync(
                    "UPDATE SessionIsolationKeyMetadata SET Version = 2, ProtectedKeyFingerprint = @fingerprint WHERE Id = 1 AND Version = 1;",
                    new SugarParameter("@fingerprint", Fingerprint(raw)));
            // IAdo raw SQL has no CT overload. Await fully, then observe CT before commit;
            // never abandon a task that can write after the connection is disposed.
            ct.ThrowIfCancellationRequested();
            client.Ado.CommitTran();
            _logger?.LogInformation(
                "Session isolation key upgraded in place from Data Protection ciphertext (v1) to raw storage (v2); the key material and all derived identifiers are unchanged.");
        }
        catch (OperationCanceledException)
        {
            try { client.Ado.RollbackTran(); } catch { }
            throw;
        }
        catch
        {
            try { client.Ado.RollbackTran(); } catch { }
            _logger?.LogWarning(
                "Upgrading the session isolation key from v1 ciphertext to raw v2 storage failed; the v1 row remains readable and the upgrade will be retried on the next startup.");
        }
    }

    private async Task<byte[]> RotateAsync(ISqlSugarClient client, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var candidate = RandomNumberGenerator.GetBytes(KeySizeBytes);
        // Raw v2 storage: the database file is this secret's persistence unit.
        var stored = Convert.ToBase64String(candidate);
        CryptographicOperations.ZeroMemory(candidate);
        ct.ThrowIfCancellationRequested();
        client.Ado.BeginTran();
        try
        {
            // Writer-first claim: SQLite arbitrates concurrent initializations and repairs
            // without a deferred read-to-write transaction upgrade or in-process lock. A
            // claimant that finds the marker already present adopts the persisted winner it
            // raced with instead of overwriting it, when that winner is usable.
            var claimed = await client.Ado.ExecuteCommandAsync("""
                INSERT INTO SessionIsolationKeyMetadata (Id, Version, ProtectedKeyFingerprint)
                VALUES (@id, @version, @fingerprint)
                ON CONFLICT (Id) DO NOTHING;
                """, new[]
            {
                new SugarParameter("@id", SessionIsolationKey.SingletonId),
                new SugarParameter("@version", SessionIsolationKey.CurrentVersion),
                new SugarParameter("@fingerprint", Fingerprint(stored))
            });
            ct.ThrowIfCancellationRequested();
            var (rows, metadata) = await ReadStateAsync(client, ct);
            var (adopted, _, _) = Unlock(rows, metadata);
            if (claimed == 0 && adopted is not null)
            {
                CryptographicOperations.ZeroMemory(adopted);
                ct.ThrowIfCancellationRequested();
                client.Ado.CommitTran();
                // Reread from the committed state instead of trusting the transaction-local copy.
                var (currentRows, currentMetadata) = await ReadStateAsync(client, ct);
                var (winner, _, _) = Unlock(currentRows, currentMetadata);
                return winner ?? throw new CryptographicException(RotationUnreadableMessage);
            }
            // Replace any absent or damaged state atomically. Key and marker are committed
            // together, so no intermediate state reads as a valid deployment. Derived owner
            // identities become stale on rotation; stale rows would sticky-conflict until
            // their TTL, so they are cleared inside the same transaction.
            await client.Deleteable<SessionIsolationKeyMetadata>().ExecuteCommandAsync(ct);
            await client.Deleteable<SessionIsolationKey>().ExecuteCommandAsync(ct);
            await client.Deleteable<UpstreamReferenceOwner>().ExecuteCommandAsync(ct);
            ct.ThrowIfCancellationRequested();
            await client.Ado.ExecuteCommandAsync("""
                INSERT INTO SessionIsolationKeyMetadata (Id, Version, ProtectedKeyFingerprint)
                VALUES (@id, @version, @fingerprint);
                """, new[]
            {
                new SugarParameter("@id", SessionIsolationKey.SingletonId),
                new SugarParameter("@version", SessionIsolationKey.CurrentVersion),
                new SugarParameter("@fingerprint", Fingerprint(stored))
            });
            ct.ThrowIfCancellationRequested();
            await client.Ado.ExecuteCommandAsync("""
                INSERT INTO SessionIsolationKeys (Id, Version, ProtectedKey)
                VALUES (@id, @version, @protectedKey);
                """, new[]
            {
                new SugarParameter("@id", SessionIsolationKey.SingletonId),
                new SugarParameter("@version", SessionIsolationKey.CurrentVersion),
                new SugarParameter("@protectedKey", stored)
            });
            ct.ThrowIfCancellationRequested();
            client.Ado.CommitTran();
        }
        catch
        {
            client.Ado.RollbackTran();
            throw;
        }
        // Post-commit verification on a fresh read: a store that cannot read back what it
        // just wrote stays fail-closed instead of rotating in a loop.
        var (finalRows, finalMetadata) = await ReadStateAsync(client, ct);
        var (key, _, _) = Unlock(finalRows, finalMetadata);
        return key ?? throw new CryptographicException(RotationUnreadableMessage);
    }

    private const string RotationUnreadableMessage =
        "The session isolation key was rotated but the replacement cannot be read back; the key storage is not functional.";

    private async Task<(List<SessionIsolationKey> Rows, List<SessionIsolationKeyMetadata> Metadata)> ReadStateAsync(
        ISqlSugarClient client, CancellationToken ct)
    {
        // Marker first: a concurrent atomic initialization can become visible between reads,
        // but rotation rereads under the writer lock instead of trusting stale data.
        var metadata = await client.Queryable<SessionIsolationKeyMetadata>().Take(2).ToListAsync(ct);
        var rows = await client.Queryable<SessionIsolationKey>().Take(2).ToListAsync(ct);
        return (rows, metadata);
    }

    private enum RepairReason
    {
        None,
        FreshDeployment,
        InvalidPersistedState,
        UndecryptablePersistedKey
    }

    // Returns the deployment key (with a flag when it came from a legacy v1 row), or the
    // reason the persisted state cannot serve one.
    private (byte[]? Key, RepairReason Failure, bool Legacy) Unlock(
        List<SessionIsolationKey> rows, List<SessionIsolationKeyMetadata> metadata)
    {
        if (rows.Count == 0 && metadata.Count == 0)
            return (null, RepairReason.FreshDeployment, false);
        if (rows.Count != 1 || rows[0].Id != SessionIsolationKey.SingletonId
            || rows[0].Version is not (SessionIsolationKey.LegacyCiphertextVersion or SessionIsolationKey.CurrentVersion)
            || string.IsNullOrWhiteSpace(rows[0].ProtectedKey) || rows[0].ProtectedKey.Length > 4096
            || metadata.Count != 1 || metadata[0].Id != SessionIsolationKey.SingletonId
            || metadata[0].Version != rows[0].Version
            || !StringComparer.Ordinal.Equals(metadata[0].ProtectedKeyFingerprint, Fingerprint(rows[0].ProtectedKey)))
            return (null, RepairReason.InvalidPersistedState, false);
        // Omit ciphertext, key material, and the protector's exception details on failure.
        byte[] key;
        try
        {
            key = rows[0].Version == SessionIsolationKey.LegacyCiphertextVersion
                ? _protector.Unprotect(Convert.FromBase64String(rows[0].ProtectedKey))
                : Convert.FromBase64String(rows[0].ProtectedKey);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return (null, RepairReason.UndecryptablePersistedKey, false);
        }
        if (key.Length != KeySizeBytes)
        {
            CryptographicOperations.ZeroMemory(key);
            return (null, RepairReason.UndecryptablePersistedKey, false);
        }
        return (key, RepairReason.None, rows[0].Version == SessionIsolationKey.LegacyCiphertextVersion);
    }

    private static string Fingerprint(string ciphertext)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ciphertext)));
}
