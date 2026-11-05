using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Web.Contracts;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using SqlSugar;

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
        // 池认领的托管入口计数（每个成员模型一个入口）。
        var managedCounts = new Dictionary<Guid, int>();
        if (poolIds.Count > 0)
        {
            foreach (var batch in poolIds.Chunk(500))
            {
                var counts = await _dbContext.ProxyRouteEntries
                    .Where(e => e.ManagedByPoolId != null && batch.Contains(e.ManagedByPoolId!.Value))
                    .GroupBy(e => e.ManagedByPoolId!.Value)
                    .Select(e => new { PoolId = e.ManagedByPoolId!.Value, Count = SqlFunc.AggregateCount(e.Id) })
                    .ToListAsync(ct);
                foreach (var count in counts) managedCounts[count.PoolId] = count.Count;
            }
        }

        var candidates = await ResolveCandidateNamesAsync(members, ct);

        return Ok(ApiResponse.Ok(pools.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            vendorKey = p.VendorKey,
            vendorLabel = VendorLabelOf(p.VendorKey),
            planType = p.PlanType,
            autoIncludeMembers = p.AutoIncludeMembers,
            exposedModels = ParseModelsJson(p.ExposedModelsJson),
            managedEntryCount = managedCounts.GetValueOrDefault(p.Id, 0),
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

    /// <summary>后台号池同步的未解决错误；后台会持续重试。</summary>
    [HttpGet("sync-status")]
    public IActionResult GetSyncStatus([FromServices] AccountPoolReconciliationService reconciliation)
        => Ok(ApiResponse.Ok(reconciliation.GetStatus()));

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

    /// <summary>建池：池名为管理标签（池间唯一）；自动池立即物化成员，手动池按勾选建行。
    /// 同步按成员模型生成托管入口——被手工路由占用的模型自动跳过（结果里列出）。</summary>
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

        // 名称占用检查：池名在池之间唯一即可（管理标签，不占用路由命名空间）。
        if (await _dbContext.AccountPools.AnyAsync(p => p.Name == name, ct))
        {
            return BadRequest(ApiResponse.Fail($"池名「{name}」已被使用", "name_taken"));
        }

        var pool = new AccountPool
        {
            Name = name,
            VendorKey = vendorKey,
            PlanType = string.IsNullOrWhiteSpace(request.PlanType) ? null : request.PlanType!.Trim(),
            AutoIncludeMembers = request.AutoIncludeMembers,
            ExposedModelsJson = SerializeModelsJson(request.ExposedModels),
        };
        try
        {
            var result = await _syncService.CreatePoolAsync(pool, request.MemberAccountIds, ct);
            return Ok(ApiResponse.Ok(new { poolId = pool.Id, sync = result }));
        }
        catch (InvalidOperationException ex)
        {
            // Service transaction already rolled back the pool, members and generated graph.
            return BadRequest(ApiResponse.Fail(ex.Message, "sync_rejected"));
        }
    }

    /// <summary>整体替换成员行（手动增删/排序/排除；自动池也可微调顺序与排除）。</summary>
    [HttpPut("{id:guid}/members")]
    public async Task<IActionResult> ReplaceMembers(Guid id, [FromBody] ReplaceMembersRequest? request, CancellationToken ct)
    {
        try
        {
            var members = (request?.Members ?? []).Where(m => m != null)
                .Select(m => (m.AccountRefId, m.Excluded)).ToList();
            var result = await _syncService.ReplaceMembersAsync(id, members, ct);
            return Ok(ApiResponse.Ok(new { sync = result }));
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

    /// <summary>管理员核实旧入口和规则后显式认领；不会立即删除规则，需再同步。</summary>
    [HttpPost("{id:guid}/claim-legacy-entry")]
    public async Task<IActionResult> ClaimLegacyEntry(Guid id, [FromBody] ClaimLegacyEntryRequest? request, CancellationToken ct)
    {
        if (request is null || !request.Confirm || request.ObservedRuleIds is null || request.ClaimRuleIds is null)
            return BadRequest(ApiResponse.Fail("必须确认并提供完整规则快照和要认领的规则", "confirmation_required"));
        try
        {
            await _syncService.ClaimLegacyEntryAsync(id, request.EntryId, request.ObservedRuleIds, request.ClaimRuleIds, ct);
            return Ok(ApiResponse.Ok(new { claimed = true }));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(ApiResponse.Fail("号池不存在", "pool_not_found"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message, "claim_rejected"));
        }
    }

    public sealed class ClaimLegacyEntryRequest
    {
        public bool Confirm { get; set; }
        public Guid EntryId { get; set; }
        public List<Guid>? ObservedRuleIds { get; set; }
        public List<Guid>? ClaimRuleIds { get; set; }
    }

    /// <summary>
    /// 模型预览：厂商（+套餐，可选限定成员账号）隐藏站点启用映射的对外模型并集（稳定序）。
    /// 建池弹窗与模型编辑弹窗共用——建池传已勾选成员（自动池不传 = 全部候选账号）。
    /// </summary>
    [HttpPost("models-preview")]
    public async Task<IActionResult> PreviewModels([FromBody] ModelsPreviewRequest? request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.VendorKey))
        {
            return BadRequest(ApiResponse.Fail("缺少厂商参数", "missing_vendor"));
        }

        var accounts = await _syncService.ListVendorAccountsAsync(request.VendorKey.Trim(), request.PlanType, ct);
        if (request.MemberAccountIds is not null)
        {
            var wanted = request.MemberAccountIds.ToHashSet();
            accounts = accounts.Where(a => wanted.Contains(a.AccountId)).ToList();
        }

        var siteIds = accounts.Select(a => a.LinkedSiteId).Distinct().ToList();
        var models = new List<string>();
        foreach (var mapping in await _dbContext.SiteModelMappings
            .Where(m => siteIds.Contains(m.SiteId) && m.IsEnabled)
            .ToListAsync(ct))
        {
            if (!string.IsNullOrWhiteSpace(mapping.RemoteModelName) && !models.Contains(mapping.RemoteModelName))
            {
                models.Add(mapping.RemoteModelName);
            }
        }

        return Ok(ApiResponse.Ok(models));
    }

    /// <summary>更新托管模型白名单：空 = 不限（成员模型并集全部生成）；非空 = 只给勾选模型生成入口。</summary>
    [HttpPut("{id:guid}/models")]
    public async Task<IActionResult> UpdateModels(Guid id, [FromBody] UpdateModelsRequest? request, CancellationToken ct)
    {
        try
        {
            return Ok(ApiResponse.Ok(new { sync = await _syncService.UpdateModelsAsync(id, SerializeModelsJson(request?.ExposedModels), ct) }));
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

    /// <summary>删除池：清成员行，并连带删除该池生成的全部托管入口与名下规则（手工路由不受影响）。</summary>
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
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message, "sync_rejected"));
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
        /// <summary>托管模型白名单；null/空 = 成员模型并集全部生成。</summary>
        public List<string>? ExposedModels { get; set; }
    }

    public sealed class ModelsPreviewRequest
    {
        public string? VendorKey { get; set; }
        public string? PlanType { get; set; }
        public List<Guid>? MemberAccountIds { get; set; }
    }

    public sealed class UpdateModelsRequest
    {
        /// <summary>null/空 = 清空白名单（全部生成）。</summary>
        public List<string>? ExposedModels { get; set; }
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

    /// <summary>白名单 JSON 序列化；空清单 = null（不限）。</summary>
    private static string? SerializeModelsJson(List<string>? models)
        => models is { Count: > 0 }
            ? System.Text.Json.JsonSerializer.Serialize(models)
            : null;

    /// <summary>白名单 JSON 反序列化；空/非法 = null（不限）。</summary>
    private static List<string>? ParseModelsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
