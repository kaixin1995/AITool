using System.Security.Cryptography;
using System.Text;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using SqlSugar;

namespace AITool.Web.Services;

/// <summary>
/// Owns the deployment's independent random 256-bit derivation key. Instances sharing a
/// database must also share their Data Protection application name and persistent key ring.
/// A bad ciphertext, unavailable protector, missing initialized key, or unknown version is
/// an error, never a rotation. Key and durable non-secret marker are committed together.
/// </summary>
public sealed class SessionIsolationKeyStore
{
    internal const int KeySizeBytes = 32;
    internal const string ProtectionPurpose = "AITool.SessionIsolation.KeyStorage.v1";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector;
    private int _hasObservedKey;

    public SessionIsolationKeyStore(IServiceScopeFactory scopeFactory, IDataProtectionProvider protectionProvider)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(protectionProvider);
        _scopeFactory = scopeFactory;
        _protector = protectionProvider.CreateProtector(ProtectionPurpose);
    }

    /// <summary>
    /// Initializes and validates the persisted key during application startup, before any
    /// request can hold a SQLite writer transaction. Bootstrap cannot acquire a second
    /// writer while another connection already holds that database's writer lock.
    /// </summary>
    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        var key = await ReadKeyAsync(ct);
        CryptographicOperations.ZeroMemory(key);
    }

    // The caller owns and must zero the returned temporary key. Request consumers use the
    // deriver, not raw deployment secrets. No plaintext/ciphertext cache masks DB corruption.
    internal async Task<byte[]> ReadKeyAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var scope = _scopeFactory.CreateScope();
        using var client = scope.ServiceProvider.GetRequiredService<AppDbContext>().Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var previouslyObserved = Volatile.Read(ref _hasObservedKey) != 0;
        // Marker first: a concurrent atomic initialization can become visible between reads,
        // but InitializeAsync will reread under a writer lock instead of trusting stale data.
        var metadata = await client.Queryable<SessionIsolationKeyMetadata>().Take(2).ToListAsync(ct);
        var rows = await client.Queryable<SessionIsolationKey>().Take(2).ToListAsync(ct);
        if (metadata.Count == 0 || rows.Count == 0)
        {
            if (previouslyObserved)
                throw Unavailable();
            await InitializeAsync(client, ct);
            metadata = await client.Queryable<SessionIsolationKeyMetadata>().Take(2).ToListAsync(ct);
            rows = await client.Queryable<SessionIsolationKey>().Take(2).ToListAsync(ct);
        }
        Validate(rows, metadata);
        Interlocked.Exchange(ref _hasObservedKey, 1);
        byte[] key;
        try
        {
            key = _protector.Unprotect(Convert.FromBase64String(rows[0].ProtectedKey));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Omit ciphertext, key material, and the protector's exception.
            throw Unavailable();
        }
        if (key.Length != KeySizeBytes || ct.IsCancellationRequested)
        {
            CryptographicOperations.ZeroMemory(key);
            ct.ThrowIfCancellationRequested();
            throw Unavailable();
        }
        return key;
    }

    private async Task InitializeAsync(ISqlSugarClient client, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var candidate = RandomNumberGenerator.GetBytes(KeySizeBytes);
        string ciphertext;
        try { ciphertext = Convert.ToBase64String(_protector.Protect(candidate)); }
        finally { CryptographicOperations.ZeroMemory(candidate); }
        ct.ThrowIfCancellationRequested();
        client.Ado.BeginTran();
        try
        {
            // Write first, before reading: SQLite arbitrates cross-process initialization
            // without a deferred read-to-write transaction upgrade or in-process lock.
            // IAdo raw SQL has no CT overload. Always await completion, then observe CT;
            // never abandon a task that can write after the connection/operation is disposed.
            var claimed = await client.Ado.ExecuteCommandAsync("""
                INSERT INTO SessionIsolationKeyMetadata (Id, Version, ProtectedKeyFingerprint)
                VALUES (@id, @version, @fingerprint)
                ON CONFLICT (Id) DO NOTHING;
                """, new[]
                {
                    new SugarParameter("@id", SessionIsolationKey.SingletonId),
                    new SugarParameter("@version", SessionIsolationKey.CurrentVersion),
                    new SugarParameter("@fingerprint", Fingerprint(ciphertext))
                });
            ct.ThrowIfCancellationRequested();
            var rows = await client.Queryable<SessionIsolationKey>().Take(2).ToListAsync(ct);
            if (claimed == 1)
            {
                // A key without its marker is damage, not permission to replace/bless it.
                if (rows.Count != 0)
                    throw Unavailable();
                await client.Ado.ExecuteCommandAsync("""
                    INSERT INTO SessionIsolationKeys (Id, Version, ProtectedKey)
                    VALUES (@id, @version, @protectedKey)
                    ON CONFLICT (Id) DO NOTHING;
                    """, new[]
                    {
                        new SugarParameter("@id", SessionIsolationKey.SingletonId),
                        new SugarParameter("@version", SessionIsolationKey.CurrentVersion),
                        new SugarParameter("@protectedKey", ciphertext)
                    });
                ct.ThrowIfCancellationRequested();
            }
            // A losing initializer must use the persisted winner; an existing marker with
            // no key is a hard failure, even for a brand-new process after restart.
            rows = await client.Queryable<SessionIsolationKey>().Take(2).ToListAsync(ct);
            var metadata = await client.Queryable<SessionIsolationKeyMetadata>().Take(2).ToListAsync(ct);
            Validate(rows, metadata);
            ct.ThrowIfCancellationRequested();
            client.Ado.CommitTran();
        }
        catch
        {
            client.Ado.RollbackTran();
            throw;
        }
    }

    private static void Validate(List<SessionIsolationKey> rows, List<SessionIsolationKeyMetadata> metadata)
    {
        if (rows.Count != 1 || rows[0].Id != SessionIsolationKey.SingletonId
            || rows[0].Version != SessionIsolationKey.CurrentVersion
            || string.IsNullOrWhiteSpace(rows[0].ProtectedKey) || rows[0].ProtectedKey.Length > 4096
            || metadata.Count != 1 || metadata[0].Id != SessionIsolationKey.SingletonId
            || metadata[0].Version != SessionIsolationKey.CurrentVersion
            || !StringComparer.Ordinal.Equals(metadata[0].ProtectedKeyFingerprint, Fingerprint(rows[0].ProtectedKey)))
            throw Unavailable();
    }

    private static string Fingerprint(string ciphertext)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ciphertext)));

    private static CryptographicException Unavailable()
        => new("The persisted session isolation key is unavailable or invalid. Restore the database and its Data Protection key ring; the key was not replaced.");
}
