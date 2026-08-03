using AITool.Infrastructure.Proxy;
using System.Text.Json;
using AITool.Application.Common;
using AITool.Application.Sites;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
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
    private readonly AdminCacheInvalidationService _adminCacheInvalidation;

    /// <summary>
    /// 初始化站点管理 API 控制器。
    /// </summary>
    public SitesApiController(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache, SiteCascadeDeleter cascadeDeleter, AdminCacheInvalidationService adminCacheInvalidation)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _cascadeDeleter = cascadeDeleter;
        _adminCacheInvalidation = adminCacheInvalidation;
    }

    /// <summary>
    /// 获取站点列表（按名称排序，过滤托管站点）。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            .Where(x => string.IsNullOrEmpty(x.ManagedSource))
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        return Ok(sites.Select(MapSiteToListItem));
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
        return Ok(ApiResponse.Ok(new
        {
            id = site.Id,
            name = site.Name,
            baseUrl = site.BaseUrl,
            endpointPathMode = SiteEndpointPathResolver.NormalizeMode(site.EndpointPathMode),
            supportsOpenAi = site.SupportsOpenAi,
            supportsAnthropic = site.SupportsAnthropic,
            protocolType = site.ProtocolType,
            isEnabled = site.IsEnabled,
            createdAt = site.CreatedAt
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

        var site = new Site
        {
            Name = payload.Name.Trim(),
            BaseUrl = payload.BaseUrl.Trim(),
            EndpointPathMode = SiteEndpointPathResolver.NormalizeMode(payload.EndpointPathMode),
            ApiKey = payload.ApiKey,
            ProtocolType = ResolveSiteProtocolType(payload.SupportsOpenAi, payload.SupportsAnthropic),
            SupportsOpenAi = payload.SupportsOpenAi,
            SupportsAnthropic = payload.SupportsAnthropic,
            IsEnabled = payload.IsEnabled
        };
        _dbContext.Sites.Add(site);
        // SqlSugar 的 Add 是立即执行（扩展方法，同步 ExecuteCommand），无需 SaveChanges。
        await _adminCacheInvalidation.InvalidateRouteTargetsAsync(cancellationToken);

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
        }
        site.SupportsOpenAi = payload.SupportsOpenAi;
        site.SupportsAnthropic = payload.SupportsAnthropic;
        site.ProtocolType = ResolveSiteProtocolType(payload.SupportsOpenAi, payload.SupportsAnthropic);
        site.IsEnabled = payload.IsEnabled;

        await _dbContext.UpdateAsync(site, cancellationToken);
        await _adminCacheInvalidation.InvalidateRouteTargetsAsync(cancellationToken);

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
        await _adminCacheInvalidation.InvalidateRouteTargetsAsync(cancellationToken);

        return Ok(ApiResponse.Ok(new { isEnabled = site.IsEnabled }, $"站点已{(site.IsEnabled ? "启用" : "禁用")}"));
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
        await _adminCacheInvalidation.InvalidateRouteTargetsAsync(cancellationToken);

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
        await _adminCacheInvalidation.InvalidateRouteTargetsAsync(cancellationToken);

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

        var exportData = sites.Select(s => new
        {
            id = s.Id,
            name = s.Name,
            baseUrl = s.BaseUrl,
            endpointPathMode = SiteEndpointPathResolver.NormalizeMode(s.EndpointPathMode),
            apiKey = s.ApiKey,
            supportsOpenAi = s.SupportsOpenAi,
            supportsAnthropic = s.SupportsAnthropic
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

            _dbContext.Sites.Add(new Site
            {
                Name = item.Name,
                BaseUrl = item.BaseUrl,
                EndpointPathMode = SiteEndpointPathResolver.NormalizeMode(item.EndpointPathMode),
                ApiKey = item.ApiKey,
                ProtocolType = ResolveSiteProtocolType(item.SupportsOpenAi, item.SupportsAnthropic),
                SupportsOpenAi = item.SupportsOpenAi,
                SupportsAnthropic = item.SupportsAnthropic,
                IsEnabled = true
            });
            created++;
        }

        // SqlSugar 的 Add 是立即执行（扩展方法，同步 ExecuteCommand），无需 SaveChanges。
        await _adminCacheInvalidation.InvalidateRouteTargetsAsync(cancellationToken);

        return Ok(ApiResponse.Ok(new { importedCount = created }, $"成功导入 {created} 个站点"));
    }

    /// <summary>
    /// 根据站点能力推导协议类型（与 PageModel 中逻辑一致）。
    /// </summary>
    private static string ResolveSiteProtocolType(bool supportsOpenAi, bool supportsAnthropic)
    {
        if (!supportsOpenAi && !supportsAnthropic)
        {
            return "Responses";
        }
        return supportsAnthropic && !supportsOpenAi ? "Anthropic" : "OpenAI";
    }

    /// <summary>
    /// 把站点实体映射为列表项（ApiKey 脱敏：只返回掩码前缀，不暴露完整密钥）。
    /// </summary>
    private static object MapSiteToListItem(Site site)
    {
        return new
        {
            id = site.Id,
            name = site.Name,
            baseUrl = site.BaseUrl,
            endpointPathMode = SiteEndpointPathResolver.NormalizeMode(site.EndpointPathMode),
            // 列表展示密钥脱敏，避免一次请求泄漏所有站点的完整密钥。
            apiKeyMasked = MaskApiKey(site.ApiKey),
            supportsOpenAi = site.SupportsOpenAi,
            supportsAnthropic = site.SupportsAnthropic,
            protocolType = site.ProtocolType,
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
    /// 是否启用（仅创建时生效）。
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
