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
/// Owns the deployment's independent random 256-bit derivation key. By default it writes
/// v1 Data Protection ciphertext compatible with older instances. After a coordinated
/// cutover, opt-in v2 stores raw bytes in the database without an external key ring.
/// Switching between formats preserves key material when the original ring is available.
/// Absent or unreadable state self-heals by atomic rotation: a fresh key, its marker, and
/// dependent persisted state (upstream reference ownership, whose legacy owner identities
/// are derived from this key) are committed together in one transaction.
/// </summary>
public sealed class SessionIsolationKeyStore
{
    internal const int KeySizeBytes = 32;
    internal const string ProtectionPurpose = "AITool.SessionIsolation.KeyStorage.v1";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector;
    private readonly ILogger<SessionIsolationKeyStore>? _logger;
    private readonly bool _rawStorageEnabled;

    public SessionIsolationKeyStore(IServiceScopeFactory scopeFactory, IDataProtectionProvider protectionProvider,
        ILogger<SessionIsolationKeyStore>? logger = null, bool rawStorageEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(protectionProvider);
        _scopeFactory = scopeFactory;
        _protector = protectionProvider.CreateProtector(ProtectionPurpose);
        _logger = logger;
        _rawStorageEnabled = rawStorageEnabled;
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

    internal static string ComputeGeneration(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexString(SHA256.HashData(key));
    }

    internal async Task<string?> TryReadCurrentGenerationAsync(ISqlSugarClient client, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(client);
        var (rows, metadata) = await ReadStateAsync(client, ct);
        var (key, _, _) = Unlock(rows, metadata);
        if (key is null) return null;
        try
        {
            return ComputeGeneration(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
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
        try
        {
            if (key is null)
            {
                LogRotation(reason);
                key = await RotateAsync(client, ct);
            }
            else if (legacy && _rawStorageEnabled)
            {
                var legacyKey = key;
                key = null;
                try { key = await TryUpgradeToRawAsync(client, legacyKey, rows[0].ProtectedKey, metadata[0].ProtectedKeyFingerprint, ct); }
                finally { CryptographicOperations.ZeroMemory(legacyKey); }
            }
            else if (!legacy && !_rawStorageEnabled)
            {
                var rawKey = key;
                key = null;
                try { key = await ConvertToCompatibleAsync(client, rawKey, rows[0].ProtectedKey, metadata[0].ProtectedKeyFingerprint, ct); }
                finally { CryptographicOperations.ZeroMemory(rawKey); }
            }
            ct.ThrowIfCancellationRequested();
            var result = key;
            key = null; // Ownership transfers only after the final cancellation check.
            return result;
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
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
                + "identifiers change and persisted upstream references are invalidated until expiry. Legacy v1 rows from a "
                + "deployment that lost its Data Protection key ring rotate once using the configured storage format."
                : "The persisted session isolation key metadata is invalid. Rotating automatically: derived session "
                + "identifiers change and persisted upstream references are invalidated until expiry.");
    }

    /// <summary>
    /// Rewrites a decryptable legacy v1 row as raw v2 in one transaction (key and marker
    /// together). The key material itself is unchanged, so derived session identifiers stay
    /// stable. Concurrent upgraders race on UPDATE ... WHERE Version=1; the loser simply
    /// observes the winner's identical result. Failure never blocks the current read: the
    /// v1 row stays decryptable and the upgrade is retried on the next startup.
    /// </summary>
    internal async Task<byte[]> TryUpgradeToRawAsync(ISqlSugarClient client, byte[] key, string oldCiphertext,
        string oldFingerprint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var raw = Convert.ToBase64String(key);
        var transactionStarted = false;
        try
        {
            client.Ado.BeginTran();
            transactionStarted = true;
            var upgraded = await client.Ado.ExecuteCommandAsync("""
                UPDATE SessionIsolationKeys SET Version = 2, ProtectedKey = @raw
                WHERE Id = 1 AND Version = 1 AND ProtectedKey = @old
                  AND EXISTS (SELECT 1 FROM SessionIsolationKeyMetadata WHERE Id = 1 AND Version = 1 AND ProtectedKeyFingerprint = @fingerprint);
                """, new SugarParameter("@raw", raw), new SugarParameter("@old", oldCiphertext),
                new SugarParameter("@fingerprint", oldFingerprint));
            if (upgraded == 1)
            {
                var marker = await client.Ado.ExecuteCommandAsync("""
                    UPDATE SessionIsolationKeyMetadata SET Version = 2, ProtectedKeyFingerprint = @newFingerprint
                    WHERE Id = 1 AND Version = 1 AND ProtectedKeyFingerprint = @oldFingerprint;
                    """, new SugarParameter("@newFingerprint", Fingerprint(raw)), new SugarParameter("@oldFingerprint", oldFingerprint));
                if (marker != 1) throw new InvalidOperationException("Session key marker changed during upgrade.");
            }
            ct.ThrowIfCancellationRequested();
            client.Ado.CommitTran();
            transactionStarted = false;
        }
        catch (OperationCanceledException)
        {
            if (transactionStarted)
            {
                try { client.Ado.RollbackTran(); } catch { }
            }
            throw;
        }
        catch
        {
            if (transactionStarted)
            {
                try { client.Ado.RollbackTran(); } catch { }
            }
            _logger?.LogWarning("Upgrading the legacy session isolation key failed; re-reading the persisted state.");
        }
        // Another writer may have rotated/replaced the row even when our conditional update
        // affected zero rows. Never return the pre-upgrade snapshot without revalidating it.
        var (rows, metadata) = await ReadStateAsync(client, ct);
        var (current, reason, _) = Unlock(rows, metadata);
        if (current is not null) return current;
        LogRotation(reason);
        return await RotateAsync(client, ct);
    }
    // Before rolling back to a v1-only binary, run this release in compatibility mode
    // with all v2 writers stopped. Unlike an upgrade failure, a failed downgrade must
    // fail closed: returning a v2 row would allow an old instance to rotate it.
    private async Task<byte[]> ConvertToCompatibleAsync(ISqlSugarClient client, byte[] key, string raw,
        string fingerprint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ciphertext = Convert.ToBase64String(_protector.Protect(key));
        client.Ado.BeginTran();
        try
        {
            var updated = await client.Ado.ExecuteCommandAsync("""
                UPDATE SessionIsolationKeys SET Version = 1, ProtectedKey = @ciphertext
                WHERE Id = 1 AND Version = 2 AND ProtectedKey = @raw
                  AND EXISTS (SELECT 1 FROM SessionIsolationKeyMetadata WHERE Id = 1 AND Version = 2 AND ProtectedKeyFingerprint = @fingerprint);
                """, new SugarParameter("@ciphertext", ciphertext), new SugarParameter("@raw", raw),
                new SugarParameter("@fingerprint", fingerprint));
            if (updated == 1)
            {
                var marker = await client.Ado.ExecuteCommandAsync("""
                    UPDATE SessionIsolationKeyMetadata SET Version = 1, ProtectedKeyFingerprint = @newFingerprint
                    WHERE Id = 1 AND Version = 2 AND ProtectedKeyFingerprint = @oldFingerprint;
                    """, new SugarParameter("@newFingerprint", Fingerprint(ciphertext)), new SugarParameter("@oldFingerprint", fingerprint));
                if (marker != 1) throw new InvalidOperationException("Session key marker changed during downgrade.");
            }
            ct.ThrowIfCancellationRequested();
            client.Ado.CommitTran();
        }
        catch
        {
            client.Ado.RollbackTran();
            throw;
        }
        var (rows, metadata) = await ReadStateAsync(client, ct);
        var (current, _, legacy) = Unlock(rows, metadata);
        if (current is not null && legacy) return current;
        if (current is not null) CryptographicOperations.ZeroMemory(current);
        throw new InvalidOperationException("Session isolation key could not be converted to v1; stop all v2 writers before rollback.");
    }

    private async Task<byte[]> RotateAsync(ISqlSugarClient client, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var candidate = RandomNumberGenerator.GetBytes(KeySizeBytes);
        // Compatibility mode writes exactly the v1 ciphertext understood by old instances.
        // Raw v2 is opt-in only after every old instance has stopped.
        string stored;
        try { stored = Convert.ToBase64String(_rawStorageEnabled ? candidate : _protector.Protect(candidate)); }
        finally { CryptographicOperations.ZeroMemory(candidate); }
        var storageVersion = _rawStorageEnabled ? SessionIsolationKey.CurrentVersion : SessionIsolationKey.LegacyCiphertextVersion;
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
                new SugarParameter("@version", storageVersion),
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
                // A concurrent v2 writer must not leave compatibility mode claiming a
                // successful startup with a row that an old binary would rotate.
                var (currentRows, currentMetadata) = await ReadStateAsync(client, ct);
                var (winner, _, winnerLegacy) = Unlock(currentRows, currentMetadata);
                if (winner is null) throw new CryptographicException(RotationUnreadableMessage);
                if (!_rawStorageEnabled && !winnerLegacy)
                {
                    try { return await ConvertToCompatibleAsync(client, winner, currentRows[0].ProtectedKey, currentMetadata[0].ProtectedKeyFingerprint, ct); }
                    finally { CryptographicOperations.ZeroMemory(winner); }
                }
                return winner;
            }
            // Replace any absent or damaged state atomically. Key and marker are committed
            // together, so no intermediate state reads as a valid deployment. Derived owner
            // identities become stale on rotation; retain conflict tombstones until
            // their TTL, so old identifiers cannot be reassigned to another owner.
            await client.Deleteable<SessionIsolationKeyMetadata>().ExecuteCommandAsync(ct);
            await client.Deleteable<SessionIsolationKey>().ExecuteCommandAsync(ct);
            await client.Updateable<UpstreamReferenceOwner>()
                .SetColumns(x => new UpstreamReferenceOwner { IsConflict = true }).Where(x => !x.IsConflict).ExecuteCommandAsync(ct);
            ct.ThrowIfCancellationRequested();
            await client.Ado.ExecuteCommandAsync("""
                INSERT INTO SessionIsolationKeyMetadata (Id, Version, ProtectedKeyFingerprint)
                VALUES (@id, @version, @fingerprint);
                """, new[]
            {
                new SugarParameter("@id", SessionIsolationKey.SingletonId),
                new SugarParameter("@version", storageVersion),
                new SugarParameter("@fingerprint", Fingerprint(stored))
            });
            ct.ThrowIfCancellationRequested();
            await client.Ado.ExecuteCommandAsync("""
                INSERT INTO SessionIsolationKeys (Id, Version, ProtectedKey)
                VALUES (@id, @version, @protectedKey);
                """, new[]
            {
                new SugarParameter("@id", SessionIsolationKey.SingletonId),
                new SugarParameter("@version", storageVersion),
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
