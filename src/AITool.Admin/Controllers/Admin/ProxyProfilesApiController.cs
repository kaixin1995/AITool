using System.Diagnostics;
using System.Net;
using AITool.Application.Common;
using AITool.Application.Operations;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Application.Common;
using AITool.Admin.Services;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;
using SqlSugar;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// 网络出口代理方案管理控制器（供调试工具及全局下拉选择使用）。
/// </summary>
[ApiController]
[Route("api/admin/developer/proxy-profiles")]
public class ProxyProfilesApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache? _metadataCache;
    private readonly ISystemRuntimeSettingsService _settingsService;

    public ProxyProfilesApiController(
        AppDbContext dbContext,
        ISystemRuntimeSettingsService settingsService,
        ProxyRequestMetadataCache? metadataCache = null)
    {
        _dbContext = dbContext;
        _settingsService = settingsService;
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 开发者功能总闸或出口网络代理开关关闭时接口整体隐藏（404）。
    /// </summary>
    private async Task<bool> IsProxyProfilesEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsService.GetOrCreateAsync(cancellationToken);
        return settings is not null && settings.DeveloperFeaturesEnabled && settings.DeveloperProxyProfilesEnabled;
    }

    /// <summary>
    /// 获取全部出口代理方案列表。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!await IsProxyProfilesEnabledAsync(cancellationToken))
        {
            return NotFound();
        }


        var profiles = await _dbContext.ProxyProfiles
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

        return Ok(profiles.Select(p => new
        {
            id = p.Id,
            key = p.Key,
            name = p.Name,
            proxyUrl = p.ProxyUrl,
            description = p.Description,
            isEnabled = p.IsEnabled,
            sortOrder = p.SortOrder,
            createdAt = p.CreatedAt,
            updatedAt = p.UpdatedAt
        }));
    }

    /// <summary>
    /// 获取单个代理方案详情。
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        if (!await IsProxyProfilesEnabledAsync(cancellationToken))
        {
            return NotFound();
        }


        var profile = await _dbContext.ProxyProfiles.InSingleAsync(id);
        if (profile is null)
        {
            return NotFound(ApiResponse.Fail("代理方案不存在", "proxy_not_found"));
        }

        return Ok(ApiResponse.Ok(new
        {
            id = profile.Id,
            key = profile.Key,
            name = profile.Name,
            proxyUrl = profile.ProxyUrl,
            description = profile.Description,
            isEnabled = profile.IsEnabled,
            sortOrder = profile.SortOrder,
            createdAt = profile.CreatedAt,
            updatedAt = profile.UpdatedAt
        }));
    }

    /// <summary>
    /// 创建出口代理方案。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ProxyProfilePayload payload, CancellationToken cancellationToken)
    {
        if (!await IsProxyProfilesEnabledAsync(cancellationToken))
        {
            return NotFound();
        }


        if (string.IsNullOrWhiteSpace(payload?.Key) || string.IsNullOrWhiteSpace(payload.Name) || string.IsNullOrWhiteSpace(payload.ProxyUrl))
        {
            return BadRequest(ApiResponse.Fail("方案标识 Key、名称和代理地址不能为空", "invalid_input"));
        }

        var key = payload.Key.Trim();
        var exists = await _dbContext.ProxyProfiles
            .AnyAsync(x => x.Key == key, cancellationToken);
        if (exists)
        {
            return Conflict(ApiResponse.Fail($"标识 Key '{key}' 已存在，请更换", "duplicate_key"));
        }

        var proxyUrl = payload.ProxyUrl.Trim();
        if (!EgressProxyValidator.TryValidate(proxyUrl, out var proxyError))
        {
            return BadRequest(ApiResponse.Fail($"代理地址格式不正确: {proxyError}", "invalid_proxy_url"));
        }

        var profile = new ProxyProfile
        {
            Key = key,
            Name = payload.Name.Trim(),
            ProxyUrl = proxyUrl,
            Description = payload.Description?.Trim(),
            IsEnabled = payload.IsEnabled,
            SortOrder = payload.SortOrder,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _dbContext.InsertAsync(profile, cancellationToken);
        _metadataCache?.InvalidateRouteTargets();
        _metadataCache?.InvalidateModelMetadata();

        return Ok(ApiResponse.Ok(new { id = profile.Id, key = profile.Key }, "代理方案已创建"));
    }

    /// <summary>
    /// 更新出口代理方案。
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ProxyProfilePayload payload, CancellationToken cancellationToken)
    {
        if (!await IsProxyProfilesEnabledAsync(cancellationToken))
        {
            return NotFound();
        }


        if (string.IsNullOrWhiteSpace(payload?.Name) || string.IsNullOrWhiteSpace(payload.ProxyUrl))
        {
            return BadRequest(ApiResponse.Fail("方案名称和代理地址不能为空", "invalid_input"));
        }

        var profile = await _dbContext.ProxyProfiles.InSingleAsync(id);
        if (profile is null)
        {
            return NotFound(ApiResponse.Fail("代理方案不存在", "proxy_not_found"));
        }

        var proxyUrl = payload.ProxyUrl.Trim();
        if (!EgressProxyValidator.TryValidate(proxyUrl, out var proxyError))
        {
            return BadRequest(ApiResponse.Fail($"代理地址格式不正确: {proxyError}", "invalid_proxy_url"));
        }

        if (!string.IsNullOrWhiteSpace(payload.Key))
        {
            var newKey = payload.Key.Trim();
            if (!string.Equals(newKey, profile.Key, StringComparison.OrdinalIgnoreCase))
            {
                var duplicate = await _dbContext.ProxyProfiles
                    .AnyAsync(x => x.Key == newKey && x.Id != id, cancellationToken);
                if (duplicate)
                {
                    return Conflict(ApiResponse.Fail($"标识 Key '{newKey}' 已存在", "duplicate_key"));
                }
                profile.Key = newKey;
            }
        }

        profile.Name = payload.Name.Trim();
        profile.ProxyUrl = proxyUrl;
        profile.Description = payload.Description?.Trim();
        profile.IsEnabled = payload.IsEnabled;
        profile.SortOrder = payload.SortOrder;
        profile.UpdatedAt = DateTimeOffset.UtcNow;

        await _dbContext.UpdateAsync(profile, cancellationToken);
        _metadataCache?.InvalidateRouteTargets();
        _metadataCache?.InvalidateModelMetadata();

        return Ok(ApiResponse.Ok("代理方案已更新"));
    }

    /// <summary>
    /// 删除出口代理方案。
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (!await IsProxyProfilesEnabledAsync(cancellationToken))
        {
            return NotFound();
        }


        var profile = await _dbContext.ProxyProfiles.InSingleAsync(id);
        if (profile is null)
        {
            return NotFound(ApiResponse.Fail("代理方案不存在", "proxy_not_found"));
        }

        await _dbContext.DeleteAsync(profile, cancellationToken);
        _metadataCache?.InvalidateRouteTargets();
        _metadataCache?.InvalidateModelMetadata();

        return Ok(ApiResponse.Ok("代理方案已删除"));
    }

    /// <summary>
    /// 测试代理连通性与延迟。
    /// </summary>
    [HttpPost("test")]
    public async Task<IActionResult> TestConnectivity([FromBody] TestProxyRequest request, CancellationToken cancellationToken)
    {
        if (!await IsProxyProfilesEnabledAsync(cancellationToken))
        {
            return NotFound();
        }


        if (string.IsNullOrWhiteSpace(request?.ProxyUrl))
        {
            return BadRequest(ApiResponse.Fail("代理地址不能为空", "invalid_proxy_url"));
        }

        var proxyUrl = request.ProxyUrl.Trim();
        if (!proxyUrl.Contains("://"))
        {
            var profile = await _dbContext.ProxyProfiles
                .FirstAsync(x => x.Key == proxyUrl, cancellationToken);
            if (profile != null && !string.IsNullOrWhiteSpace(profile.ProxyUrl))
            {
                proxyUrl = profile.ProxyUrl;
            }
            else
            {
                return BadRequest(ApiResponse.Fail($"未找到标识为 '{proxyUrl}' 的代理方案", "proxy_not_found"));
            }
        }
        else if (!EgressProxyValidator.TryValidate(proxyUrl, out var proxyError))
        {
            return BadRequest(ApiResponse.Fail($"代理地址格式不正确: {proxyError}", "invalid_proxy_url"));
        }

        var targetUrl = string.IsNullOrWhiteSpace(request.TargetUrl)
            ? "http://cp.cloudflare.com/generate_204"
            : request.TargetUrl.Trim();

        var sw = Stopwatch.StartNew();
        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy(new Uri(proxyUrl)),
                UseProxy = true,
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            using var httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            var response = await httpClient.GetAsync(targetUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();

            return Ok(ApiResponse.Ok(new
            {
                isSuccess = true,
                statusCode = (int)response.StatusCode,
                latencyMs = sw.ElapsedMilliseconds,
                targetUrl = targetUrl
            }, $"连接成功，延迟 {sw.ElapsedMilliseconds} ms"));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Ok(ApiResponse.Ok(new
            {
                isSuccess = false,
                statusCode = 0,
                latencyMs = sw.ElapsedMilliseconds,
                errorMessage = ex.InnerException?.Message ?? ex.Message,
                targetUrl = targetUrl
            }, $"连接失败: {ex.InnerException?.Message ?? ex.Message}"));
        }
    }
}

public sealed class ProxyProfilePayload
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ProxyUrl { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
}

public sealed class TestProxyRequest
{
    public string ProxyUrl { get; set; } = string.Empty;
    public string? TargetUrl { get; set; }
}
