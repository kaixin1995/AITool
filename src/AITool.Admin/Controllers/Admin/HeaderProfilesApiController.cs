using System.Text.Json;
using AITool.Application.Common;
using AITool.Application.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Proxy;
using AITool.Application.Common;
using AITool.Admin.Services;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// 请求头模板与客户端特征方案管理控制器（保存在本地 client-header-profiles.json，脱离数据库存储）。
/// </summary>
[ApiController]
[Route("api/admin/developer/header-profiles")]
public class HeaderProfilesApiController : ControllerBase
{
    private readonly IHeaderProfileCatalogService _catalogService;
    private readonly ProxyRequestMetadataCache? _metadataCache;

    public HeaderProfilesApiController(
        IHeaderProfileCatalogService catalogService,
        ProxyRequestMetadataCache? metadataCache = null)
    {
        _catalogService = catalogService;
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 获取全部请求头模板方案列表（含系统内置与自定义）。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var profiles = await _catalogService.GetAllAsync(cancellationToken);

        return Ok(profiles.Select(p => new
        {
            id = p.Id,
            key = p.Key,
            name = p.Name,
            description = p.Description,
            headersJson = p.HeadersJson,
            isBuiltIn = p.IsBuiltIn,
            isEnabled = p.IsEnabled,
            sortOrder = p.SortOrder,
            createdAt = p.CreatedAt,
            updatedAt = p.UpdatedAt
        }));
    }

    /// <summary>
    /// 获取单个方案详情。
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var profile = await _catalogService.GetByIdAsync(id, cancellationToken);
        if (profile is null)
        {
            return NotFound(ApiResponse.Fail("请求头方案不存在", "profile_not_found"));
        }

        return Ok(ApiResponse.Ok(new
        {
            id = profile.Id,
            key = profile.Key,
            name = profile.Name,
            description = profile.Description,
            headersJson = profile.HeadersJson,
            isBuiltIn = profile.IsBuiltIn,
            isEnabled = profile.IsEnabled,
            sortOrder = profile.SortOrder,
            createdAt = profile.CreatedAt,
            updatedAt = profile.UpdatedAt
        }));
    }

    /// <summary>
    /// 创建自定义请求头方案。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] HeaderProfilePayload payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload?.Key) || string.IsNullOrWhiteSpace(payload.Name))
        {
            return BadRequest(ApiResponse.Fail("方案标识 Key 和名称不能为空", "invalid_input"));
        }

        var key = payload.Key.Trim();
        var existing = await _catalogService.GetByKeyAsync(key, cancellationToken);
        if (existing != null)
        {
            return Conflict(ApiResponse.Fail($"标识 Key '{key}' 已存在，请更换", "duplicate_key"));
        }

        if (!ValidateHeadersJson(payload.HeadersJson, out var jsonError))
        {
            return BadRequest(ApiResponse.Fail($"Headers JSON 格式错误: {jsonError}", "invalid_headers_json"));
        }

        var profile = new HeaderProfile
        {
            Key = key,
            Name = payload.Name.Trim(),
            Description = payload.Description?.Trim(),
            HeadersJson = string.IsNullOrWhiteSpace(payload.HeadersJson) ? null : payload.HeadersJson.Trim(),
            IsBuiltIn = false,
            IsEnabled = payload.IsEnabled,
            SortOrder = payload.SortOrder,
            CreatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            var created = await _catalogService.CreateAsync(profile, cancellationToken);
            _metadataCache?.InvalidateRouteTargets();
            _metadataCache?.InvalidateModelMetadata();

            return Ok(ApiResponse.Ok(new { id = created.Id, key = created.Key }, "请求头方案已创建"));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ApiResponse.Fail(ex.Message, "duplicate_key"));
        }
    }

    /// <summary>
    /// 更新请求头方案。
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] HeaderProfilePayload payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload?.Name))
        {
            return BadRequest(ApiResponse.Fail("方案名称不能为空", "invalid_input"));
        }

        var profile = await _catalogService.GetByIdAsync(id, cancellationToken);
        if (profile is null)
        {
            return NotFound(ApiResponse.Fail("请求头方案不存在", "profile_not_found"));
        }

        if (!ValidateHeadersJson(payload.HeadersJson, out var jsonError))
        {
            return BadRequest(ApiResponse.Fail($"Headers JSON 格式错误: {jsonError}", "invalid_headers_json"));
        }

        // 内置方案不允许修改 Key，自定义方案允许修改 Key（需预检重名）
        string? newKey = null;
        if (!profile.IsBuiltIn && !string.IsNullOrWhiteSpace(payload.Key))
        {
            var candidateKey = payload.Key.Trim();
            if (!string.Equals(candidateKey, profile.Key, StringComparison.OrdinalIgnoreCase))
            {
                var duplicate = await _catalogService.GetByKeyAsync(candidateKey, cancellationToken);
                if (duplicate != null && duplicate.Id != id)
                {
                    return Conflict(ApiResponse.Fail($"标识 Key '{candidateKey}' 已存在", "duplicate_key"));
                }
                newKey = candidateKey;
            }
        }

        await _catalogService.UpdateAsync(id, target =>
        {
            if (newKey != null) target.Key = newKey;
            target.Name = payload.Name.Trim();
            target.Description = payload.Description?.Trim();
            target.HeadersJson = string.IsNullOrWhiteSpace(payload.HeadersJson) ? null : payload.HeadersJson.Trim();
            target.IsEnabled = payload.IsEnabled;
            target.SortOrder = payload.SortOrder;
        }, cancellationToken);

        _metadataCache?.InvalidateRouteTargets();
        _metadataCache?.InvalidateModelMetadata();

        return Ok(ApiResponse.Ok("请求头方案已更新"));
    }

    /// <summary>
    /// 删除自定义请求头方案（内置方案禁止删除）。
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var profile = await _catalogService.GetByIdAsync(id, cancellationToken);
        if (profile is null)
        {
            return NotFound(ApiResponse.Fail("请求头方案不存在", "profile_not_found"));
        }

        if (profile.IsBuiltIn)
        {
            return BadRequest(ApiResponse.Fail("系统内置预设方案禁止删除，您可以禁用或克隆它", "builtin_cannot_delete"));
        }

        try
        {
            await _catalogService.DeleteAsync(id, cancellationToken);
            _metadataCache?.InvalidateRouteTargets();
            _metadataCache?.InvalidateModelMetadata();
            return Ok(ApiResponse.Ok("请求头方案已删除"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message, "builtin_cannot_delete"));
        }
    }

    /// <summary>
    /// 重置系统内置预设方案为官方最新默认值。
    /// </summary>
    [HttpPost("reset-builtins")]
    public async Task<IActionResult> ResetBuiltIns(CancellationToken cancellationToken)
    {
        var profiles = await _catalogService.ResetBuiltInsAsync(cancellationToken);
        _metadataCache?.InvalidateRouteTargets();
        _metadataCache?.InvalidateModelMetadata();
        return Ok(ApiResponse.Ok(profiles, "系统内置预设已重置为官方默认值"));
    }

    /// <summary>
    /// 实时求值与测试请求头模板（替换动态变量）。
    /// </summary>
    [HttpPost("preview")]
    public IActionResult Preview([FromBody] PreviewHeadersRequest request)
    {
        Dictionary<string, string> inputHeaders = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(request.HeadersJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(request.HeadersJson);
                if (parsed != null)
                {
                    inputHeaders = new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                return BadRequest(ApiResponse.Fail($"Headers JSON 解析失败: {ex.Message}", "json_parse_error"));
            }
        }

        var resolved = ClientEmulationEngine.ResolveHeaders(
            request.EmulationPreset,
            inputHeaders,
            request.ModelName,
            request.ProjectId,
            request.IsAntigravity);

        return Ok(ApiResponse.Ok(new
        {
            previewHeaders = resolved,
            evaluatedCount = resolved.Count
        }));
    }

    private static bool ValidateHeadersJson(string? json, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            errorMessage = null;
            return true;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed == null)
            {
                errorMessage = "必须是一个 JSON Object（如 {\"key\": \"value\"}）";
                return false;
            }
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}

public sealed class HeaderProfilePayload
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? HeadersJson { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
}

public sealed class PreviewHeadersRequest
{
    public string? EmulationPreset { get; set; }
    public string? HeadersJson { get; set; }
    public string? ModelName { get; set; }
    public string? ProjectId { get; set; }
    public bool IsAntigravity { get; set; }
}
