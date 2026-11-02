using AITool.Application.Proxy;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using SqlSugar;

namespace AITool.Web.Services;

/// <summary>
/// Independent connections and SQLite writer-first transactions arbitrate across store instances/processes.
/// Fixed TTL from first commit (default 24h): reads/same-owner commits never renew it. Conflict is sticky
/// until that original expiry. Bounded expired-row cleanup runs on writes; reads always enforce expiry.
/// </summary>
public sealed class UpstreamReferenceOwnerStore : IUpstreamReferenceOwnerStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;
    private long _nextCleanupAtUtcTicks;
    public const int MaxReferences = 256;

    public UpstreamReferenceOwnerStore(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, TimeSpan? ttl = null)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _ttl = ttl ?? TimeSpan.FromHours(24);
        if (_ttl <= TimeSpan.Zero || _ttl > TimeSpan.FromDays(30))
            throw new ArgumentOutOfRangeException(nameof(ttl));
    }

    public async Task<UpstreamReferenceRestriction?> ResolveAsync(string clientScope,
        IReadOnlyCollection<UpstreamReferenceLookup> references, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateScope(clientScope);
        ValidateReferences(references);
        if (references.Count == 0) return null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            using var client = Open(scope);
            client.Ado.BeginTran();
            try
            {
                var now = _timeProvider.GetUtcNow().UtcTicks;
                UpstreamReferenceRestriction? result = null;
                foreach (var reference in references.Distinct())
                {
                    var row = await FindAsync(client, clientScope, reference, ct);
                    if (row is null || row.ExpiresAtUtcTicks <= now)
                        throw Error("unknown_reference", "The upstream reference is unknown or expired.");
                    if (row.IsConflict || row.ExposureStatus != UpstreamReferenceExposureStatus.CommittedForExposure)
                        throw Error("reference_conflict", "The upstream reference has conflicting ownership.");
                    if (!row.IsNative)
                        throw Error("reference_replay_required", "This synthetic reference requires transcript replay, which is unavailable.");
                    var current = new UpstreamReferenceRestriction(
                        new(row.OwnerProviderKey, row.OwnerAccountId), row.UpstreamNamespace);
                    ValidateContext(new(clientScope, current.Owner, current.UpstreamNamespace));
                    if (result is not null && result != current)
                        throw Error("reference_owner_mismatch", "The request references different upstream owners or namespaces.");
                    result = current;
                }
                ct.ThrowIfCancellationRequested();
                client.Ado.CommitTran();
                return result;
            }
            catch { client.Ado.RollbackTran(); throw; }
        }
        catch (OperationCanceledException) { throw; }
        catch (UpstreamReferenceException) { throw; }
        catch { throw Unavailable(); }
    }

    public async Task CommitAsync(UpstreamReferenceContext context,
        IReadOnlyCollection<UpstreamReferenceLookup> references, bool native, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateContext(context);
        ValidateReferences(references);
        // Even an id-free SSE event is an exposure boundary, but it needs no writer lock.
        if (references.Count == 0)
        {
            await CheckAvailableAsync(ct);
            return;
        }
        bool conflict = false;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            using var client = Open(scope);
            client.Ado.BeginTran();
            try
            {
                // First statement MUST be a write, before checking any existing ownership.
                // Raw Ado has no CT overload: await fully, then observe cancellation before commit.
                await client.Ado.ExecuteCommandAsync("UPDATE UpstreamReferenceOwners SET IsConflict=IsConflict WHERE 1=0;");
                ct.ThrowIfCancellationRequested();
                var now = _timeProvider.GetUtcNow().UtcTicks;
                if (ClaimCleanup(now))
                {
                    // At most one bounded sweep per minute per store, only on identifier commits.
                    await client.Ado.ExecuteCommandAsync("""
                        DELETE FROM UpstreamReferenceOwners WHERE rowid IN
                        (SELECT rowid FROM UpstreamReferenceOwners WHERE ExpiresAtUtcTicks <= @now LIMIT 128);
                        """, new[] { new SugarParameter("@now", now) });
                    ct.ThrowIfCancellationRequested();
                }
                foreach (var reference in references.Distinct())
                {
                    var row = await FindAsync(client, context.ClientScope, reference, ct);
                    if (row is not null && row.ExpiresAtUtcTicks <= now)
                    {
                        await client.Deleteable<UpstreamReferenceOwner>().Where(p => p.ClientScope == context.ClientScope
                            && p.Kind == reference.Kind && p.LookupDigest == reference.LookupDigest).ExecuteCommandAsync(ct);
                        row = null;
                    }
                    if (row is null)
                    {
                        await client.Insertable(new UpstreamReferenceOwner
                        {
                            ClientScope = context.ClientScope, Kind = reference.Kind, LookupDigest = reference.LookupDigest,
                            OwnerProviderKey = context.Owner.ProviderKey, OwnerAccountId = context.Owner.AccountId,
                            UpstreamNamespace = context.UpstreamNamespace, IsNative = native,
                            ExposureStatus = UpstreamReferenceExposureStatus.CommittedForExposure,
                            CreatedAtUtcTicks = now, ExpiresAtUtcTicks = checked(now + _ttl.Ticks)
                        }).ExecuteCommandAsync(ct);
                    }
                    else if (row.IsConflict || row.OwnerProviderKey != context.Owner.ProviderKey
                        || row.OwnerAccountId != context.Owner.AccountId || row.UpstreamNamespace != context.UpstreamNamespace
                        || row.IsNative != native || row.ExposureStatus != UpstreamReferenceExposureStatus.CommittedForExposure)
                    {
                        // Persist the tombstone, THEN throw outside the committed transaction. No last writer wins.
                        row.IsConflict = true;
                        await client.Updateable(row).ExecuteCommandAsync(ct);
                        conflict = true;
                    }
                    // Same owner/native/namespace: deliberately do not renew expiry or clear conflict.
                }
                ct.ThrowIfCancellationRequested();
                client.Ado.CommitTran();
            }
            catch { client.Ado.RollbackTran(); throw; }
        }
        catch (OperationCanceledException) { throw; }
        catch (UpstreamReferenceException) { throw; }
        catch { throw Unavailable(); }
        if (conflict) throw Error("reference_conflict", "The upstream reference has conflicting ownership.");
    }

    private async Task CheckAvailableAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            using var client = Open(scope);
            // Selecting all mapped columns verifies the actual ownership table/schema, including
            // an empty table. No write/transaction/cleanup; streaming deltas do not serialize writers.
            await client.Queryable<UpstreamReferenceOwner>().Take(1).ToListAsync(ct);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { throw; }
        catch { throw Unavailable(); }
    }

    private bool ClaimCleanup(long now)
    {
        while (true)
        {
            var next = Volatile.Read(ref _nextCleanupAtUtcTicks);
            if (now < next) return false;
            if (Interlocked.CompareExchange(ref _nextCleanupAtUtcTicks,
                    checked(now + TimeSpan.TicksPerMinute), next) == next) return true;
        }
    }

    private static Task<UpstreamReferenceOwner> FindAsync(ISqlSugarClient client, string scope,
        UpstreamReferenceLookup reference, CancellationToken ct) => client.Queryable<UpstreamReferenceOwner>()
        .FirstAsync(p => p.ClientScope == scope && p.Kind == reference.Kind && p.LookupDigest == reference.LookupDigest, ct);

    private static ISqlSugarClient Open(IServiceScope scope)
    {
        var client = scope.ServiceProvider.GetRequiredService<AppDbContext>().Client.CopyNew();
        try { client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;"); return client; }
        catch { client.Dispose(); throw; }
    }

    internal static void ValidateScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Length > 256)
            throw Error("invalid_reference_context", "A trusted client scope is required.");
    }

    internal static void ValidateContext(UpstreamReferenceContext context)
    {
        if (context is null) throw Error("invalid_reference_context", "The upstream reference context is invalid.");
        ValidateScope(context.ClientScope);
        if (context.Owner is null || context.Owner.AccountId == Guid.Empty
            || string.IsNullOrWhiteSpace(context.Owner.ProviderKey) || context.Owner.ProviderKey.Length > 100
            || context.Owner.ProviderKey.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            || string.IsNullOrEmpty(context.UpstreamNamespace) || context.UpstreamNamespace.Length > 2048)
            throw Error("invalid_reference_context", "The upstream reference context is invalid.");
        var split = context.UpstreamNamespace.Split('|');
        if (split.Length != 2 || UpstreamReferenceNamespace.Create(split[0], split[1]) != context.UpstreamNamespace)
            throw Error("invalid_reference_context", "The upstream reference namespace is not canonical.");
        if (context.Restriction is not null && (context.Owner != context.Restriction.Owner
                || context.UpstreamNamespace != context.Restriction.UpstreamNamespace))
            throw Error("reference_owner_mismatch", "The selected upstream does not match the reference restriction.");
    }

    private static void ValidateReferences(IReadOnlyCollection<UpstreamReferenceLookup> references)
    {
        if (references is null || references.Count > MaxReferences || references.Any(r => r is null
                || !Enum.IsDefined(r.Kind) || r.LookupDigest is null || r.LookupDigest.Length != 64
                || r.LookupDigest.Any(c => !(char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))))
            throw Error("invalid_reference_shape", "The upstream reference set is invalid or exceeds its limit.");
    }

    private static UpstreamReferenceException Error(string code, string message) => new(code, message);
    private static UpstreamReferenceException Unavailable() => Error("reference_store_unavailable", "Upstream reference ownership could not be persisted or verified.");
}
