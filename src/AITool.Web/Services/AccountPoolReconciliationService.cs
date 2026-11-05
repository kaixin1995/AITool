using System.Text.Json;
using AITool.Infrastructure.Persistence;

namespace AITool.Web.Services;

/// <summary>
/// Reconciles pools from committed account and mapping state, independently of the caller.
/// A startup pass repairs interrupted work; failed pools are retried on every subsequent pass.
/// </summary>
public sealed class AccountPoolReconciliationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AccountPoolReconciliationService> _logger;
    private readonly Dictionary<Guid, string> _completed = new();
    private readonly Dictionary<Guid, string> _failures = new();
    private readonly object _statusGate = new();
    private string? _scanFailure;

    // Updates can come from any database writer, so row counts cannot safely detect them.
    // Scan every interval; per-pool signatures still avoid unnecessary materialization.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public AccountPoolReconciliationService(IServiceScopeFactory scopeFactory, ILogger<AccountPoolReconciliationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Current unresolved errors, exposed to administrators; cleared only after a successful retry.</summary>
    public AccountPoolReconciliationStatus GetStatus()
    {
        lock (_statusGate)
            return new AccountPoolReconciliationStatus(_scanFailure, new Dictionary<Guid, string>(_failures));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    /// <summary>Runs one full pass. Snapshots are recorded before syncing, so concurrent changes are observed on the next pass.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sync = scope.ServiceProvider.GetRequiredService<AccountPoolSyncService>();
            var pools = await db.AccountPools.ToListAsync(ct);
            var members = await db.AccountPoolMembers.ToListAsync(ct);
            var mappings = await db.SiteModelMappings.ToListAsync(ct);
            var ids = pools.Select(p => p.Id).ToHashSet();
            lock (_statusGate)
            {
                foreach (var id in _completed.Keys.Where(id => !ids.Contains(id)).ToList()) _completed.Remove(id);
                foreach (var id in _failures.Keys.Where(id => !ids.Contains(id)).ToList()) _failures.Remove(id);
                _scanFailure = null;
            }

            foreach (var pool in pools)
            {
                try
                {
                    var candidates = await sync.ListVendorAccountsAsync(pool.VendorKey, pool.PlanType, ct);
                    var poolMembers = members.Where(m => m.PoolId == pool.Id).OrderBy(m => m.AccountRefId).ToList();
                    var sites = candidates.Select(a => a.LinkedSiteId).Concat(poolMembers.Select(m => m.LinkedSiteId)).ToHashSet();
                    // Include only fields that affect pool materialization, not quota/health updates.
                    var signature = JsonSerializer.Serialize(new
                    {
                        pool.VendorKey, pool.PlanType, pool.AutoIncludeMembers, pool.ExposedModelsJson,
                        Candidates = candidates.OrderBy(a => a.AccountId),
                        Members = poolMembers.Select(m => new { m.AccountRefId, m.LinkedSiteId, m.Priority, m.Excluded, m.AddedAt }),
                        Mappings = mappings.Where(m => sites.Contains(m.SiteId))
                            .OrderBy(m => m.SiteId).ThenBy(m => m.RemoteModelName, StringComparer.Ordinal)
                            .Select(m => new { m.SiteId, m.RemoteModelName, m.IsEnabled }),
                    });
                    bool unchanged;
                    lock (_statusGate) unchanged = _completed.TryGetValue(pool.Id, out var previous) && previous == signature && !_failures.ContainsKey(pool.Id);
                    if (unchanged) continue;
                    await sync.SyncPoolAsync(pool.Id, ct);
                    lock (_statusGate)
                    {
                        _completed[pool.Id] = signature;
                        _failures.Remove(pool.Id);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lock (_statusGate) _failures[pool.Id] = ex.Message;
                    _logger.LogError(ex, "Account pool {PoolId} reconciliation failed; will retry", pool.Id);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            lock (_statusGate) _scanFailure = ex.Message;
            _logger.LogError(ex, "Account pool reconciliation scan failed; will retry");
        }

    }
}

public sealed record AccountPoolReconciliationStatus(string? ScanFailure, IReadOnlyDictionary<Guid, string> PoolFailures);
