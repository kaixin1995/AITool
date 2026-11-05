using System.Text.Json;
using AITool.Application.Common;
using AITool.Application.Proxy;
using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 路由规则配置页中展示的模型项，包含该模型的站点数量和路由配置状态。
/// </summary>
public sealed class RouteModelItem
{
    /// <summary>
    /// 模型名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;
    /// <summary>
    /// 显示名称。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>
    /// 站点数量。
    /// </summary>
    public int SiteCount { get; set; }
    /// <summary>
    /// 是否已配置路由规则。
    /// </summary>
    public bool HasRouteRules { get; set; }
}

/// <summary>
/// 路由主入口列表项，展示入口名称和其下的候选实例数量。
/// </summary>
public sealed class RouteEntryListItem
{
    public Guid? EntryId { get; set; }
    public Guid? ManagedByPoolId { get; set; }
    public bool HasRouteReferences { get; set; }
    /// <summary>
    /// 主入口名称。
    /// </summary>
    public string EntryName { get; set; } = string.Empty;
    /// <summary>
    /// 模型显示名称（入口名匹配模型库 ModelName 时回填，供展示层优先显示；否则为空）。
    /// </summary>
    public string? DisplayName { get; set; }
    /// <summary>
    /// 候选实例数量。
    /// </summary>
    public int CandidateCount { get; set; }
}

/// <summary>
/// 创建路由主入口的请求参数。
/// </summary>
public sealed class CreateRouteEntryRequest
{
    /// <summary>
    /// 主入口名称。
    /// </summary>
    public string EntryName { get; set; } = string.Empty;
}

/// <summary>
/// 删除路由主入口的请求参数。
/// </summary>
public sealed class DeleteRouteEntryRequest
{
    public bool ConfirmReferenced { get; set; }
    /// <summary>
    /// 主入口名称。
    /// </summary>
    public string EntryName { get; set; } = string.Empty;
}

/// <summary>
/// 可选站点实例项，用于路由规则配置页中展示可绑定的站点映射。
/// </summary>
public sealed class SiteInstanceItem
{
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
    /// <summary>
    /// 站点模型名称。
    /// </summary>
    public string SiteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 协议类型。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;
    /// <summary>
    /// 站点是否启用，用于在管理页面区分已禁用站点。
    /// </summary>
    public bool SiteEnabled { get; set; } = true;
}

/// <summary>
/// 按模型名称发现的可用站点项，展示站点信息和远端模型名称。
/// </summary>
public sealed class DiscoveredSiteItem
{
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
    /// <summary>
    /// 远端模型名称。
    /// </summary>
    public string RemoteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 站点是否启用。
    /// </summary>
    public bool SiteEnabled { get; set; }
}

/// <summary>
/// 路由规则列表项，展示单条规则的详细信息，包括站点、模型、优先级和启用状态。
/// </summary>
public sealed class RouteRuleListItem
{
    public string TargetKind { get; set; } = "Site";
    public Guid? TargetRouteEntryId { get; set; }
    public string? TargetRouteEntryName { get; set; }
    public string? ReferenceStatus { get; set; }
    /// <summary>
    /// 规则标识。
    /// </summary>
    public Guid RuleId { get; set; }
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
    /// <summary>
    /// 上游模型名称。
    /// </summary>
    public string UpstreamModelName { get; set; } = string.Empty;
    /// <summary>
    /// 上游模型的显示名称（按 UpstreamModelName 匹配模型库取 DisplayName，供展示层优先显示对外名）。
    /// </summary>
    public string ModelDisplayName { get; set; } = string.Empty;
    /// <summary>
    /// 站点模型名称。
    /// </summary>
    public string SiteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 全局优先级。
    /// </summary>
    public int Priority { get; set; }
    /// <summary>
    /// 模型优先级。
    /// </summary>
    public int ModelPriority { get; set; }
    /// <summary>
    /// 实例优先级。
    /// </summary>
    public int InstancePriority { get; set; }
    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; }
    /// <summary>
    /// 站点是否启用，用于在管理页面区分已禁用站点。
    /// </summary>
    public bool SiteEnabled { get; set; } = true;
    /// <summary>
    /// 时间可用性模式，旧规则为空时按全天可用处理。
    /// </summary>
    public string AvailabilityMode { get; set; } = "AllDay";
    /// <summary>
    /// 每日时间范围 JSON，空值表示不限制。
    /// </summary>
    public string TimeRangesJson { get; set; } = string.Empty;
}

/// <summary>
/// 批量保存路由规则的请求参数，按外部模型名称整体覆盖该模型下的所有规则。
/// </summary>
public sealed class SaveRouteRulesRequest
{
    public bool SupportsRouteReferences { get; set; }
    /// <summary>
    /// 外部模型名称。
    /// </summary>
    public string ExternalModelName { get; set; } = string.Empty;
    /// <summary>
    /// 路由规则列表。
    /// </summary>
    public List<SaveRouteRuleEntry> Rules { get; set; } = [];
}

/// <summary>
/// 单条路由规则条目，用于保存规则时指定上游模型与目标站点的映射关系。
/// </summary>
public sealed class SaveRouteRuleEntry
{
    public string TargetKind { get; set; } = "Site";
    public Guid? TargetRouteEntryId { get; set; }
    public string? TargetRouteEntryName { get; set; }
    /// <summary>
    /// 上游模型名称。
    /// </summary>
    public string UpstreamModelName { get; set; } = string.Empty;
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }
    /// <summary>
    /// 站点模型名称。
    /// </summary>
    public string SiteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 是否启用，未传时默认启用以兼容旧调用方。
    /// </summary>
    public bool IsEnabled { get; set; } = true;
    /// <summary>
    /// 时间可用性模式，未传时默认全天可用。
    /// </summary>
    public string AvailabilityMode { get; set; } = "AllDay";
    /// <summary>
    /// 每日时间范围 JSON，未传或无效时默认不限制。
    /// </summary>
    public string TimeRangesJson { get; set; } = string.Empty;
}

/// <summary>Pool queue edits include the loaded version of each rule to reject stale writes.</summary>
public sealed class SavePoolQueueRequest
{
    public Guid EntryId { get; set; }
    public List<SavePoolQueueItem>? Rules { get; set; }
}

public sealed class SavePoolQueueItem
{
    public Guid RuleId { get; set; }
    public int? ExpectedPriority { get; set; }
    public int? ExpectedModelPriority { get; set; }
    public int? ExpectedInstancePriority { get; set; }
    public bool? ExpectedIsEnabled { get; set; }
    public bool? IsEnabled { get; set; }
}
/// <summary>
/// 每日时间范围配置项。
/// </summary>
public sealed class RouteTimeRange
{
    /// <summary>
    /// 开始时间，格式为 HH:mm。
    /// </summary>
    public string Start { get; set; } = string.Empty;
    /// <summary>
    /// 结束时间，格式为 HH:mm。
    /// </summary>
    public string End { get; set; } = string.Empty;
}

/// <summary>
/// 路由规则管理控制器，提供主入口、站点实例和规则条目的增删改查。
/// </summary>
[ApiController]
[Route("api/admin/route-rules")]
public sealed class RouteRulesApiController : ControllerBase
{
    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;
    /// <summary>
    /// 代理元数据缓存。
    /// </summary>
    private readonly ProxyRequestMetadataCache _metadataCache;
    /// <summary>
    /// 模型并发限制器，用于判断路由保存时是否存在正在调用的模型实例。
    /// </summary>
    private readonly ModelConcurrencyLimiter _concurrencyLimiter;

    /// <summary>
    /// 创建路由规则控制器。
    /// </summary>
    public RouteRulesApiController(
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        ModelConcurrencyLimiter concurrencyLimiter)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _concurrencyLimiter = concurrencyLimiter;
    }

    /// <summary>
    /// 获取主入口列表。
    /// </summary>
    [HttpGet("entries")]
    public async Task<IActionResult> GetEntries(CancellationToken cancellationToken)
    {
        using var lease = await RouteGraphMutationGate.AcquireAsync(cancellationToken);
        var result = await _metadataCache.GetRouteEntriesAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// 创建主入口。
    /// </summary>
    [HttpPost("entries")]
    public async Task<IActionResult> CreateEntry(
        [FromBody] CreateRouteEntryRequest request,
        CancellationToken cancellationToken)
    {
        var entryName = (request.EntryName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(entryName))
            return BadRequest(new { message = "主入口名称不能为空" });

        return await ExecuteGraphMutationAsync(async db =>
        {
            if (await db.ProxyRouteEntries.AnyAsync(x => x.EntryName == entryName, cancellationToken)
                || await db.ProxyRouteRules.AnyAsync(x => x.ExternalModelName == entryName, cancellationToken))
                return BadRequest(new { message = "主入口已存在" });
            await db.InsertAsync(new ProxyRouteEntry { EntryName = entryName }, cancellationToken);
            return Ok(new { message = "创建成功" });
        }, cancellationToken);
    }

    /// <summary>
    /// 删除主入口。
    /// </summary>
    [HttpPost("entries/delete")]
    public async Task<IActionResult> DeleteEntry(
        [FromBody] DeleteRouteEntryRequest request,
        CancellationToken cancellationToken)
    {
        var entryName = (request.EntryName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(entryName))
            return BadRequest(new { message = "主入口名称不能为空" });

        return await ExecuteGraphMutationAsync(async db =>
        {
            var entry = await db.ProxyRouteEntries.FirstAsync(x => x.EntryName == entryName, cancellationToken);
            var rules = await db.ProxyRouteRules.Where(x => x.ExternalModelName == entryName).ToListAsync(cancellationToken);
            if (entry is null && rules.Count == 0) return NotFound(new { message = "主入口不存在" });
            if (entry?.ManagedByPoolId is not null) return Conflict(new { message = "号池托管入口不能手工修改" });
            var inbound = entry is null ? new List<ProxyRouteRule>() : await db.ProxyRouteRules
                .Where(x => x.TargetKind == "RouteEntry" && x.TargetRouteEntryId == entry.Id).ToListAsync(cancellationToken);
            if (inbound.Count > 0 && !request.ConfirmReferenced)
                return Conflict(new { message = "入口正在被引用，确认删除后引用将失效", referenceCount = inbound.Count });
            // Incoming references deliberately survive; recreating the name must not rebind them.
            if (entry is not null) await db.DeleteAsync(entry, cancellationToken);
            if (rules.Count > 0) await db.DeleteRangeAsync(rules, cancellationToken);
            return Ok(new { message = "删除成功" });
        }, cancellationToken, () =>
        {
            _metadataCache.InvalidateDeletedRouteEntry(entryName);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 获取可选站点实例。
    /// </summary>
    [HttpGet("site-instances")]
    public async Task<IActionResult> GetSiteInstances(CancellationToken cancellationToken)
    {
        using var lease = await RouteGraphMutationGate.AcquireAsync(cancellationToken);
        var result = await _metadataCache.GetRouteSiteInstancesAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// 获取可配置路由的模型。
    /// </summary>
    [HttpGet("models")]
    public async Task<IActionResult> GetModels(CancellationToken cancellationToken)
    {
        using var lease = await RouteGraphMutationGate.AcquireAsync(cancellationToken);
        var models = await _metadataCache.GetRouteModelsAsync(cancellationToken);
        return Ok(models);
    }

    /// <summary>
    /// 按模型发现可用站点。
    /// </summary>
    [HttpGet("discover-sites")]
    public async Task<IActionResult> DiscoverSites(
        [FromQuery] string modelName,
        CancellationToken cancellationToken)
    {
        using var lease = await RouteGraphMutationGate.AcquireAsync(cancellationToken);
        var results = await _metadataCache.GetDiscoveredSitesAsync(modelName, cancellationToken);
        return Ok(results);
    }

    /// <summary>
    /// 获取路由规则列表。
    /// </summary>
    [HttpGet("list")]
    public async Task<IActionResult> ListRules(
        [FromQuery] string modelName,
        CancellationToken cancellationToken)
    {
        using var lease = await RouteGraphMutationGate.AcquireAsync(cancellationToken);
        var result = await _metadataCache.GetRouteRulesAsync(modelName, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// 保存路由规则。
    /// </summary>
    [HttpPost("save")]
    public async Task<IActionResult> SaveRules(
        [FromBody] SaveRouteRulesRequest request,
        CancellationToken cancellationToken)
    {
        var entryName = (request.ExternalModelName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(entryName))
            return BadRequest(new { message = "模型名称不能为空" });

        if (request.Rules is null || request.Rules.Any(x => x is null))
            return BadRequest(new { message = "规则不能为空" });
        RouteTargetsMutationSnapshot? mutationSnapshot = null;
        var routeRefreshDeferred = false;
        var result = await ExecuteGraphMutationAsync(async db =>
        {
            var entries = await db.ProxyRouteEntries.ToListAsync(cancellationToken);
            var allRules = await db.ProxyRouteRules.ToListAsync(cancellationToken);
            var existingEntry = entries.FirstOrDefault(x => x.EntryName == entryName);
            if (existingEntry?.ManagedByPoolId is not null)
                return Conflict(new { message = "号池托管入口不能手工修改" });
            var oldRules = allRules.Where(x => x.ExternalModelName == entryName).ToList();
            if (!request.SupportsRouteReferences && (oldRules.Any(IsReference) || request.Rules.Any(x => NormalizeTargetKind(x.TargetKind) == "RouteEntry")))
                return Conflict(new { message = "客户端不支持入口引用，请升级后重试" });

            var parent = existingEntry ?? new ProxyRouteEntry { EntryName = entryName };
            var newRules = new List<ProxyRouteRule>();
            var modelOrder = new List<string>();
            var sites = (await db.Sites.Select(x => x.Id).ToListAsync(cancellationToken)).ToHashSet();
            foreach (var item in request.Rules)
            {
                var kind = NormalizeTargetKind(item.TargetKind);
                if (kind != "Site" && kind != "RouteEntry") return BadRequest(new { message = "未知目标类型" });
                if (!TryNormalizeAvailability(item.AvailabilityMode, item.TimeRangesJson, out var availability))
                    return BadRequest(new { message = "时间范围配置无效" });
                ProxyRouteEntry? target = null;
                if (kind == "RouteEntry")
                {
                    if (item.SiteId != Guid.Empty || !string.IsNullOrWhiteSpace(item.SiteModelName)
                        || item.TargetRouteEntryId is null || item.TargetRouteEntryId == Guid.Empty)
                        return BadRequest(new { message = "入口引用必须使用入口标识且不能包含站点字段" });
                    target = entries.FirstOrDefault(x => x.Id == item.TargetRouteEntryId);
                    // Existing broken references can be preserved/reordered, but never rebound by name.
                    if (target is null && !oldRules.Any(x => IsReference(x) && x.TargetRouteEntryId == item.TargetRouteEntryId))
                        return BadRequest(new { message = "引用入口不存在" });
                }
                else if (item.SiteId == Guid.Empty || !sites.Contains(item.SiteId)
                    || item.TargetRouteEntryId is not null || !string.IsNullOrWhiteSpace(item.TargetRouteEntryName))
                    return BadRequest(new { message = "站点规则包含无效站点或入口引用字段" });

                var model = kind == "RouteEntry" ? target?.EntryName ?? item.TargetRouteEntryName ?? string.Empty
                    : string.IsNullOrWhiteSpace(item.UpstreamModelName) ? item.SiteModelName : item.UpstreamModelName.Trim();
                if (!modelOrder.Contains(model)) modelOrder.Add(model);
                newRules.Add(new ProxyRouteRule
                {
                    ExternalModelName = entryName, TargetKind = kind,
                    TargetRouteEntryId = kind == "RouteEntry" ? item.TargetRouteEntryId : null,
                    TargetRouteEntryName = kind == "RouteEntry" ? target?.EntryName
                        ?? oldRules.First(x => IsReference(x) && x.TargetRouteEntryId == item.TargetRouteEntryId).TargetRouteEntryName : null,
                    SiteId = kind == "RouteEntry" ? Guid.Empty : item.SiteId,
                    SiteModelName = kind == "RouteEntry" ? string.Empty : item.SiteModelName,
                    UpstreamModelName = model, Priority = newRules.Count, ModelPriority = modelOrder.IndexOf(model),
                    InstancePriority = newRules.Count(x => x.UpstreamModelName == model),
                    IsEnabled = item.IsEnabled, AvailabilityMode = availability.Mode, TimeRangesJson = availability.TimeRangesJson
                });
            }
            var graphEntries = entries.Where(x => x.Id != parent.Id).Append(parent).ToList();
            var graphRules = allRules.Where(x => x.ExternalModelName != entryName).Concat(newRules).ToList();
            var graphError = ValidateGraph(parent, graphEntries, graphRules);
            if (graphError is not null) return BadRequest(new { message = graphError });
            mutationSnapshot = await _metadataCache.CaptureMutationSnapshotAsync(entryName, cancellationToken);
            mutationSnapshot = await _metadataCache.IncludeProposedRouteTargetsAsync(
                mutationSnapshot, db, newRules, graphEntries, graphRules, cancellationToken);
            if (existingEntry is null) await db.InsertAsync(parent, cancellationToken);
            if (oldRules.Count > 0) await db.DeleteRangeAsync(oldRules, cancellationToken);
            if (newRules.Count > 0) await db.InsertRangeAsync(newRules, cancellationToken);
            return Ok(new { message = "保存成功" });
        }, cancellationToken, () =>
        {
            // The prewrite token owns the effective source and dependent-parent generations, even
            // if a credential refresh evicts the shared cache before this transaction commits.
            routeRefreshDeferred = _concurrencyLimiter.TryDeferRuntimeRouteTargetsRefresh(mutationSnapshot!);
            if (!routeRefreshDeferred) _metadataCache.InvalidateRuntimeRouteTargets();
            return Task.CompletedTask;
        });
        return result is OkObjectResult && routeRefreshDeferred
            ? Ok(new { message = "保存成功，调用中的模型会在当前请求结束后生效" }) : result;
    }

    /// <summary>Reorder and enable existing pool candidates without replacing any rules.</summary>
    [HttpPost("pool-queue")]
    public async Task<IActionResult> SavePoolQueue([FromBody] SavePoolQueueRequest request, CancellationToken cancellationToken)
    {
        if (request.EntryId == Guid.Empty || request.Rules is null || request.Rules.Any(x => x is null || x.IsEnabled is null || x.ExpectedIsEnabled is null || x.ExpectedPriority is null || x.ExpectedModelPriority is null || x.ExpectedInstancePriority is null))
            return BadRequest(new { message = "候选队列无效" });
        RouteTargetsMutationSnapshot? snapshot = null;
        var deferred = false;
        var result = await ExecuteGraphMutationAsync(async db =>
        {
            var entry = await db.ProxyRouteEntries.InSingleAsync(request.EntryId);
            if (entry?.ManagedByPoolId is null)
                return Conflict(new { message = "号池入口已变更，请刷新后重试" });
            var existing = await db.ProxyRouteRules.Where(r => r.ExternalModelName == entry.EntryName).ToListAsync(cancellationToken);
            var byId = existing.ToDictionary(r => r.Id);
            if (request.Rules.Count != existing.Count || request.Rules.Select(r => r.RuleId).Distinct().Count() != existing.Count
                || request.Rules.Any(item => !byId.TryGetValue(item.RuleId, out var old)
                    || old.Priority != item.ExpectedPriority || old.ModelPriority != item.ExpectedModelPriority
                    || old.InstancePriority != item.ExpectedInstancePriority || old.IsEnabled != item.ExpectedIsEnabled))
                return Conflict(new { message = "候选队列已变更，请刷新后重试" });

            snapshot = await _metadataCache.CaptureMutationSnapshotAsync(entry.EntryName, cancellationToken);
            var modelOrder = new List<string>();
            var instanceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < request.Rules.Count; i++)
            {
                var item = request.Rules[i];
                var rule = byId[item.RuleId];
                if (!modelOrder.Contains(rule.UpstreamModelName)) modelOrder.Add(rule.UpstreamModelName);
                rule.Priority = i;
                rule.ModelPriority = modelOrder.IndexOf(rule.UpstreamModelName);
                rule.InstancePriority = instanceCounts.GetValueOrDefault(rule.UpstreamModelName);
                instanceCounts[rule.UpstreamModelName] = rule.InstancePriority + 1;
                rule.IsEnabled = item.IsEnabled!.Value;
            }
            snapshot = await _metadataCache.IncludeProposedRouteTargetsAsync(
                snapshot, db, existing, [entry], existing, cancellationToken);
            if (existing.Count > 0) await db.Client.Updateable(existing)
                .UpdateColumns(r => new { r.Priority, r.ModelPriority, r.InstancePriority, r.IsEnabled })
                .ExecuteCommandAsync(cancellationToken);
            return Ok(new { message = "候选队列已保存" });
        }, cancellationToken, () =>
        {
            deferred = _concurrencyLimiter.TryDeferRuntimeRouteTargetsRefresh(snapshot!);
            if (!deferred) _metadataCache.InvalidateRuntimeRouteTargets();
            return Task.CompletedTask;
        });
        return result is OkObjectResult && deferred
            ? Ok(new { message = "已保存，调用中的模型会在当前请求结束后生效" }) : result;
    }
    [HttpGet("reference-targets")]
    public async Task<IActionResult> GetReferenceTargets([FromQuery] string? parentEntryName, CancellationToken cancellationToken)
    {
        using var lease = await RouteGraphMutationGate.AcquireAsync(cancellationToken);
        var entries = await _dbContext.ProxyRouteEntries.ToListAsync(cancellationToken);
        var rules = await _dbContext.ProxyRouteRules.ToListAsync(cancellationToken);
        var parentName = (parentEntryName ?? string.Empty).Trim();
        var parent = entries.FirstOrDefault(x => x.EntryName == parentName)
            ?? new ProxyRouteEntry { EntryName = parentName };
        var counts = (await _metadataCache.GetRouteEntriesAsync(cancellationToken))
            .ToDictionary(x => x.EntryName, x => x.CandidateCount, StringComparer.Ordinal);
        return Ok(entries.OrderBy(x => x.EntryName).Select(target =>
        {
            var proposed = rules.Where(x => x.ExternalModelName != parentName).Append(new ProxyRouteRule
            {
                ExternalModelName = parentName, TargetKind = "RouteEntry", TargetRouteEntryId = target.Id
            }).ToList();
            var reason = parent.ManagedByPoolId is not null ? "号池托管入口不能手工修改"
                : ValidateGraph(parent, entries, proposed);
            return new { entryId = target.Id, entryName = target.EntryName, managedByPoolId = target.ManagedByPoolId,
                candidateCount = counts.GetValueOrDefault(target.EntryName), canSelect = reason is null, reason };
        }).ToList());
    }

    private static string NormalizeTargetKind(string? kind) => string.IsNullOrWhiteSpace(kind) ? "Site" : kind;
    private static bool IsReference(ProxyRouteRule rule) => NormalizeTargetKind(rule.TargetKind) == "RouteEntry";

    // All edges, including disabled/missing candidates, participate in structural validation.
    private static string? ValidateGraph(ProxyRouteEntry parent, IReadOnlyList<ProxyRouteEntry> entries, IReadOnlyList<ProxyRouteRule> rules)
    {
        var outgoing = rules.Where(x => x.ExternalModelName == parent.EntryName && IsReference(x)).ToList();
        if (outgoing.Count == 0) return null;
        if (rules.Any(x => IsReference(x) && x.TargetRouteEntryId == parent.Id))
            return "被引用入口不能再引用其他入口（不允许嵌套引用）";
        foreach (var edge in outgoing)
        {
            if (edge.TargetRouteEntryId == parent.Id) return "入口不能引用自身";
            var target = entries.FirstOrDefault(x => x.Id == edge.TargetRouteEntryId);
            if (target is not null && rules.Any(x => x.ExternalModelName == target.EntryName && IsReference(x)))
                return "不能引用包含入口引用的入口（不允许嵌套引用）";
        }
        return null;
    }

    private async Task<IActionResult> ExecuteGraphMutationAsync(
        Func<AppDbContext, Task<IActionResult>> mutation, CancellationToken ct, Func<Task>? afterCommit = null)
    {
        using var lease = await RouteGraphMutationGate.AcquireAsync(ct);
        // Never share a SqlSugarScope transaction with a parallel request/background writer.
        using var client = _dbContext.Client.CopyNew();
        var db = new AppDbContext(client, new SemaphoreSlim(1, 1));
        var inTransaction = false;
        try
        {
            await client.Ado.ExecuteCommandAsync("PRAGMA busy_timeout=5000;");
            await client.Ado.BeginTranAsync();
            inTransaction = true;
            var result = await mutation(db);
            if (result is not OkObjectResult)
            {
                await client.Ado.RollbackTranAsync();
                inTransaction = false;
                return result;
            }
            await client.Ado.CommitTranAsync();
            inTransaction = false;
            _metadataCache.InvalidateAdminRouteMetadata();
            if (afterCommit is null) _metadataCache.InvalidateRuntimeRouteTargets();
            else await afterCommit();
            return result;
        }
        catch (Exception ex) when (IsSqliteBusy(ex))
        {
            if (inTransaction) await client.Ado.RollbackTranAsync();
            return Conflict(new { message = "配置正在被其他连接修改，请重试" });
        }
        catch
        {
            if (inTransaction) await client.Ado.RollbackTranAsync();
            throw;
        }
    }

    private static bool IsSqliteBusy(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
            if (current.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("database table is locked", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("SQLITE_BUSY", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 规范化时间可用性配置，空配置和无效配置都回落为全天可用以兼容旧规则。
    /// </summary>
    private static bool TryNormalizeAvailability(
        string? mode,
        string? timeRangesJson,
        out (string Mode, string TimeRangesJson) availability)
    {
        var normalizedMode = string.Equals(mode, "AvailableOnly", StringComparison.Ordinal)
            ? "AvailableOnly"
            : string.Equals(mode, "Unavailable", StringComparison.Ordinal)
                ? "Unavailable"
                : "AllDay";
        if (normalizedMode == "AllDay" || string.IsNullOrWhiteSpace(timeRangesJson))
        {
            availability = ("AllDay", string.Empty);
            return true;
        }

        try
        {
            var ranges = JsonSerializer.Deserialize<List<RouteTimeRange?>>(timeRangesJson, JsonSerializerPresets.CaseInsensitive) ?? [];
            if (ranges.Any(x => x is null))
            {
                availability = default;
                return false;
            }

            var validRanges = ranges
                .Where(x => IsValidTimeText(x!.Start) && IsValidTimeText(x.End))
                .Select(x => new RouteTimeRange { Start = x!.Start.Trim(), End = x.End.Trim() })
                .ToList();
            availability = validRanges.Count == 0
                ? ("AllDay", string.Empty)
                : (normalizedMode, JsonSerializer.Serialize(validRanges, JsonSerializerPresets.CamelCase));
            return true;
        }
        catch (JsonException)
        {
            availability = ("AllDay", string.Empty);
            return true;
        }
    }

    /// <summary>
    /// 校验 HH:mm 时间文本，避免无效配置影响运行时选路。
    /// </summary>
    private static bool IsValidTimeText(string? value)
    {
        return TimeOnly.TryParseExact(value, "HH:mm", out _);
    }

    /// <summary>
    /// 切换规则启用状态。
    /// </summary>
    [HttpPost("toggle/{ruleId}")]
    public async Task<IActionResult> ToggleRule(Guid ruleId, CancellationToken cancellationToken)
    {
        string? changedEntry = null;
        return await ExecuteGraphMutationAsync(async db =>
        {
            var rule = await db.ProxyRouteRules.InSingleAsync(ruleId);
            if (rule is null) return NotFound(new { message = "规则不存在" });
            var entries = await db.ProxyRouteEntries.ToListAsync(cancellationToken);
            var parent = entries.FirstOrDefault(x => x.EntryName == rule.ExternalModelName);
            if (parent?.ManagedByPoolId is not null) return Conflict(new { message = "号池托管入口不能手工修改" });
            if (parent is not null)
            {
                var error = ValidateGraph(parent, entries, await db.ProxyRouteRules.ToListAsync(cancellationToken));
                if (error is not null) return BadRequest(new { message = error });
            }
            rule.IsEnabled = !rule.IsEnabled;
            changedEntry = rule.ExternalModelName;
            await db.UpdateAsync(rule, cancellationToken);
            return Ok(new { message = "状态已切换", isEnabled = rule.IsEnabled });
        }, cancellationToken, () =>
        {
            _metadataCache.InvalidateDeletedRouteEntry(changedEntry!);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 删除路由规则。
    /// </summary>
    [HttpPost("delete/{ruleId}")]
    public async Task<IActionResult> DeleteRule(Guid ruleId, CancellationToken cancellationToken)
    {
        string? changedEntry = null;
        return await ExecuteGraphMutationAsync(async db =>
        {
            var rule = await db.ProxyRouteRules.InSingleAsync(ruleId);
            if (rule is null) return NotFound(new { message = "规则不存在" });
            var parent = await db.ProxyRouteEntries.FirstAsync(x => x.EntryName == rule.ExternalModelName, cancellationToken);
            if (parent?.ManagedByPoolId is not null) return Conflict(new { message = "号池托管入口不能手工修改" });
            changedEntry = rule.ExternalModelName;
            await db.DeleteAsync(rule, cancellationToken);
            return Ok(new { message = "规则已删除" });
        }, cancellationToken, () =>
        {
            _metadataCache.InvalidateDeletedRouteEntry(changedEntry!);
            return Task.CompletedTask;
        });
    }
}
