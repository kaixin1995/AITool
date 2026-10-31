using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Web.Contracts;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 号池管理 API：把多个同厂商 OAuth 账号打包成一个对外路由入口（生成式）。
/// 池由 <see cref="AccountPoolSyncService"/> 展开成普通路由规则，转发热路径零改动。
/// </summary>
[ApiController]
[Route("api/admin/account-pools")]
[ServiceFilter(typeof(OAuthFeatureToggleAttribute))]
public sealed class AccountPoolsApiController : ControllerBase
{
    private static readonly string[] VerticalVendors = ["codex", "antigravity", "kimi", "xai"];

    private readonly AppDbContext _dbContext;
    private readonly AccountPoolSyncService _syncService;
    private readonly DeviceOAuthVendorRegistry _deviceVendors;

    public AccountPoolsApiController(
        AppDbContext dbContext,
        AccountPoolSyncService syncService,
        DeviceOAuthVendorRegistry deviceVendors)
    {
        _dbContext = dbContext;
        _syncService = syncService;
        _deviceVendors = deviceVendors;
    }

    /// <summary>池列表（含成员行摘要与候选厂商清单）。</summary>
    [HttpGet]
    public async Task<IActionResult> GetPools(CancellationToken ct)
    {
        var pools = await _dbContext.AccountPools.OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var poolIds = pools.Select(p => p.Id).ToList();
        var members = poolIds.Count == 0
            ? []
            : await _dbContext.AccountPoolMembers
                .Where(m => poolIds.Contains(m.PoolId))
                .OrderBy(m => m.Priority)
                .ToListAsync(ct);
        var entryNames = pools.Select(p => p.Name).ToList();
        var entries = entryNames.Count == 0
            ? []
            : await _dbContext.ProxyRouteEntries
                .Where(e => entryNames.Contains(e.EntryName))
                .ToListAsync(ct);

        var candidates = await ResolveCandidateNamesAsync(members, ct);

        return Ok(ApiResponse.Ok(pools.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            vendorKey = p.VendorKey,
            vendorLabel = VendorLabelOf(p.VendorKey),
            planType = p.PlanType,
            autoIncludeMembers = p.AutoIncludeMembers,
            managed = entries.FirstOrDefault(e => e.EntryName == p.Name)?.ManagedByPoolId == p.Id,
            createdAt = p.CreatedAt,
            updatedAt = p.UpdatedAt,
            members = members
                .Where(m => m.PoolId == p.Id)
                .Select(m => new
                {
                    accountRefId = m.AccountRefId,
                    displayName = candidates.TryGetValue(m.AccountRefId, out var name) ? name : null,
                    linkedSiteId = m.LinkedSiteId,
                    priority = m.Priority,
                    excluded = m.Excluded,
                }),
        }).ToList()));
    }

    /// <summary>成员候选：指定厂商（+套餐）下的全部账号（建池/编辑成员勾选用）。</summary>
    [HttpGet("candidates")]
    public async Task<IActionResult> GetCandidates([FromQuery] string? vendorKey, [FromQuery] string? planType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vendorKey))
        {
            return BadRequest(ApiResponse.Fail("缺少厂商参数", "missing_vendor"));
        }

        var accounts = await _syncService.ListVendorAccountsAsync(vendorKey, planType, ct);
        return Ok(ApiResponse.Ok(accounts));
    }

    /// <summary>建池：名称即对外路由条目名（创建后不可改）；自动池立即物化成员，手动池按勾选建行。</summary>
    [HttpPost]
    public async Task<IActionResult> CreatePool([FromBody] CreatePoolRequest? request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(ApiResponse.Fail("请填写池名", "missing_name"));
        }
        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(request.VendorKey))
        {
            return BadRequest(ApiResponse.Fail("请选择厂商", "missing_vendor"));
        }
        var vendorKey = request.VendorKey.Trim();
        if (!IsValidVendor(vendorKey))
        {
            return BadRequest(ApiResponse.Fail("未知厂商", "unknown_vendor"));
        }

        // 名称占用检查：池名与任何既有池名/路由条目名冲突都拒绝（路由条目名唯一索引兜底重复创建）。
        if (await _dbContext.AccountPools.AnyAsync(p => p.Name == name, ct))
        {
            return BadRequest(ApiResponse.Fail($"池名「{name}」已被使用", "name_taken"));
        }
        if (await _dbContext.ProxyRouteEntries.AnyAsync(e => e.EntryName == name, ct))
        {
            return BadRequest(ApiResponse.Fail($"路由入口「{name}」已存在，池名不可与其重名", "name_taken"));
        }
        if (await _dbContext.ProxyRouteRules.AnyAsync(r => r.ExternalModelName == name, ct))
        {
            return BadRequest(ApiResponse.Fail($"名称「{name}」已被路由规则占用，请换一个池名", "name_taken"));
        }

        var pool = new AccountPool
        {
            Name = name,
            VendorKey = vendorKey,
            PlanType = string.IsNullOrWhiteSpace(request.PlanType) ? null : request.PlanType!.Trim(),
            AutoIncludeMembers = request.AutoIncludeMembers,
        };
        await _dbContext.Client.Insertable(pool).ExecuteCommandAsync(ct);

        // 手动池按勾选建成员行；自动池交给同步物化。
        if (!pool.AutoIncludeMembers && request.MemberAccountIds is { Count: > 0 })
        {
            var candidates = await _syncService.ListVendorAccountsAsync(pool.VendorKey, pool.PlanType, ct);
            var byId = candidates.ToDictionary(c => c.AccountId);
            var priority = 0;
            var rows = new List<AccountPoolMember>();
            foreach (var accountId in request.MemberAccountIds.Where(id => byId.ContainsKey(id)))
            {
                rows.Add(new AccountPoolMember
                {
                    PoolId = pool.Id,
                    AccountRefId = accountId,
                    LinkedSiteId = byId[accountId].LinkedSiteId,
                    Priority = priority++,
                });
            }

            if (rows.Count > 0)
            {
                await _dbContext.Client.Insertable(rows).ExecuteCommandAsync(ct);
            }
        }

        try
        {
            var result = await _syncService.SyncPoolAsync(pool.Id, ct);
            return Ok(ApiResponse.Ok(new { poolId = pool.Id, sync = result }));
        }
        catch (InvalidOperationException ex)
        {
            // 同步保护失败（名称冲突等）：回滚新建的池，避免留下空壳。
            await _syncService.DeletePoolAsync(pool.Id, ct);
            return BadRequest(ApiResponse.Fail(ex.Message, "sync_rejected"));
        }
    }

    /// <summary>整体替换成员行（手动增删/排序/排除；自动池也可微调顺序与排除）。</summary>
    [HttpPut("{id:guid}/members")]
    public async Task<IActionResult> ReplaceMembers(Guid id, [FromBody] ReplaceMembersRequest? request, CancellationToken ct)
    {
        var pool = (await _dbContext.AccountPools.Where(p => p.Id == id).ToListAsync(ct)).FirstOrDefault();
        if (pool is null)
        {
            return NotFound(ApiResponse.Fail("号池不存在", "pool_not_found"));
        }

        var candidates = await _syncService.ListVendorAccountsAsync(pool.VendorKey, pool.PlanType, ct);
        var byId = candidates.ToDictionary(c => c.AccountId);
        var priority = 0;
        var rows = new List<AccountPoolMember>();
        foreach (var member in request?.Members ?? [])
        {
            if (member?.AccountRefId == null || !byId.ContainsKey(member.AccountRefId))
            {
                continue; // 非本厂商（+套餐）账号不允许入池。
            }

            rows.Add(new AccountPoolMember
            {
                PoolId = pool.Id,
                AccountRefId = member.AccountRefId,
                LinkedSiteId = byId[member.AccountRefId].LinkedSiteId,
                Priority = priority++,
                Excluded = member.Excluded,
            });
        }

        await _dbContext.Client.Deleteable<AccountPoolMember>().Where(m => m.PoolId == pool.Id).ExecuteCommandAsync(ct);
        if (rows.Count > 0)
        {
            await _dbContext.Client.Insertable(rows).ExecuteCommandAsync(ct);
        }

        var result = await _syncService.SyncPoolAsync(pool.Id, ct);
        return Ok(ApiResponse.Ok(new { sync = result }));
    }

    /// <summary>手动同步（成员映射变更后刷新生成规则）。</summary>
    [HttpPost("{id:guid}/sync")]
    public async Task<IActionResult> SyncPool(Guid id, CancellationToken ct)
    {
        try
        {
            return Ok(ApiResponse.Ok(new { sync = await _syncService.SyncPoolAsync(id, ct) }));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(ApiResponse.Fail("号池不存在", "pool_not_found"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message, "sync_rejected"));
        }
    }

    /// <summary>同步全部池（前端在模型导入/账号删除后调用收口）。</summary>
    [HttpPost("sync-all")]
    public async Task<IActionResult> SyncAll(CancellationToken ct)
    {
        return Ok(ApiResponse.Ok(new { results = await _syncService.SyncAllPoolsAsync(ct) }));
    }

    /// <summary>删除池：清成员行并解除路由托管（生成规则保留，可继续手工维护）。</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeletePool(Guid id, CancellationToken ct)
    {
        try
        {
            await _syncService.DeletePoolAsync(id, ct);
            return Ok(ApiResponse.Ok(new { deleted = true }));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(ApiResponse.Fail("号池不存在", "pool_not_found"));
        }
    }

    // —— 私有 ——

    private bool IsValidVendor(string vendorKey)
        => VerticalVendors.Contains(vendorKey) || _deviceVendors.All.Any(v => v.VendorKey == vendorKey);

    private string VendorLabelOf(string vendorKey)
        => vendorKey switch
        {
            "codex" => "Codex",
            "antigravity" => "Antigravity",
            "kimi" => "Kimi",
            "xai" => "xAI",
            _ => _deviceVendors.All.FirstOrDefault(v => v.VendorKey == vendorKey)?.Label ?? vendorKey,
        };

    /// <summary>成员行跨五张账号表解析展示名（逐厂商小查询，账号量级小）。</summary>
    private async Task<Dictionary<Guid, string>> ResolveCandidateNamesAsync(List<AccountPoolMember> members, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string>();
        if (members.Count == 0)
        {
            return names;
        }

        var ids = members.Select(m => m.AccountRefId).Distinct().ToList();
        foreach (var account in await _dbContext.CodexAccounts.Where(a => ids.Contains(a.Id)).ToListAsync(ct))
        {
            names[account.Id] = account.DisplayName;
        }
        foreach (var account in await _dbContext.GoogleAccounts.Where(a => ids.Contains(a.Id)).ToListAsync(ct))
        {
            names[account.Id] = account.DisplayName;
        }
        foreach (var account in await _dbContext.KimiAccounts.Where(a => ids.Contains(a.Id)).ToListAsync(ct))
        {
            names[account.Id] = account.DisplayName ?? account.Email ?? account.Id.ToString();
        }
        foreach (var account in await _dbContext.XaiAccounts.Where(a => ids.Contains(a.Id)).ToListAsync(ct))
        {
            names[account.Id] = account.DisplayName ?? account.Email ?? account.Id.ToString();
        }
        foreach (var account in await _dbContext.DeviceOAuthAccounts.Where(a => ids.Contains(a.Id)).ToListAsync(ct))
        {
            names[account.Id] = account.DisplayName;
        }

        return names;
    }

    public sealed class CreatePoolRequest
    {
        public string? Name { get; set; }
        public string? VendorKey { get; set; }
        public string? PlanType { get; set; }
        public bool AutoIncludeMembers { get; set; }
        public List<Guid>? MemberAccountIds { get; set; }
    }

    public sealed class ReplaceMembersRequest
    {
        public List<MemberInput>? Members { get; set; }
    }

    public sealed class MemberInput
    {
        public Guid AccountRefId { get; set; }
        public bool Excluded { get; set; }
    }
}
