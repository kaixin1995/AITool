using System.Text.Json;
using AITool.Application.Common;
using AITool.Application.Proxy;
using AITool.Application.Sites;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Application.Common;
using AITool.Admin.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// 站点管理 API：CRUD、启停、批量删除、导入、导出。
/// <para>
/// 迁移自 <c>Pages/Admin/Sites/*.cshtml.cs</c>（Index / Create / Edit / Import / Export 共 5 个 PageModel）。
/// 级联删除（清理 mappings / rules / 空路由入口）复用 <see cref="SiteCascadeDeleter"/>。
/// 所有写操作后失效路由缓存，使转发链路在 5s 内感知变更。
/// </para>
/// <para>
/// 托管站点（<see cref="Site.ManagedSource"/> 非空，如 Codex 账号自动创建）在列表/导出/删除中都被过滤，
/// 只能经对应账号管理页删除，避免 CodexAccount 成孤儿。
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/sites")]
public sealed class SitesApiController : ControllerBase
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
    /// 站点级联删除工具。
    /// </summary>
    private readonly SiteCascadeDeleter _cascadeDeleter;

    /// <summary>
    /// 初始化站点管理 API 控制器。
    /// </summary>
    public SitesApiController(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache, SiteCascadeDeleter cascadeDeleter)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _cascadeDeleter = cascadeDeleter;
    }

    /// <summary>
    /// 获取站点列表（按名称排序，默认过滤托管站点，可传 includeManaged=true 包含 OAuth/托管站点）。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeManaged = false, CancellationToken cancellationToken = default)
    {
        var sites = await _dbContext.Sites
            .Where(x => includeManaged || string.IsNullOrEmpty(x.ManagedSource))
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var siteIds = sites.Select(x => x.Id).ToList();
        // 一次性加载这些站点的 SiteKey，按 SiteId 分组，避免列表接口 N+1 查询。
        var allKeys = await _dbContext.SiteKeys
            .Where(k => siteIds.Contains(k.SiteId))
            .ToListAsync(cancellationToken);
        var keysBySite = allKeys.GroupBy(k => k.SiteId)
            .ToDictionary(g => g.Key, g => g.OrderBy(k => k.Priority).ThenBy(k => k.CreatedAt).ThenBy(k => k.Id).ToList());

        return Ok(sites.Select(s => MapSiteToListItem(s, keysBySite)));
    }

    /// <summary>
    /// 获取单个站点详情。
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var site = await _dbContext.Sites.InSingleAsync(id);
        if (site is null || !string.IsNullOrEmpty(site.ManagedSource))
        {
            return NotFound(ApiResponse.Fail("站点不存在", "site_not_found"));
        }

        // 编辑时密钥留空表示保留原值，详情接口不向浏览器返回原始密钥。
        // 返回该站点的 SiteKey 列表（KeyValue 脱敏），供前端多 Key 管理展示。
        var siteKeys = await _dbContext.SiteKeys
            .Where(k => k.SiteId == id)
            .OrderBy(k => k.Priority)
            .ThenBy(k => k.CreatedAt)
            .ThenBy(k => k.Id)
            .ToListAsync(cancellationToken);

        return Ok(ApiResponse.Ok(new
        {
            id = site.Id,
            name = site.Name,
            baseUrl = site.BaseUrl,
            endpointPathMode = SiteEndpointPathResolver.NormalizeMode(site.EndpointPathMode),
            supportsOpenAi = site.SupportsOpenAi,
            supportsAnthropic = site.SupportsAnthropic,
            supportsResponses = ProxyProtocolResolver.SupportsResponses(
                site.SupportsOpenAi,
                site.SupportsAnthropic,
                site.SupportsResponses,
                site.ProtocolType),
            protocolType = site.ProtocolType,
            clientEmulation = site.ClientEmulation,
            extraHeadersJson = site.ExtraHeadersJson ?? string.Empty,
            egressProxyUrl = site.EgressProxyUrl ?? string.Empty,
            isEnabled = site.IsEnabled,
            createdAt = site.CreatedAt,
            keys = siteKeys.Select(k => new
            {
                id = k.Id,
                keyValueMasked = MaskApiKey(k.KeyValue),
                remark = k.Remark ?? string.Empty,
                priority = k.Priority,
                isEnabled = k.IsEnabled,
                createdAt = k.CreatedAt
            })
        }));
    }

    /// <summary>
    /// 创建站点。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SitePayload payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload?.Name) || string.IsNullOrWhiteSpace(payload.BaseUrl))
        {
            return BadRequest(ApiResponse.Fail("站点名称和基础地址不能为空", "invalid_input"));
        }
        if (string.IsNullOrWhiteSpace(payload.ApiKey))
        {
            return BadRequest(ApiResponse.Fail("站点密钥不能为空", "invalid_input"));
        }
        if (!EgressProxyValidator.TryValidate(payload.EgressProxyUrl, out var createProxyError))
        {
            return BadRequest(ApiResponse.Fail(createProxyError, "invalid_egress_proxy"));
        }

        var site = new Site
        {
            Name = payload.Name.Trim(),
            BaseUrl = payload.BaseUrl.Trim(),
            EndpointPathMode = SiteEndpointPathResolver.NormalizeMode(payload.EndpointPathMode),
            ApiKey = payload.ApiKey,
            ProtocolType = ProxyProtocolResolver.ResolveSiteProtocolType(payload.SupportsOpenAi, payload.SupportsAnthropic, payload.SupportsResponses),
            SupportsOpenAi = payload.SupportsOpenAi,
            SupportsAnthropic = payload.SupportsAnthropic,
            SupportsResponses = ProxyProtocolResolver.SupportsResponses(
                payload.SupportsOpenAi,
                payload.SupportsAnthropic,
                payload.SupportsResponses),
            ClientEmulation = ClientEmulationConstants.Normalize(payload.ClientEmulation),
            ExtraHeadersJson = string.IsNullOrWhiteSpace(payload.ExtraHeadersJson) ? null : payload.ExtraHeadersJson.Trim(),
            EgressProxyUrl = string.IsNullOrWhiteSpace(payload.EgressProxyUrl) ? null : payload.EgressProxyUrl.Trim(),
            IsEnabled = payload.IsEnabled
        };
        _dbContext.Sites.Add(site);
        // 同时插入一条默认 SiteKey（Priority=0），使新站点立即具备多 Key 能力。
        // Site.ApiKey 保持同步，兼容健康检测/目录拉取等站点级回退路径。
        _dbContext.SiteKeys.Add(new SiteKey
        {
            SiteId = site.Id,
            KeyValue = payload.ApiKey,
            Remark = "默认",
            Priority = 0,
            IsEnabled = true
        });
        // SqlSugar 的 Add 是立即执行（扩展方法，同步 ExecuteCommand），无需 SaveChanges。
        _metadataCache.InvalidateRouteTargets();

        return Ok(ApiResponse.Ok(new { id = site.Id }, "站点已创建"));
    }

    /// <summary>
    /// 更新站点。apiKey 留空表示保留原有密钥（与编辑页一致）。
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SitePayload payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload?.Name) || string.IsNullOrWhiteSpace(payload.BaseUrl))
        {
            return BadRequest(ApiResponse.Fail("站点名称和基础地址不能为空", "invalid_input"));
        }
        if (!EgressProxyValidator.TryValidate(payload.EgressProxyUrl, out var updateProxyError))
        {
            return BadRequest(ApiResponse.Fail(updateProxyError, "invalid_egress_proxy"));
        }

        var site = await _dbContext.Sites.InSingleAsync(id);
        if (site is null || !string.IsNullOrEmpty(site.ManagedSource))
        {
            return NotFound(ApiResponse.Fail("站点不存在", "site_not_found"));
        }

        site.Name = payload.Name.Trim();
        site.BaseUrl = payload.BaseUrl.Trim();
        site.EndpointPathMode = SiteEndpointPathResolver.NormalizeMode(payload.EndpointPathMode);
        // 编辑时留空表示继续使用原有密钥。
        if (!string.IsNullOrWhiteSpace(payload.ApiKey))
        {
            site.ApiKey = payload.ApiKey;
            // 同步更新默认 SiteKey（Priority 最小的启用项）的 KeyValue，保持多 Key 链路与站点字段一致。
            // 若站点还没有 SiteKey（极端情况），则补建一条默认项。
            await SyncDefaultSiteKeyAsync(site.Id, payload.ApiKey, cancellationToken);
        }
        site.SupportsOpenAi = payload.SupportsOpenAi;
        site.SupportsAnthropic = payload.SupportsAnthropic;
        site.SupportsResponses = ProxyProtocolResolver.SupportsResponses(
            payload.SupportsOpenAi,
            payload.SupportsAnthropic,
            payload.SupportsResponses);
        site.ProtocolType = ProxyProtocolResolver.ResolveSiteProtocolType(payload.SupportsOpenAi, payload.SupportsAnthropic, site.SupportsResponses);
        site.ClientEmulation = ClientEmulationConstants.Normalize(payload.ClientEmulation);
        site.ExtraHeadersJson = string.IsNullOrWhiteSpace(payload.ExtraHeadersJson) ? null : payload.ExtraHeadersJson.Trim();
        site.EgressProxyUrl = string.IsNullOrWhiteSpace(payload.EgressProxyUrl) ? null : payload.EgressProxyUrl.Trim();
        site.IsEnabled = payload.IsEnabled;

        await _dbContext.UpdateAsync(site, cancellationToken);
        _metadataCache.InvalidateRouteTargets();

        return Ok(ApiResponse.Ok("站点已更新"));
    }

    /// <summary>
    /// 切换站点启用状态。
    /// </summary>
    [HttpPost("{id:guid}/toggle")]
    public async Task<IActionResult> Toggle(Guid id, CancellationToken cancellationToken)
    {
        var site = await _dbContext.Sites.InSingleAsync(id);
        if (site is null || !string.IsNullOrEmpty(site.ManagedSource))
        {
            return NotFound(ApiResponse.Fail("站点不存在", "site_not_found"));
        }

        site.IsEnabled = !site.IsEnabled;
        await _dbContext.UpdateAsync(site, cancellationToken);
        _metadataCache.InvalidateRouteTargets();

        return Ok(ApiResponse.Ok(new { isEnabled = site.IsEnabled }, $"站点已{(site.IsEnabled ? "启用" : "禁用")}"));
    }

    /// <summary>
    /// 获取站点的密钥列表（KeyValue 脱敏，不暴露完整密钥）。
    /// </summary>
    [HttpGet("{id:guid}/keys")]
    public async Task<IActionResult> ListKeys(Guid id, CancellationToken cancellationToken)
    {
        var site = await _dbContext.Sites.InSingleAsync(id);
        if (site is null || !string.IsNullOrEmpty(site.ManagedSource))
        {
            return NotFound(ApiResponse.Fail("站点不存在", "site_not_found"));
        }

        var keys = await _dbContext.SiteKeys
            .Where(k => k.SiteId == id)
            .OrderBy(k => k.Priority)
            .ThenBy(k => k.CreatedAt)
            .ThenBy(k => k.Id)
            .ToListAsync(cancellationToken);

        return Ok(ApiResponse.Ok(keys.Select(k => new
        {
            id = k.Id,
            keyValueMasked = MaskApiKey(k.KeyValue),
            remark = k.Remark ?? string.Empty,
            priority = k.Priority,
            isEnabled = k.IsEnabled,
            createdAt = k.CreatedAt
        })));
    }

    /// <summary>
    /// 新增站点密钥。
    /// </summary>
    [HttpPost("{id:guid}/keys")]
    public async Task<IActionResult> CreateKey(Guid id, [FromBody] SiteKeyUpsertRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.KeyValue))
        {
            return BadRequest(ApiResponse.Fail("密钥不能为空", "invalid_input"));
        }

        var site = await _dbContext.Sites.InSingleAsync(id);
        if (site is null || !string.IsNullOrEmpty(site.ManagedSource))
        {
            return NotFound(ApiResponse.Fail("站点不存在", "site_not_found"));
        }

        var key = new SiteKey
        {
            SiteId = id,
            KeyValue = request.KeyValue,
            Remark = string.IsNullOrWhiteSpace(request.Remark) ? null : request.Remark,
            Priority = request.Priority,
            IsEnabled = request.IsEnabled
        };
        _dbContext.SiteKeys.Add(key);
        _metadataCache.InvalidateRouteTargets();

        return Ok(ApiResponse.Ok(new { id = key.Id }, "密钥已添加"));
    }

    /// <summary>
    /// 更新站点密钥。KeyValue 留空表示保留原值（与站点编辑一致）。
    /// </summary>
    [HttpPut("{id:guid}/keys/{keyId:guid}")]
    public async Task<IActionResult> UpdateKey(Guid id, Guid keyId, [FromBody] SiteKeyUpsertRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(ApiResponse.Fail("请求体无效", "invalid_input"));
        }

        var site = await _dbContext.Sites.InSingleAsync(id);
        if (site is null || !string.IsNullOrEmpty(site.ManagedSource))
        {
            return NotFound(ApiResponse.Fail("站点不存在", "site_not_found"));
        }

        var key = await _dbContext.SiteKeys.InSingleAsync(keyId);
        if (key is null || key.SiteId != id)
        {
            return NotFound(ApiResponse.Fail("密钥不存在", "key_not_found"));
        }

        // KeyValue 留空表示保留原值。
        if (!string.IsNullOrWhiteSpace(request.KeyValue))
        {
            key.KeyValue = request.KeyValue;
        }
        key.Remark = string.IsNullOrWhiteSpace(request.Remark) ? null : request.Remark;
        key.Priority = request.Priority;
        key.IsEnabled = request.IsEnabled;
        await _dbContext.UpdateAsync(key, cancellationToken);
        _metadataCache.InvalidateRouteTargets();

        return Ok(ApiResponse.Ok("密钥已更新"));
    }

    /// <summary>
    /// 删除站点密钥。
    /// </summary>
    [HttpDelete("{id:guid}/keys/{keyId:guid}")]
    public async Task<IActionResult> DeleteKey(Guid id, Guid keyId, CancellationToken cancellationToken)
    {
        var site = await _dbContext.Sites.InSingleAsync(id);
        if (site is null || !string.IsNullOrEmpty(site.ManagedSource))
        {
            return NotFound(ApiResponse.Fail("站点不存在", "site_not_found"));
        }

        var key = await _dbContext.SiteKeys.InSingleAsync(keyId);
        if (key is null || key.SiteId != id)
        {
            return NotFound(ApiResponse.Fail("密钥不存在", "key_not_found"));
        }

        await _dbContext.DeleteAsync(key, cancellationToken);
        _metadataCache.InvalidateRouteTargets();

        return Ok(ApiResponse.Ok("密钥已删除"));
    }

    /// <summary>
    /// 切换站点密钥启用状态。
    /// </summary>
    [HttpPost("{id:guid}/keys/{keyId:guid}/toggle")]
    public async Task<IActionResult> ToggleKey(Guid id, Guid keyId, CancellationToken cancellationToken)
    {
        var site = await _dbContext.Sites.InSingleAsync(id);
        if (site is null || !string.IsNullOrEmpty(site.ManagedSource))
        {
            return NotFound(ApiResponse.Fail("站点不存在", "site_not_found"));
        }

        var key = await _dbContext.SiteKeys.InSingleAsync(keyId);
        if (key is null || key.SiteId != id)
        {
            return NotFound(ApiResponse.Fail("密钥不存在", "key_not_found"));
        }

        key.IsEnabled = !key.IsEnabled;
        await _dbContext.UpdateAsync(key, cancellationToken);
        _metadataCache.InvalidateRouteTargets();

        return Ok(ApiResponse.Ok(new { isEnabled = key.IsEnabled }, $"密钥已{(key.IsEnabled ? "启用" : "禁用")}"));
    }

    /// <summary>
    /// 同步默认 SiteKey 的 KeyValue：找到站点优先级最高的启用 SiteKey 更新其值；
    /// 站点没有任何 SiteKey 时补建一条默认项。保证站点字段与多 Key 链路一致。
    /// </summary>
    private async Task SyncDefaultSiteKeyAsync(Guid siteId, string keyValue, CancellationToken cancellationToken)
    {
        var keys = await _dbContext.SiteKeys
            .Where(k => k.SiteId == siteId)
            .OrderBy(k => k.Priority)
            .ThenBy(k => k.CreatedAt)
            .ThenBy(k => k.Id)
            .ToListAsync(cancellationToken);

        if (keys.Count == 0)
        {
            _dbContext.SiteKeys.Add(new SiteKey
            {
                SiteId = siteId,
                KeyValue = keyValue,
                Remark = "默认",
                Priority = 0,
                IsEnabled = true
            });
            return;
        }

        // 更新主 Key（优先级最高的启用项，没有启用项则取第一条）的 KeyValue。
        var primaryKey = keys.FirstOrDefault(k => k.IsEnabled) ?? keys[0];
        primaryKey.KeyValue = keyValue;
        await _dbContext.UpdateAsync(primaryKey, cancellationToken);
    }

    /// <summary>
    /// 删除单个站点（含级联清理 mappings / rules / 空路由入口）。
    /// 托管站点禁止从此接口删除。
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var site = await _dbContext.Sites.InSingleAsync(id);
        if (site is null)
        {
            return NotFound(ApiResponse.Fail("站点不存在", "site_not_found"));
        }
        if (!string.IsNullOrEmpty(site.ManagedSource))
        {
            return BadRequest(ApiResponse.Fail($"该站点为 {site.ManagedSource} 托管站点，请到对应账号管理页删除", "managed_site_protected"));
        }

        await _cascadeDeleter.RemoveSitesAsync([id], cancellationToken);
        // SiteCascadeDeleter 内部用 RemoveRange（立即执行），无需额外 SaveChanges。
        // 删站点会级联删除 SiteModelMappings，需同时失效模型元数据缓存，避免 /v1/models 短时残留。
        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();

        return Ok(ApiResponse.Ok("站点已删除"));
    }

    /// <summary>
    /// 批量删除站点（仅删除用户自建站点，托管站点自动跳过）。
    /// </summary>
    [HttpPost("bulk-delete")]
    public async Task<IActionResult> BulkDelete([FromBody] BulkDeleteRequest request, CancellationToken cancellationToken)
    {
        if (request?.SiteIds is null || request.SiteIds.Count == 0)
        {
            return BadRequest(ApiResponse.Fail("请先选择要删除的站点", "no_selection"));
        }

        // 仅删除用户自建站点（托管 Site 只能经 Codex 账号删除，避免 CodexAccount 成孤儿）。
        var siteIds = await _dbContext.Sites
            .Where(x => request.SiteIds.Contains(x.Id) && string.IsNullOrEmpty(x.ManagedSource))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        if (siteIds.Count == 0)
        {
            return Ok(ApiResponse.Ok(new { deletedCount = 0 }, "没有可删除的站点"));
        }

        var deletedCount = await _cascadeDeleter.RemoveSitesAsync(siteIds, cancellationToken);
        // SiteCascadeDeleter 内部用 RemoveRange（立即执行），无需额外 SaveChanges。
        // 删站点会级联删除 SiteModelMappings，需同时失效模型元数据缓存，避免 /v1/models 短时残留。
        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();

        return Ok(ApiResponse.Ok(new { deletedCount }, $"已批量删除 {deletedCount} 个站点"));
    }

    /// <summary>
    /// 导出全部用户自建站点为 JSON（camelCase，仅含恢复所需字段）。
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            .Where(s => string.IsNullOrEmpty(s.ManagedSource))
            .ToListAsync(cancellationToken);

        var siteIds = sites.Select(s => s.Id).ToList();
        var allKeys = await _dbContext.SiteKeys
            .Where(k => siteIds.Contains(k.SiteId))
            .ToListAsync(cancellationToken);
        var keysBySite = allKeys.GroupBy(k => k.SiteId)
            .ToDictionary(g => g.Key, g => g.OrderBy(k => k.Priority).ThenBy(k => k.CreatedAt).ThenBy(k => k.Id).ToList());

        var exportData = sites.Select(s =>
        {
            // 导出包含完整 SiteKey 列表（含原始 KeyValue），用于跨实例迁移。
            // 站点没有 SiteKey 时导出空数组（导入侧会用 apiKey 兜底建默认项）。
            keysBySite.TryGetValue(s.Id, out var keys);
            var keysExport = (keys ?? []).Select(k => new
            {
                keyValue = k.KeyValue,
                remark = k.Remark ?? string.Empty,
                priority = k.Priority,
                isEnabled = k.IsEnabled
            }).ToList();

            return new
            {
                id = s.Id,
                name = s.Name,
                baseUrl = s.BaseUrl,
                endpointPathMode = SiteEndpointPathResolver.NormalizeMode(s.EndpointPathMode),
                apiKey = s.ApiKey,
                supportsOpenAi = s.SupportsOpenAi,
                supportsAnthropic = s.SupportsAnthropic,
                supportsResponses = ProxyProtocolResolver.SupportsResponses(
                    s.SupportsOpenAi,
                    s.SupportsAnthropic,
                    s.SupportsResponses,
                    s.ProtocolType),
                clientEmulation = s.ClientEmulation,
                extraHeadersJson = s.ExtraHeadersJson,
                egressProxyUrl = s.EgressProxyUrl,
                keys = keysExport
            };
        });

        return Ok(exportData);
    }

    /// <summary>
    /// 批量导入站点。请求体为站点数组，跳过名称/地址/密钥为空的条目。
    /// </summary>
    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] List<SitePayload> items, CancellationToken cancellationToken)
    {
        if (items is null || items.Count == 0)
        {
            return BadRequest(ApiResponse.Fail("导入数据为空", "empty_input"));
        }

        var created = 0;
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.BaseUrl) || string.IsNullOrWhiteSpace(item.ApiKey))
            {
                continue;
            }

            var site = new Site
            {
                Name = item.Name,
                BaseUrl = item.BaseUrl,
                EndpointPathMode = SiteEndpointPathResolver.NormalizeMode(item.EndpointPathMode),
                ApiKey = item.ApiKey,
                ProtocolType = ProxyProtocolResolver.ResolveSiteProtocolType(item.SupportsOpenAi, item.SupportsAnthropic, item.SupportsResponses),
                SupportsOpenAi = item.SupportsOpenAi,
                SupportsAnthropic = item.SupportsAnthropic,
                SupportsResponses = ProxyProtocolResolver.SupportsResponses(
                    item.SupportsOpenAi,
                    item.SupportsAnthropic,
                    item.SupportsResponses),
                ClientEmulation = ClientEmulationConstants.Normalize(item.ClientEmulation),
                ExtraHeadersJson = string.IsNullOrWhiteSpace(item.ExtraHeadersJson) ? null : item.ExtraHeadersJson.Trim(),
                EgressProxyUrl = string.IsNullOrWhiteSpace(item.EgressProxyUrl) ? null : item.EgressProxyUrl.Trim(),
                IsEnabled = true
            };
            _dbContext.Sites.Add(site);

            // 兼容多 Key 导入：导出数据带 keys 数组时按其创建；否则用 apiKey 建一条默认 SiteKey。
            if (item.Keys is { Count: > 0 })
            {
                foreach (var key in item.Keys)
                {
                    if (string.IsNullOrWhiteSpace(key.KeyValue))
                    {
                        continue;
                    }
                    _dbContext.SiteKeys.Add(new SiteKey
                    {
                        SiteId = site.Id,
                        KeyValue = key.KeyValue,
                        Remark = string.IsNullOrWhiteSpace(key.Remark) ? null : key.Remark,
                        Priority = key.Priority,
                        IsEnabled = key.IsEnabled
                    });
                }
            }
            else
            {
                _dbContext.SiteKeys.Add(new SiteKey
                {
                    SiteId = site.Id,
                    KeyValue = item.ApiKey,
                    Remark = "默认",
                    Priority = 0,
                    IsEnabled = true
                });
            }

            created++;
        }

        // SqlSugar 的 Add 是立即执行（扩展方法，同步 ExecuteCommand），无需 SaveChanges。
        _metadataCache.InvalidateRouteTargets();

        return Ok(ApiResponse.Ok(new { importedCount = created }, $"成功导入 {created} 个站点"));
    }

    /// <summary>
    /// 把站点实体映射为列表项（ApiKey 脱敏：只返回掩码前缀，不暴露完整密钥）。
    /// 多 Key 场景下展示主 Key（Priority 最小的启用项）脱敏值与 Key 总数。
    /// </summary>
    private static object MapSiteToListItem(Site site, Dictionary<Guid, List<SiteKey>> keysBySite)
    {
        // 主 Key：优先级最高（Priority 最小）的启用项；没有启用项则取 Priority 最小的任意项；都没有则回退 site.ApiKey。
        var primaryKey = keysBySite.TryGetValue(site.Id, out var keys) && keys.Count > 0
            ? (keys.FirstOrDefault(k => k.IsEnabled) ?? keys[0])
            : null;
        var primaryKeyValue = primaryKey?.KeyValue ?? site.ApiKey;
        var keyCount = keys?.Count ?? 0;

        return new
        {
            id = site.Id,
            name = site.Name,
            baseUrl = site.BaseUrl,
            endpointPathMode = SiteEndpointPathResolver.NormalizeMode(site.EndpointPathMode),
            // 列表展示主 Key 脱敏，避免一次请求泄漏所有站点的完整密钥。
            apiKeyMasked = MaskApiKey(primaryKeyValue),
            // 站点的密钥总数，供前端展示"N 个 Key"。
            keyCount,
            supportsOpenAi = site.SupportsOpenAi,
            supportsAnthropic = site.SupportsAnthropic,
            supportsResponses = ProxyProtocolResolver.SupportsResponses(
                site.SupportsOpenAi,
                site.SupportsAnthropic,
                site.SupportsResponses,
                site.ProtocolType),
            protocolType = site.ProtocolType,
            clientEmulation = site.ClientEmulation,
            extraHeadersJson = site.ExtraHeadersJson,
            egressProxyUrl = site.EgressProxyUrl,
            isEnabled = site.IsEnabled,
            createdAt = site.CreatedAt
        };
    }

    /// <summary>
    /// 简单脱敏：保留前 4 位和后 4 位，中间用 *** 代替。
    /// </summary>
    private static string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return string.Empty;
        }
        if (apiKey.Length <= 8)
        {
            return "***";
        }
        return string.Concat(apiKey.AsSpan(0, 4), "***", apiKey.AsSpan(apiKey.Length - 4));
    }
}

/// <summary>
/// 站点创建/更新/导入的载荷。
/// </summary>
public sealed class SitePayload
{
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// 站点基础地址。
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;
    /// <summary>
    /// 接口路径模式（standard-root / versioned-base）。
    /// </summary>
    public string EndpointPathMode { get; set; } = SiteEndpointPathResolver.StandardRoot;
    /// <summary>
    /// 站点密钥。更新时留空表示保留原密钥。
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>
    /// 是否支持 OpenAI 协议。
    /// </summary>
    public bool SupportsOpenAi { get; set; } = true;
    /// <summary>
    /// 是否支持 Anthropic 协议。
    /// </summary>
    public bool SupportsAnthropic { get; set; }
    /// <summary>
    /// 是否支持 OpenAI Responses 原生接口。
    /// </summary>
    public bool SupportsResponses { get; set; }
    /// <summary>
    /// 客户端特征模拟预设类型（None | OpenCode | ClaudeCode | CodexCli | Antigravity | Custom）。
    /// </summary>
    public string ClientEmulation { get; set; } = "None";
    /// <summary>
    /// 自定义请求头 JSON 字符串。
    /// </summary>
    public string? ExtraHeadersJson { get; set; }
    /// <summary>
    /// 站点专用出口网络代理地址。
    /// </summary>
    public string? EgressProxyUrl { get; set; }
    /// <summary>
    /// 是否启用（仅创建时生效）。
    /// </summary>
    public bool IsEnabled { get; set; } = true;
    /// <summary>
    /// 站点密钥列表（导入时使用）。为空时用 <see cref="ApiKey"/> 建一条默认 SiteKey。
    /// </summary>
    public List<SiteKeyPayload>? Keys { get; set; }
}

/// <summary>
/// 站点密钥导入载荷（导出/导入用，携带完整 KeyValue）。
/// </summary>
public sealed class SiteKeyPayload
{
    /// <summary>
    /// 密钥值。
    /// </summary>
    public string KeyValue { get; set; } = string.Empty;
    /// <summary>
    /// 备注。
    /// </summary>
    public string? Remark { get; set; }
    /// <summary>
    /// 优先级。
    /// </summary>
    public int Priority { get; set; }
    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// 批量删除请求。
/// </summary>
public sealed class BulkDeleteRequest
{
    /// <summary>
    /// 要删除的站点 ID 列表。
    /// </summary>
    public List<Guid> SiteIds { get; set; } = [];
}

/// <summary>
/// 站点密钥新增/编辑请求。KeyValue 留空表示编辑时保留原值。
/// </summary>
public sealed class SiteKeyUpsertRequest
{
    /// <summary>
    /// 密钥值。编辑时留空表示保留原值。
    /// </summary>
    public string KeyValue { get; set; } = string.Empty;
    /// <summary>
    /// 备注，用于区分多个 Key。
    /// </summary>
    public string? Remark { get; set; }
    /// <summary>
    /// 优先级，数字越小越优先。
    /// </summary>
    public int Priority { get; set; }
    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}
