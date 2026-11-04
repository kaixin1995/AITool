using System.Text.Json;
using AITool.Domain.Codex;
using AITool.Domain.Google;
using AITool.Domain.Kimi;
using AITool.Domain.OAuth;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Domain.Xai;
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
    private string? _lastFingerprint;

    // 轻量轮询节奏：多数轮次只做 COUNT 指纹比对（行数无变化即跳过全量对账），
    // 每 FullScanEveryPasses 轮无条件全量，兜住行数不变的更新类变更（改名/映射启停等）。
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private const int FullScanEveryPasses = 6;

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
        var pass = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            // 首轮强制全量（修复中断的池工作）；此后仅指纹变化、存在待重试失败或周期性强制轮做全量。
            var forceFull = pass % FullScanEveryPasses == 0;
            await RunOnceAsync(stoppingToken, forceFull);
            pass++;
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    /// <summary>Runs one full pass. Snapshots are recorded before syncing, so concurrent changes are observed on the next pass.</summary>
    public Task RunOnceAsync(CancellationToken ct) => RunOnceAsync(ct, forceFull: true);

    internal async Task RunOnceAsync(CancellationToken ct, bool forceFull)
    {
        if (!forceFull && !HasPendingFailures() && await FingerprintUnchangedAsync(ct))
        {
            return;
        }

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

        // 全量完成后刷新指纹基线（含强制轮），使紧随其后的快速轮无需再全量学习一次指纹。
        await TryRefreshFingerprintAsync(ct);
    }

    private bool HasPendingFailures()
    {
        lock (_statusGate) return _failures.Count > 0 || _scanFailure is not null;
    }

    /// <summary>
    /// 轻量指纹：池/成员/映射/站点/各厂商账号表的行数聚合。行数无变化的大多数轮次
    /// 以此跳过全量对账；行数不变的更新类变更由周期性强制全量兜底。
    /// 指纹查询失败时按"已变化"处理（退化为全量，绝不因指纹故障漏对账）。
    /// </summary>
    private async Task<bool> FingerprintUnchangedAsync(CancellationToken ct)
    {
        try
        {
            var fingerprint = await ComputeFingerprintAsync(ct);
            if (fingerprint == _lastFingerprint) return true;
            _lastFingerprint = fingerprint;
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Account pool reconciliation fingerprint check failed; running full pass");
            return false;
        }
    }

    private async Task TryRefreshFingerprintAsync(CancellationToken ct)
    {
        try { _lastFingerprint = await ComputeFingerprintAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* 基线刷新失败无害：下一快速轮会多做一次全量 */ }
    }

    private async Task<string> ComputeFingerprintAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<AppDbContext>().Client;
        return string.Join(',',
            await client.Queryable<AccountPool>().CountAsync(ct),
            await client.Queryable<AccountPoolMember>().CountAsync(ct),
            await client.Queryable<SiteModelMapping>().CountAsync(ct),
            await client.Queryable<Site>().CountAsync(ct),
            await client.Queryable<CodexAccount>().CountAsync(ct),
            await client.Queryable<GoogleAccount>().CountAsync(ct),
            await client.Queryable<KimiAccount>().CountAsync(ct),
            await client.Queryable<XaiAccount>().CountAsync(ct),
            await client.Queryable<DeviceOAuthAccount>().CountAsync(ct));
    }
}

public sealed record AccountPoolReconciliationStatus(string? ScanFailure, IReadOnlyDictionary<Guid, string> PoolFailures);
