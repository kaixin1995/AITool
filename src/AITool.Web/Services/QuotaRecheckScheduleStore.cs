using AITool.Application.Accounts;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using SqlSugar;

namespace AITool.Web.Services;

/// <summary>
/// SQLite-backed work queue. Every operation owns its connection; no database lock survives
/// into an upstream request. Atomic conditional updates, not process locks, arbitrate claims.
/// </summary>
public sealed class QuotaRecheckScheduleStore(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IQuotaRecheckIntervalSource intervals) : IQuotaRecheckScheduleStore
{
    // Inspector requests must have a shorter total timeout (currently two minutes).
    // A crashed worker becomes claimable at this deadline, without another 40–60 minute delay.
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    public async Task EnsureScheduledAsync(string providerKey, Guid accountId, DateTimeOffset? lastQuotaCheckedAt,
        CancellationToken cancellationToken)
    {
        ValidateProvider(providerKey);
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = scopeFactory.CreateScope();
        using var client = OpenClient(scope);
        await InsertMissingAsync(client, providerKey, accountId, lastQuotaCheckedAt, cancellationToken);
    }

    public async Task<QuotaRecheckObservation> BeginReconciliationAsync(string providerKey, CancellationToken cancellationToken)
    {
        ValidateProvider(providerKey);
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = scopeFactory.CreateScope();
        using var client = OpenClient(scope);
        var rows = await client.Queryable<QuotaRecheckSchedule>()
            .Where(s => s.ProviderKey == providerKey).ToListAsync(cancellationToken);
        return new QuotaRecheckObservation(providerKey, rows.ToDictionary(s => s.AccountId, s => s.Generation));
    }

    // Without a pre-listing observation there is no safe deletion authority. This overload
    // remains useful to initialize legacy schedules and supports older interface consumers.
    public Task<IReadOnlyList<QuotaRecheckScheduleInfo>> ReconcileAsync(string providerKey,
        IReadOnlyList<AccountQuotaTarget> eligibleAccounts, CancellationToken cancellationToken)
        => ReconcileAsync(providerKey, eligibleAccounts,
            new QuotaRecheckObservation(providerKey, new Dictionary<Guid, string>()), cancellationToken);

    public async Task<IReadOnlyList<QuotaRecheckScheduleInfo>> ReconcileAsync(string providerKey,
        IReadOnlyList<AccountQuotaTarget> eligibleAccounts, QuotaRecheckObservation observation,
        CancellationToken cancellationToken)
    {
        ValidateProvider(providerKey);
        ArgumentNullException.ThrowIfNull(observation);
        if (!StringComparer.Ordinal.Equals(providerKey, observation.ProviderKey))
            throw new ArgumentException("Observation belongs to a different provider.", nameof(observation));
        ArgumentNullException.ThrowIfNull(eligibleAccounts);
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = scopeFactory.CreateScope();
        using var client = OpenClient(scope);
        var accounts = eligibleAccounts.DistinctBy(a => a.AccountId).ToArray();
        var ids = accounts.Select(a => a.AccountId).ToHashSet();

        client.Ado.BeginTran();
        try
        {
            // A listing can already be stale when it returns. Never delete rows inserted,
            // re-disabled, claimed or completed since its pre-listing observation, nor any
            // active lease (even if acquired before that observation). A later successful
            // fresh listing can clean them up after completion or crash expiry.
            foreach (var observed in observation.Generations.Where(s => !ids.Contains(s.Key)))
            {
                var accountId = observed.Key;
                var generation = observed.Value;
                var now = timeProvider.GetUtcNow().UtcTicks;
                await client.Deleteable<QuotaRecheckSchedule>()
                    .Where(s => s.ProviderKey == providerKey && s.AccountId == accountId && s.Generation == generation)
                    .Where(s => s.LeaseToken == null || s.LeaseExpiresAtUtcTicks <= now)
                    .ExecuteCommandAsync(cancellationToken);
            }

            // Insert before reading so any non-empty transaction acquires its writer lock
            // without a SQLite deferred-transaction read-to-write upgrade race. A conflict
            // is not an Ensure event and must not change the existing row's generation.
            foreach (var account in accounts)
                await InsertMissingAsync(client, providerKey, account.AccountId, account.LastQuotaCheckedAt, cancellationToken,
                    touchExisting: false);

            var result = await client.Queryable<QuotaRecheckSchedule>()
                .Where(s => s.ProviderKey == providerKey).ToListAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            client.Ado.CommitTran();
            return result.Select(s => new QuotaRecheckScheduleInfo(s.ProviderKey, s.AccountId,
                new DateTimeOffset(s.NextCheckAtUtcTicks, TimeSpan.Zero))).ToArray();
        }
        catch
        {
            client.Ado.RollbackTran();
            throw;
        }
    }

    public async Task<QuotaRecheckLease?> TryAcquireAsync(string providerKey, Guid accountId,
        CancellationToken cancellationToken, bool force = false)
    {
        ValidateProvider(providerKey);
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = scopeFactory.CreateScope();
        using var client = OpenClient(scope);
        var now = timeProvider.GetUtcNow().UtcTicks;
        var deadline = now + LeaseDuration.Ticks;
        var token = Guid.NewGuid();
        var tokenText = token.ToString("D");
        var updated = await client.Updateable<QuotaRecheckSchedule>()
            .SetColumns(s => new QuotaRecheckSchedule
            {
                Generation = tokenText,
                LeaseToken = tokenText,
                LeaseExpiresAtUtcTicks = deadline,
                NextCheckAtUtcTicks = deadline,
                UpdatedAtUtcTicks = now
            })
            .Where(s => s.ProviderKey == providerKey && s.AccountId == accountId)
            .Where(s => force || s.NextCheckAtUtcTicks <= now)
            .Where(s => s.LeaseToken == null || s.LeaseExpiresAtUtcTicks <= now)
            .ExecuteCommandAsync(cancellationToken);
        return updated == 1 ? new QuotaRecheckLease(providerKey, accountId, token) : null;
    }

    public async Task CompleteAsync(QuotaRecheckLease lease, bool keepScheduled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = scopeFactory.CreateScope();
        using var client = OpenClient(scope);
        var token = lease.Token.ToString("D");
        var now = timeProvider.GetUtcNow().UtcTicks;
        var generation = Guid.NewGuid().ToString("D");
        var next = now + intervals.NextInterval().Ticks;
        // An expired worker no longer owns the work, even if a new worker has not claimed it yet.
        if (!keepScheduled)
        {
            // Claim stamps Generation with its lease token. Ensure on an existing row changes
            // only Generation: that newer disable event must survive an older worker's removal.
            await client.Deleteable<QuotaRecheckSchedule>()
                .Where(s => s.ProviderKey == lease.ProviderKey && s.AccountId == lease.AccountId
                    && s.LeaseToken == token && s.LeaseExpiresAtUtcTicks > now && s.Generation == token)
                .ExecuteCommandAsync(cancellationToken);
            await client.Updateable<QuotaRecheckSchedule>()
                .SetColumns(s => new QuotaRecheckSchedule
                {
                    Generation = generation,
                    NextCheckAtUtcTicks = next,
                    UpdatedAtUtcTicks = now,
                    LeaseToken = null,
                    LeaseExpiresAtUtcTicks = null
                })
                .Where(s => s.ProviderKey == lease.ProviderKey && s.AccountId == lease.AccountId
                    && s.LeaseToken == token && s.LeaseExpiresAtUtcTicks > now && s.Generation != token)
                .ExecuteCommandAsync(cancellationToken);
            return;
        }

        await client.Updateable<QuotaRecheckSchedule>()
            .SetColumns(s => new QuotaRecheckSchedule
            {
                Generation = generation,
                NextCheckAtUtcTicks = next,
                UpdatedAtUtcTicks = now,
                LeaseToken = null,
                LeaseExpiresAtUtcTicks = null
            })
            .Where(s => s.ProviderKey == lease.ProviderKey && s.AccountId == lease.AccountId
                && s.LeaseToken == token && s.LeaseExpiresAtUtcTicks > now)
            .ExecuteCommandAsync(cancellationToken);
    }

    private async Task InsertMissingAsync(ISqlSugarClient client, string providerKey, Guid accountId,
        DateTimeOffset? lastQuotaCheckedAt, CancellationToken cancellationToken, bool touchExisting = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        var next = lastQuotaCheckedAt?.Add(intervals.NextInterval());
        if (!next.HasValue || next <= now)
            next = now + intervals.NextCatchUpDelay();

        // An Ensure event renews the generation even on conflict, but NEVER postpones due
        // or alters a current lease. Reconciliation insertion is not a new disable event.
        var conflict = touchExisting
            ? "DO UPDATE SET Generation = excluded.Generation"
            : "DO NOTHING";
        await client.Ado.ExecuteCommandAsync($"""
            INSERT INTO QuotaRecheckSchedules
                (ProviderKey, AccountId, Generation, NextCheckAtUtcTicks, CreatedAtUtcTicks, UpdatedAtUtcTicks)
            VALUES (@provider, @id, @generation, @next, @now, @now)
            ON CONFLICT (ProviderKey, AccountId) {conflict};
            """, new[]
            {
                new SugarParameter("@provider", providerKey),
                new SugarParameter("@id", accountId),
                new SugarParameter("@generation", Guid.NewGuid().ToString("D")),
                new SugarParameter("@next", next.Value.UtcTicks),
                new SugarParameter("@now", now.UtcTicks)
            });
    }

    private static ISqlSugarClient OpenClient(IServiceScope scope)
    {
        var client = scope.ServiceProvider.GetRequiredService<AppDbContext>().Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        return client;
    }

    private static void ValidateProvider(string providerKey) => ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
}
