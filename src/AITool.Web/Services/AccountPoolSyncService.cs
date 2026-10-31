using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Infrastructure.Persistence;
using SqlSugar;

namespace AITool.Web.Services;

/// <summary>
/// 号池同步服务（生成式）：把池成员展开成普通 <see cref="ProxyRouteRule"/>
/// （ExternalModelName = 池名），托管标记挂在路由条目的 ManagedByPoolId 上。
/// <para>
/// 设计要点：转发热路径零改动——池规则就是普通规则，优先级/熔断/分级冷却/
/// 会话亲和/配额排序（P0 已对 OAuth 账号贯通）全部原样生效。池内静态顺序 =
/// InstancePriority 基线；对外暴露的模型 = 全体成员已启用映射的并集，
/// 每个模型只路由到持有它的成员；对外名透出上游名。
/// </para>
/// <para>保护：池名与任何非本池托管的路由条目/规则冲突时拒绝同步（绝不覆盖手工路由）；
/// 删除池仅清除托管标记（规则保留，可继续手工维护）。</para>
/// </summary>
public sealed class AccountPoolSyncService
{
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly ILogger<AccountPoolSyncService> _logger;

    public AccountPoolSyncService(
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        ILogger<AccountPoolSyncService> logger)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _logger = logger;
    }

    /// <summary>同步单个池：物化自动成员 → 重生成该池名的全部路由规则。</summary>
    public async Task<AccountPoolSyncResult> SyncPoolAsync(Guid poolId, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");

        var pool = (await client.Queryable<AccountPool>()
            .Where(p => p.Id == poolId)
            .ToListAsync(ct)).FirstOrDefault()
            ?? throw new KeyNotFoundException("号池不存在");

        // ① 物化成员行：自动池补齐厂商（+套餐）匹配账号、清掉已消亡账号；
        //    手动池只清已消亡账号（保留用户行）。排除位在两种池都保留。
        await MaterializeMembersAsync(client, pool, ct);
        var members = (await client.Queryable<AccountPoolMember>()
            .Where(m => m.PoolId == pool.Id)
            .ToListAsync(ct))
            .Where(m => !m.Excluded)
            .OrderBy(m => m.Priority)
            .ThenBy(m => m.AddedAt)
            .ToList();

        // ② 收集成员站点的已启用映射（对外模型并集的来源）。
        var siteIds = members.Select(m => m.LinkedSiteId).Distinct().ToList();
        var mappingsBySite = (await client.Queryable<SiteModelMapping>()
                .Where(m => siteIds.Contains(m.SiteId) && m.IsEnabled)
                .ToListAsync(ct))
            .ToLookup(m => m.SiteId);

        // 并集保持稳定次序：按成员顺序 × 各成员映射序（去重）。
        var exposedModels = new List<string>();
        foreach (var siteId in siteIds)
        {
            foreach (var mapping in mappingsBySite[siteId])
            {
                if (!string.IsNullOrWhiteSpace(mapping.RemoteModelName)
                    && !exposedModels.Contains(mapping.RemoteModelName))
                {
                    exposedModels.Add(mapping.RemoteModelName);
                }
            }
        }

        // ③ 托管保护：同名条目被别的池托管、或同名规则属于手工路由 → 拒绝（绝不覆盖手工路由）。
        var entry = (await client.Queryable<ProxyRouteEntry>()
            .Where(e => e.EntryName == pool.Name)
            .ToListAsync(ct)).FirstOrDefault();
        if (entry is { ManagedByPoolId: { } owner } && owner != pool.Id)
        {
            throw new InvalidOperationException($"路由入口「{pool.Name}」已被其它号池托管");
        }

        var existingRules = await client.Queryable<ProxyRouteRule>()
            .Where(r => r.ExternalModelName == pool.Name)
            .ToListAsync(ct);
        if (existingRules.Count > 0 && (entry == null || entry.ManagedByPoolId == null))
        {
            throw new InvalidOperationException($"路由入口「{pool.Name}」已被手工路由占用，请换一个池名");
        }

        // ④ 重生成（事务保护：删旧→建条目→插新原子完成，中途失败不丢既有规则）。
        var newRules = new List<ProxyRouteRule>();
        var ruleIndex = 0;
        for (var modelIndex = 0; modelIndex < exposedModels.Count; modelIndex++)
        {
            var modelName = exposedModels[modelIndex];
            foreach (var member in members)
            {
                if (!mappingsBySite[member.LinkedSiteId].Any(m => m.RemoteModelName == modelName))
                {
                    continue; // 该成员没有此模型：模型只路由到持有它的成员。
                }

                newRules.Add(new ProxyRouteRule
                {
                    ExternalModelName = pool.Name,
                    UpstreamModelName = modelName,
                    SiteId = member.LinkedSiteId,
                    SiteModelName = modelName,
                    Priority = ruleIndex++,
                    ModelPriority = modelIndex,
                    InstancePriority = member.Priority,
                    IsEnabled = true,
                });
            }
        }

        var entryRef = entry;
        await client.Ado.UseTranAsync(async () =>
        {
            if (existingRules.Count > 0)
            {
                await client.Deleteable<ProxyRouteRule>().Where(r => r.ExternalModelName == pool.Name).ExecuteCommandAsync(ct);
            }

            if (entryRef == null)
            {
                entryRef = new ProxyRouteEntry { EntryName = pool.Name, ManagedByPoolId = pool.Id };
                await client.Insertable(entryRef).ExecuteCommandAsync(ct);
            }
            else if (entryRef.ManagedByPoolId == null)
            {
                entryRef.ManagedByPoolId = pool.Id;
                await client.Updateable(entryRef).UpdateColumns(e => e.ManagedByPoolId).ExecuteCommandAsync(ct);
            }

            if (newRules.Count > 0)
            {
                await client.Insertable(newRules).ExecuteCommandAsync(ct);
            }
        });

        pool.UpdatedAt = DateTimeOffset.UtcNow;
        await client.Updateable(pool).UpdateColumns(p => p.UpdatedAt).ExecuteCommandAsync(ct);

        _metadataCache.InvalidateAdminRouteMetadata();
        _metadataCache.InvalidateRuntimeRouteTargets();

        return new AccountPoolSyncResult(
            pool.Id,
            pool.Name,
            members.Count,
            exposedModels.Count,
            newRules.Count);
    }

    /// <summary>同步全部池（成员映射变更后的统一收口；逐池容错，单池失败不中断）。</summary>
    public async Task<List<AccountPoolSyncResult>> SyncAllPoolsAsync(CancellationToken ct)
    {
        var poolIds = (await _dbContext.AccountPools.ToListAsync(ct)).Select(p => p.Id).ToList();
        var results = new List<AccountPoolSyncResult>(poolIds.Count);
        foreach (var poolId in poolIds)
        {
            try
            {
                results.Add(await SyncPoolAsync(poolId, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Account pool {PoolId} sync failed", poolId);
            }
        }

        return results;
    }

    /// <summary>删除池：删成员行 + 清除条目托管标记（规则保留，转手工维护）。</summary>
    public async Task DeletePoolAsync(Guid poolId, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var pool = (await client.Queryable<AccountPool>()
            .Where(p => p.Id == poolId)
            .ToListAsync(ct)).FirstOrDefault()
            ?? throw new KeyNotFoundException("号池不存在");

        await client.Deleteable<AccountPoolMember>().Where(m => m.PoolId == poolId).ExecuteCommandAsync(ct);
        await client.Deleteable<AccountPool>().Where(p => p.Id == poolId).ExecuteCommandAsync(ct);

        var entry = (await client.Queryable<ProxyRouteEntry>()
            .Where(e => e.EntryName == pool.Name)
            .ToListAsync(ct)).FirstOrDefault();
        if (entry is { ManagedByPoolId: { } owner } && owner == poolId)
        {
            entry.ManagedByPoolId = null;
            await client.Updateable(entry).UpdateColumns(e => e.ManagedByPoolId).ExecuteCommandAsync(ct);
        }

        _metadataCache.InvalidateAdminRouteMetadata();
        _metadataCache.InvalidateRuntimeRouteTargets();
    }

    /// <summary>
    /// 列出厂商（+套餐）匹配的账号（成员勾选/自动物化共用）：
    /// 返回 (账号行 Id, 展示名, 隐藏站点 Id)。设备厂商按 VendorKey 过滤，
    /// 四个垂直厂商按固定键分发。
    /// </summary>
    public async Task<List<AccountPoolCandidate>> ListVendorAccountsAsync(string vendorKey, string? planType, CancellationToken ct)
    {
        var key = vendorKey?.Trim() ?? string.Empty;
        switch (key)
        {
            case "codex":
            {
                var accounts = await _dbContext.CodexAccounts.ToListAsync(ct);
                return accounts
                    .Where(a => planType == null || string.Equals(a.PlanType, planType, StringComparison.OrdinalIgnoreCase))
                    .Select(a => new AccountPoolCandidate(a.Id, a.DisplayName, a.LinkedSiteId))
                    .ToList();
            }

            case "antigravity":
            {
                var accounts = await _dbContext.GoogleAccounts.ToListAsync(ct);
                return accounts
                    .Select(a => new AccountPoolCandidate(a.Id, a.DisplayName, a.LinkedSiteId))
                    .ToList();
            }

            case "kimi":
            {
                var accounts = await _dbContext.KimiAccounts.Where(a => !a.IsDeleted).ToListAsync(ct);
                return accounts
                    .Select(a => new AccountPoolCandidate(a.Id, a.DisplayName ?? a.Email ?? a.Id.ToString(), a.LinkedSiteId))
                    .ToList();
            }

            case "xai":
            {
                var accounts = await _dbContext.XaiAccounts.Where(a => !a.IsDeleted).ToListAsync(ct);
                return accounts
                    .Select(a => new AccountPoolCandidate(a.Id, a.DisplayName ?? a.Email ?? a.Id.ToString(), a.LinkedSiteId))
                    .ToList();
            }

            default:
            {
                // 设备 OAuth 厂商：VendorKey 精确匹配（软删过滤）。
                var accounts = await _dbContext.DeviceOAuthAccounts
                    .Where(a => !a.IsDeleted && a.VendorKey == key)
                    .ToListAsync(ct);
                return accounts
                    .Select(a => new AccountPoolCandidate(a.Id, a.DisplayName, a.LinkedSiteId))
                    .ToList();
            }
        }
    }

    /// <summary>自动物化：补齐匹配账号的成员行、清掉已消亡账号的行；LinkedSiteId 随账号刷新。</summary>
    private static async Task MaterializeMembersAsync(
        ISqlSugarClient client,
        AccountPool pool,
        CancellationToken ct)
    {
        // 厂商账号枚举直接查表（同步服务持库即可，避免依赖各控制器）。
        List<(Guid AccountId, Guid SiteId)> vendorAccounts = pool.VendorKey switch
        {
            "codex" => (await client.Queryable<Domain.Codex.CodexAccount>().ToListAsync(ct))
                .Where(a => pool.PlanType == null || string.Equals(a.PlanType, pool.PlanType, StringComparison.OrdinalIgnoreCase))
                .Select(a => (a.Id, a.LinkedSiteId))
                .ToList(),
            "antigravity" => (await client.Queryable<Domain.Google.GoogleAccount>().ToListAsync(ct))
                .Select(a => (a.Id, a.LinkedSiteId))
                .ToList(),
            "kimi" => (await client.Queryable<Domain.Kimi.KimiAccount>().Where(a => !a.IsDeleted).ToListAsync(ct))
                .Select(a => (a.Id, a.LinkedSiteId))
                .ToList(),
            "xai" => (await client.Queryable<Domain.Xai.XaiAccount>().Where(a => !a.IsDeleted).ToListAsync(ct))
                .Select(a => (a.Id, a.LinkedSiteId))
                .ToList(),
            _ => (await client.Queryable<Domain.OAuth.DeviceOAuthAccount>()
                    .Where(a => !a.IsDeleted && a.VendorKey == pool.VendorKey)
                    .ToListAsync(ct))
                .Select(a => (a.Id, a.LinkedSiteId))
                .ToList(),
        };
        var byAccount = vendorAccounts.ToDictionary(a => a.AccountId, a => a.SiteId);

        var rows = await client.Queryable<AccountPoolMember>()
            .Where(m => m.PoolId == pool.Id)
            .ToListAsync(ct);

        // 清已消亡账号（两种池都清——账号没了行就没意义）。
        var dead = rows.Where(r => !byAccount.ContainsKey(r.AccountRefId)).ToList();
        if (dead.Count > 0)
        {
            await client.Deleteable<AccountPoolMember>()
                .Where(m => m.PoolId == pool.Id && dead.Select(d => d.Id).Contains(m.Id))
                .ExecuteCommandAsync(ct);
            rows = rows.Where(r => byAccount.ContainsKey(r.AccountRefId)).ToList();
        }

        // 站点刷新（账号重供给会换站点？现状 1:1 稳定，但行里的站点以账号现值 为准）。
        foreach (var row in rows)
        {
            if (byAccount.TryGetValue(row.AccountRefId, out var siteId) && siteId != row.LinkedSiteId)
            {
                row.LinkedSiteId = siteId;
                await client.Updateable(row).UpdateColumns(m => m.LinkedSiteId).ExecuteCommandAsync(ct);
            }
        }

        if (!pool.AutoIncludeMembers)
        {
            return;
        }

        // 自动补齐：匹配但尚无行的账号按现有最大 Priority+1 追加（排除位保留的行不算缺失）。
        var existingIds = rows.Select(r => r.AccountRefId).ToHashSet();
        var next = rows.Count == 0 ? 0 : rows.Max(r => r.Priority) + 1;
        var additions = new List<AccountPoolMember>();
        foreach (var (accountId, siteId) in vendorAccounts)
        {
            if (existingIds.Contains(accountId))
            {
                continue;
            }

            additions.Add(new AccountPoolMember
            {
                PoolId = pool.Id,
                AccountRefId = accountId,
                LinkedSiteId = siteId,
                Priority = next++,
            });
        }

        if (additions.Count > 0)
        {
            await client.Insertable(additions).ExecuteCommandAsync(ct);
        }
    }
}

/// <summary>一次池同步的结果摘要。</summary>
public sealed record AccountPoolSyncResult(Guid PoolId, string Name, int MemberCount, int ModelCount, int RuleCount);

/// <summary>厂商账号候选（成员勾选用）。</summary>
public sealed record AccountPoolCandidate(Guid AccountId, string DisplayName, Guid LinkedSiteId);
