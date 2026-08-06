using AITool.Domain.Models;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Contracts;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 模型管理 API：模型库 CRUD、厂商规则集读写、站点关联映射增删、并发数配置、批量清空。
/// <para>
/// 扩充自原 <c>ModelsApiController</c>（仅 clear-all + concurrency），
/// 迁移自 <c>Pages/Admin/Models/Index.cshtml.cs</c> 与 <c>Edit.cshtml.cs</c>。
/// </para>
/// <para>
/// 删除模型时的级联清理：mappings → rules → monitors → 解绑 detection tasks → 模型本体 → 空路由入口。
/// 删除映射时的级联清理：映射本体 → SiteId+RemoteModelName 匹配的 rules → 空路由入口。
/// 所有写操作后失效模型元数据与路由缓存。
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/models")]
public sealed class ModelsApiController : ControllerBase
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
    /// 模型并发限制器。
    /// </summary>
    private readonly ModelConcurrencyLimiter _concurrencyLimiter;
    /// <summary>
    /// 厂商模型目录服务（读取/保存 model-vendor-catalog.json）。
    /// </summary>
    private readonly ModelVendorCatalogService _vendorCatalogService;
    /// <summary>
    /// 站点级联删除工具（复用空路由入口清理逻辑）。
    /// </summary>
    private readonly SiteCascadeDeleter _cascadeDeleter;

    /// <summary>
    /// 初始化模型管理 API 控制器。
    /// </summary>
    public ModelsApiController(
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        ModelConcurrencyLimiter concurrencyLimiter,
        ModelVendorCatalogService vendorCatalogService,
        SiteCascadeDeleter cascadeDeleter)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _concurrencyLimiter = concurrencyLimiter;
        _vendorCatalogService = vendorCatalogService;
        _cascadeDeleter = cascadeDeleter;
    }

    /// <summary>
    /// 清空模型相关数据。
    /// </summary>
    [HttpPost("clear-all")]
    public async Task<IActionResult> ClearAll(CancellationToken cancellationToken)
    {
        // 按依赖顺序删除：映射 → 监控 → 模型（SqlSugar 用 Db.Deleteable<T>() 清空整表）。
        var mappingCount = await _dbContext.SiteModelMappings.CountAsync(cancellationToken);
        var monitorCount = await _dbContext.ModelHealthMonitors.CountAsync(cancellationToken);
        var modelCount = await _dbContext.ModelLibraryItems.CountAsync(cancellationToken);

        await _dbContext.Client.Deleteable<SiteModelMapping>().ExecuteCommandAsync(cancellationToken);
        await _dbContext.Client.Deleteable<ModelHealthMonitor>().ExecuteCommandAsync(cancellationToken);
        await _dbContext.Client.Deleteable<ModelLibraryItem>().ExecuteCommandAsync(cancellationToken);

        _metadataCache.InvalidateModelMetadata();
        _metadataCache.InvalidateRouteTargets();

        return Ok(new
        {
            deletedModels = modelCount,
            deletedMappings = mappingCount,
            deletedMonitors = monitorCount
        });
    }

    /// <summary>
    /// 更新站点模型映射的最大并发数。
    /// </summary>
    [HttpPut("mappings/{mappingId:guid}/concurrency")]
    public async Task<IActionResult> UpdateConcurrency(
        Guid mappingId,
        [FromBody] UpdateConcurrencyRequest request,
        CancellationToken cancellationToken)
    {
        var mapping = await _dbContext.SiteModelMappings.InSingleAsync(mappingId);
        if (mapping is null)
        {
            return NotFound(ApiResponse.Fail("站点模型映射不存在", "mapping_not_found"));
        }

        mapping.MaxConcurrency = Math.Max(0, request.MaxConcurrency);
        await _dbContext.UpdateAsync(mapping, cancellationToken);

        // 配置保存后立即失效缓存，并同步更新运行中的限制器状态，仅影响后续新请求。
        _metadataCache.InvalidateRouteTargets();
        // 多 Key 场景：并发计数按 SiteKey 维度，需对站点每个启用的 Key 各同步一次运行时限制器，
        // 使调大上限后正在排队的请求能被立即唤醒。站点没有 SiteKey 时回退用 SiteId（兼容 Codex/老站点）。
        var siteKeyIds = await _dbContext.SiteKeys
            .Where(k => k.SiteId == mapping.SiteId && k.IsEnabled)
            .Select(k => k.Id)
            .ToListAsync(cancellationToken);
        if (siteKeyIds.Count > 0)
        {
            foreach (var keyId in siteKeyIds)
            {
                _concurrencyLimiter.UpdateLimit(keyId, mapping.RemoteModelName, mapping.MaxConcurrency);
            }
        }
        else
        {
            _concurrencyLimiter.UpdateLimit(mapping.SiteId, mapping.RemoteModelName, mapping.MaxConcurrency);
        }

        return Ok(ApiResponse.Ok(new { maxConcurrency = mapping.MaxConcurrency }));
    }

    /// <summary>
    /// 获取模型库列表（按厂商分组）。SiteCount 仅统计「映射启用 AND 站点启用」的关联。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var catalog = await _vendorCatalogService.GetOrCreateAsync(cancellationToken);

        // 启用站点 Id 集合，用于统计有效关联数。
        var enabledSiteIds = await _dbContext.Sites
            .Where(s => s.IsEnabled)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        // 仅统计「映射启用 AND 站点启用」的关联数（与原 LoadModelGroupsAsync 一致）。
        var siteCounts = (await _dbContext.SiteModelMappings
            .Where(m => m.IsEnabled && enabledSiteIds.Contains(m.SiteId))
            .ToListAsync(cancellationToken))
            .GroupBy(m => m.ModelLibraryItemId)
            .Select(g => new { ModelId = g.Key, Count = g.Count() })
            .ToDictionary(x => x.ModelId, x => x.Count);

        var models = await _dbContext.ModelLibraryItems
            .OrderBy(x => x.ModelName)
            .ToListAsync(cancellationToken);

        var vendorGroups = models
            .Select(m => new
            {
                id = m.Id,
                modelName = m.ModelName,
                displayName = m.DisplayName,
                isEnabled = m.IsEnabled,
                overrideReasoningEffort = m.OverrideReasoningEffort,
                compatibilityProfileId = m.CompatibilityProfileId,
                siteCount = siteCounts.GetValueOrDefault(m.Id),
                vendor = ModelVendorCatalogService.ResolveVendor(catalog, m.ModelName)
            })
            .GroupBy(x => x.vendor, ModelVendorNameComparer.Instance)
            .OrderBy(g => g.Key.SortOrder)
            .ThenBy(g => g.Key.VendorName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                vendorName = g.Key.VendorName,
                iconSvgBody = g.Key.IconSvgBody,
                headerBackground = g.Key.HeaderBackground,
                models = g.OrderBy(x => x.modelName, StringComparer.OrdinalIgnoreCase)
                          .Select(x => new
                          {
                              x.id,
                              x.modelName,
                              x.displayName,
                              x.isEnabled,
                              x.overrideReasoningEffort,
                              x.compatibilityProfileId,
                              x.siteCount
                          })
                          .ToList()
            })
            .ToList();

        return Ok(ApiResponse.Ok(new { vendorGroups }));
    }

    /// <summary>
    /// 创建模型。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ModelPayload payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload?.ModelName))
        {
            return BadRequest(ApiResponse.Fail("模型名称不能为空", "invalid_input"));
        }

        // 唯一性校验：ModelName 有唯一索引，重复会抛异常。
        var model = new ModelLibraryItem
        {
            ModelName = payload.ModelName.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(payload.DisplayName) ? payload.ModelName.Trim() : payload.DisplayName.Trim(),
            OverrideReasoningEffort = (payload.OverrideReasoningEffort ?? string.Empty).Trim(),
            CompatibilityProfileId = payload.CompatibilityProfileId,
            IsEnabled = payload.IsEnabled
        };

        try
        {
            _dbContext.ModelLibraryItems.Add(model);
        }
        catch (Exception ex)
        {
            return Ok(ApiResponse.Fail($"创建失败：{ex.Message}", "create_failed"));
        }

        _metadataCache.InvalidateModelMetadata();
        _metadataCache.InvalidateRouteTargets();

        return Ok(ApiResponse.Ok(new { id = model.Id }, "模型已创建"));
    }

    /// <summary>
    /// 获取模型详情（含关联站点映射、可选站点、兼容规则集选项）。
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var model = await _dbContext.ModelLibraryItems.InSingleAsync(id);
        if (model is null)
        {
            return NotFound(ApiResponse.Fail("模型不存在", "model_not_found"));
        }

        // 关联站点映射列表：数据库侧先过滤当前模型的映射（避免拉全表），再内存 join 站点名。
        var modelMappings = await _dbContext.SiteModelMappings
            .Where(x => x.ModelLibraryItemId == id)
            .ToListAsync(cancellationToken);
        var mappedSiteIds = modelMappings.Select(x => x.SiteId).Distinct().ToList();
        var mappedSites = mappedSiteIds.Count > 0
            ? await _dbContext.Sites.Where(s => mappedSiteIds.Contains(s.Id)).ToListAsync(cancellationToken)
            : new List<Site>();
        var siteLookup = mappedSites.ToDictionary(s => s.Id);
        var siteMappings = (
                from mapping in modelMappings
                where siteLookup.ContainsKey(mapping.SiteId)
                join site in mappedSites on mapping.SiteId equals site.Id
                orderby site.Name, mapping.RemoteModelName
                select new
                {
                    mappingId = mapping.Id,
                    siteId = site.Id,
                    siteName = site.Name,
                    remoteModelName = mapping.RemoteModelName,
                    isEnabled = mapping.IsEnabled,
                    maxConcurrency = mapping.MaxConcurrency
                })
            .ToList();

        // 可选站点（启用且未被当前模型关联）。
        var availableSites = await _dbContext.Sites
            .Where(x => x.IsEnabled && !mappedSiteIds.Contains(x.Id))
            .OrderBy(x => x.Name)
            .Select(x => new { id = x.Id, name = x.Name })
            .ToListAsync(cancellationToken);

        // 可选兼容规则集。
        var profiles = await _dbContext.CompatibilityProfiles
            .Where(p => p.IsEnabled)
            .OrderBy(p => p.Name)
            .Select(p => new { id = p.Id, name = p.Name })
            .ToListAsync(cancellationToken);

        return Ok(ApiResponse.Ok(new
        {
            id = model.Id,
            modelName = model.ModelName,
            displayName = model.DisplayName,
            isEnabled = model.IsEnabled,
            overrideReasoningEffort = model.OverrideReasoningEffort,
            compatibilityProfileId = model.CompatibilityProfileId,
            siteMappings,
            availableSites,
            availableProfiles = profiles
        }));
    }

    /// <summary>
    /// 更新模型基础字段（名称、显示名、启用、思考等级覆盖、兼容规则集）。
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ModelPayload payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload?.ModelName))
        {
            return BadRequest(ApiResponse.Fail("模型名称不能为空", "invalid_input"));
        }

        var model = await _dbContext.ModelLibraryItems.InSingleAsync(id);
        if (model is null)
        {
            return NotFound(ApiResponse.Fail("模型不存在", "model_not_found"));
        }

        model.ModelName = payload.ModelName.Trim();
        model.DisplayName = string.IsNullOrWhiteSpace(payload.DisplayName) ? payload.ModelName.Trim() : payload.DisplayName.Trim();
        model.IsEnabled = payload.IsEnabled;
        model.OverrideReasoningEffort = (payload.OverrideReasoningEffort ?? string.Empty).Trim();
        model.CompatibilityProfileId = payload.CompatibilityProfileId;

        await _dbContext.UpdateAsync(model, cancellationToken);
        _metadataCache.InvalidateModelMetadata();
        _metadataCache.InvalidateRouteTargets();

        return Ok(ApiResponse.Ok("模型已更新"));
    }

    /// <summary>
    /// 切换模型启用状态。
    /// </summary>
    [HttpPost("{id:guid}/toggle")]
    public async Task<IActionResult> Toggle(Guid id, CancellationToken cancellationToken)
    {
        var model = await _dbContext.ModelLibraryItems.InSingleAsync(id);
        if (model is null)
        {
            return NotFound(ApiResponse.Fail("模型不存在", "model_not_found"));
        }

        model.IsEnabled = !model.IsEnabled;
        await _dbContext.UpdateAsync(model, cancellationToken);
        _metadataCache.InvalidateModelMetadata();
        _metadataCache.InvalidateRouteTargets();

        return Ok(ApiResponse.Ok(new { isEnabled = model.IsEnabled }, $"模型已{(model.IsEnabled ? "启用" : "禁用")}"));
    }

    /// <summary>
    /// 删除模型（含级联清理：mappings / rules / monitors / 解绑 detection tasks / 空路由入口）。
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var model = await _dbContext.ModelLibraryItems.InSingleAsync(id);
        if (model is null)
        {
            return NotFound(ApiResponse.Fail("模型不存在或已被删除", "model_not_found"));
        }

        // 1. 查该模型的所有站点映射，提取 (SiteId, RemoteModelName) 对。
        var mappings = await _dbContext.SiteModelMappings
            .Where(x => x.ModelLibraryItemId == id)
            .ToListAsync(cancellationToken);
        var mappingPairs = mappings.Select(x => new { x.SiteId, x.RemoteModelName }).ToList();
        var mappingSiteIds = mappingPairs.Select(x => x.SiteId).Distinct().ToList();

        // 2. 查候选路由规则（ExternalModelName 匹配 或 SiteId ∈ 映射站点）。
        var candidateRules = await _dbContext.ProxyRouteRules
            .Where(x => x.ExternalModelName == model.ModelName || mappingSiteIds.Contains(x.SiteId))
            .ToListAsync(cancellationToken);
        // 内存精确过滤：名称匹配 或 (站点匹配 AND 远程名匹配)。
        var affectedRules = candidateRules
            .Where(x => x.ExternalModelName == model.ModelName
                || mappingPairs.Any(p => p.SiteId == x.SiteId && p.RemoteModelName == x.SiteModelName))
            .ToList();

        // 3. 受影响的入口名（用于清理空 entry）。
        var affectedEntryNames = affectedRules
            .Select(x => x.ExternalModelName)
            .Append(model.ModelName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // 4. 健康监控。
        var affectedMonitors = await _dbContext.ModelHealthMonitors
            .Where(x => x.ModelLibraryItemId == id)
            .ToListAsync(cancellationToken);

        // 5. 检测任务（解绑而非删除，避免后台继续引用已删除模型）。
        var affectedDetectionTasks = await _dbContext.DetectionTasks
            .Where(x => x.ModelLibraryItemId == id)
            .ToListAsync(cancellationToken);

        // 6. 执行删除。
        if (mappings.Count > 0)
        {
            _dbContext.SiteModelMappings.RemoveRange(mappings);
        }
        if (affectedRules.Count > 0)
        {
            _dbContext.ProxyRouteRules.RemoveRange(affectedRules);
        }
        if (affectedMonitors.Count > 0)
        {
            _dbContext.ModelHealthMonitors.RemoveRange(affectedMonitors);
        }
        foreach (var task in affectedDetectionTasks)
        {
            task.ModelLibraryItemId = null;
            await _dbContext.UpdateAsync(task, cancellationToken);
        }
        _dbContext.ModelLibraryItems.Remove(model);

        // 7. 清理空路由入口。
        await _cascadeDeleter.CleanupEmptyRouteEntriesAsync(affectedEntryNames, cancellationToken);

        // 8. 失效缓存。
        _metadataCache.InvalidateModelMetadata();
        _metadataCache.InvalidateRouteTargets();

        return Ok(ApiResponse.Ok(
            $"模型已删除，并清理了 {mappings.Count} 条站点关联、{affectedRules.Count} 条相关路由规则、{affectedMonitors.Count} 条健康监控，并解绑了 {affectedDetectionTasks.Count} 个检测任务"));
    }

    /// <summary>
    /// 获取厂商规则集（model-vendor-catalog.json 的完整内容，供编辑器回填）。
    /// </summary>
    [HttpGet("vendor-catalog")]
    public async Task<IActionResult> GetVendorCatalog(CancellationToken cancellationToken)
    {
        var catalog = await _vendorCatalogService.GetOrCreateAsync(cancellationToken);
        return Ok(catalog);
    }

    /// <summary>
    /// 保存厂商规则集。内部会校验规则引用的厂商存在、regex 模式合法（非法抛异常）。
    /// </summary>
    [HttpPut("vendor-catalog")]
    public async Task<IActionResult> SaveVendorCatalog([FromBody] ModelVendorCatalog catalog, CancellationToken cancellationToken)
    {
        if (catalog is null)
        {
            return BadRequest(ApiResponse.Fail("厂商规则数据不能为空", "empty_input"));
        }

        try
        {
            await _vendorCatalogService.SaveAsync(catalog, cancellationToken);
        }
        catch (Exception ex)
        {
            return Ok(ApiResponse.Fail($"保存失败：{ex.Message}", "save_failed"));
        }

        return Ok(ApiResponse.Ok("厂商规则已保存"));
    }

    /// <summary>
    /// 为模型添加站点关联映射（已存在则更新启用状态，标记 LastStatus="manual"）。
    /// </summary>
    [HttpPost("{id:guid}/mappings")]
    public async Task<IActionResult> AddMapping(Guid id, [FromBody] AddMappingRequest request, CancellationToken cancellationToken)
    {
        if (request.SiteId == Guid.Empty)
        {
            return BadRequest(ApiResponse.Fail("请选择站点", "site_required"));
        }
        if (string.IsNullOrWhiteSpace(request.RemoteModelName))
        {
            return BadRequest(ApiResponse.Fail("请填写站点模型名", "remote_model_name_required"));
        }

        var model = await _dbContext.ModelLibraryItems.InSingleAsync(id);
        if (model is null)
        {
            return NotFound(ApiResponse.Fail("模型不存在", "model_not_found"));
        }

        // 按主键取单行，用 InSingleAsync 避免 ToListAsync 实体化。
        var site = await _dbContext.Sites.InSingleAsync(request.SiteId);
        if (site is null || !site.IsEnabled)
        {
            return BadRequest(ApiResponse.Fail("所选站点不存在或已禁用", "site_unavailable"));
        }

        var remoteModelName = request.RemoteModelName.Trim();
        // 按组合键取单行，加 Take(1) 让 SQL 加 LIMIT 1。
        var existingMapping = (await _dbContext.SiteModelMappings
            .Where(x => x.SiteId == request.SiteId && x.RemoteModelName == remoteModelName)
            .Take(1)
            .ToListAsync(cancellationToken))
            .FirstOrDefault();

        if (existingMapping is not null)
        {
            existingMapping.ModelLibraryItemId = id;
            existingMapping.IsEnabled = request.IsEnabled;
            existingMapping.LastStatus = "manual";
            await _dbContext.UpdateAsync(existingMapping, cancellationToken);
        }
        else
        {
            _dbContext.SiteModelMappings.Add(new SiteModelMapping
            {
                SiteId = request.SiteId,
                ModelLibraryItemId = id,
                RemoteModelName = remoteModelName,
                LastStatus = "manual",
                IsEnabled = request.IsEnabled
            });
        }

        _metadataCache.InvalidateModelMetadata();
        _metadataCache.InvalidateRouteTargets();

        return Ok(ApiResponse.Ok("关联站点已添加"));
    }

    /// <summary>
    /// 删除模型的站点关联映射（级联清理 SiteId+RemoteModelName 匹配的路由规则 + 空路由入口）。
    /// </summary>
    [HttpDelete("{id:guid}/mappings/{mappingId:guid}")]
    public async Task<IActionResult> DeleteMapping(Guid id, Guid mappingId, CancellationToken cancellationToken)
    {
        // 按主键取单行，用 InSingleAsync。
        var mapping = await _dbContext.SiteModelMappings.InSingleAsync(mappingId);
        if (mapping is null || mapping.ModelLibraryItemId != id)
        {
            return NotFound(ApiResponse.Fail("关联映射不存在", "mapping_not_found"));
        }

        var model = await _dbContext.ModelLibraryItems.InSingleAsync(id);
        if (model is null)
        {
            return NotFound(ApiResponse.Fail("模型不存在", "model_not_found"));
        }

        // 查受影响规则：SiteId + RemoteModelName 精确匹配。
        var affectedRules = await _dbContext.ProxyRouteRules
            .Where(x => x.SiteId == mapping.SiteId && x.SiteModelName == mapping.RemoteModelName)
            .ToListAsync(cancellationToken);

        var affectedEntryNames = affectedRules
            .Select(x => x.ExternalModelName)
            .Append(model.ModelName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        _dbContext.SiteModelMappings.Remove(mapping);
        if (affectedRules.Count > 0)
        {
            _dbContext.ProxyRouteRules.RemoveRange(affectedRules);
        }
        await _cascadeDeleter.CleanupEmptyRouteEntriesAsync(affectedEntryNames, cancellationToken);

        _metadataCache.InvalidateModelMetadata();
        _metadataCache.InvalidateRouteTargets();

        return Ok(ApiResponse.Ok($"站点关联已删除，并清理了 {affectedRules.Count} 条相关路由规则"));
    }
}

/// <summary>
/// 更新并发数请求体。
/// </summary>
public sealed class UpdateConcurrencyRequest
{
    /// <summary>
    /// 最大并发数，0 表示不限制。
    /// </summary>
    public int MaxConcurrency { get; set; }
}

/// <summary>
/// 模型创建/更新载荷。
/// </summary>
public sealed class ModelPayload
{
    /// <summary>
    /// 模型名称（唯一）。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;
    /// <summary>
    /// 显示名称（空时回退为 ModelName）。
    /// </summary>
    public string? DisplayName { get; set; }
    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; } = true;
    /// <summary>
    /// 强制覆盖的思考等级（空=透传）。
    /// </summary>
    public string? OverrideReasoningEffort { get; set; }
    /// <summary>
    /// 关联的兼容规则集 Id（可空）。
    /// </summary>
    public Guid? CompatibilityProfileId { get; set; }
}

/// <summary>
/// 添加站点关联映射请求。
/// </summary>
public sealed class AddMappingRequest
{
    /// <summary>
    /// 站点 Id。
    /// </summary>
    public Guid SiteId { get; set; }
    /// <summary>
    /// 站点上的远程模型名。
    /// </summary>
    public string RemoteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 该映射是否启用。
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// 按 VendorName 不区分大小写比较 <see cref="ModelVendorDefinition"/>，
/// 用于模型列表按厂商分组（与原 Razor Pages 的 ModelVendorDefinitionComparer 行为一致）。
/// </summary>
internal sealed class ModelVendorNameComparer : IEqualityComparer<ModelVendorDefinition>
{
    /// <summary>
    /// 单例实例。
    /// </summary>
    public static readonly ModelVendorNameComparer Instance = new();

    public bool Equals(ModelVendorDefinition? x, ModelVendorDefinition? y)
        => string.Equals(x?.VendorName, y?.VendorName, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(ModelVendorDefinition obj)
        => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.VendorName ?? string.Empty);
}
