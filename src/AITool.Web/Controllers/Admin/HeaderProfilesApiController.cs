using System.Text.Json;
using AITool.Application.Common;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Web.Contracts;
using AITool.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SqlSugar;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 请求头模板与客户端特征方案管理控制器（供调试工具及全局下拉选择使用）。
/// </summary>
[ApiController]
[Route("api/admin/developer/header-profiles")]
public class HeaderProfilesApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache? _metadataCache;

    public HeaderProfilesApiController(AppDbContext dbContext, ProxyRequestMetadataCache? metadataCache = null)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 获取全部请求头模板方案列表（含系统内置与自定义）。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        await EnsureBuiltInProfilesAsync(cancellationToken);

        var profiles = await _dbContext.HeaderProfiles
            .OrderByDescending(x => x.IsBuiltIn)
            .ThenBy(x => x.SortOrder)
            .ThenBy(x => x.Key)
            .ToListAsync(cancellationToken);

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
        var profile = await _dbContext.HeaderProfiles.InSingleAsync(id);
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
        var exists = await _dbContext.HeaderProfiles
            .AnyAsync(x => x.Key == key, cancellationToken);
        if (exists)
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

        await _dbContext.InsertAsync(profile, cancellationToken);
        _metadataCache?.InvalidateRouteTargets();
        _metadataCache?.InvalidateModelMetadata();

        return Ok(ApiResponse.Ok(new { id = profile.Id, key = profile.Key }, "请求头方案已创建"));
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

        var profile = await _dbContext.HeaderProfiles.InSingleAsync(id);
        if (profile is null)
        {
            return NotFound(ApiResponse.Fail("请求头方案不存在", "profile_not_found"));
        }

        if (!ValidateHeadersJson(payload.HeadersJson, out var jsonError))
        {
            return BadRequest(ApiResponse.Fail($"Headers JSON 格式错误: {jsonError}", "invalid_headers_json"));
        }

        // 内置方案不允许修改 Key，自定义方案允许修改 Key（需预检重名）
        if (!profile.IsBuiltIn && !string.IsNullOrWhiteSpace(payload.Key))
        {
            var newKey = payload.Key.Trim();
            if (!string.Equals(newKey, profile.Key, StringComparison.OrdinalIgnoreCase))
            {
                var duplicate = await _dbContext.HeaderProfiles
                    .AnyAsync(x => x.Key == newKey && x.Id != id, cancellationToken);
                if (duplicate)
                {
                    return Conflict(ApiResponse.Fail($"标识 Key '{newKey}' 已存在", "duplicate_key"));
                }
                profile.Key = newKey;
            }
        }

        profile.Name = payload.Name.Trim();
        profile.Description = payload.Description?.Trim();
        profile.HeadersJson = string.IsNullOrWhiteSpace(payload.HeadersJson) ? null : payload.HeadersJson.Trim();
        profile.IsEnabled = payload.IsEnabled;
        profile.SortOrder = payload.SortOrder;
        profile.UpdatedAt = DateTimeOffset.UtcNow;

        await _dbContext.UpdateAsync(profile, cancellationToken);
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
        var profile = await _dbContext.HeaderProfiles.InSingleAsync(id);
        if (profile is null)
        {
            return NotFound(ApiResponse.Fail("请求头方案不存在", "profile_not_found"));
        }

        if (profile.IsBuiltIn)
        {
            return BadRequest(ApiResponse.Fail("系统内置预设方案禁止删除，您可以禁用或克隆它", "builtin_cannot_delete"));
        }

        await _dbContext.DeleteAsync(profile, cancellationToken);
        _metadataCache?.InvalidateRouteTargets();
        _metadataCache?.InvalidateModelMetadata();
        return Ok(ApiResponse.Ok("请求头方案已删除"));
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

    private async Task EnsureBuiltInProfilesAsync(CancellationToken ct)
    {
        var existingKeys = (await _dbContext.HeaderProfiles
            .Select(x => x.Key)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var builtIns = GetBuiltInProfiles();
        var toInsert = builtIns.Where(p => !existingKeys.Contains(p.Key)).ToList();
        if (toInsert.Count > 0)
        {
            await _dbContext.InsertRangeAsync(toInsert, ct);
        }
    }

    public static List<HeaderProfile> GetBuiltInProfiles()
    {
        return
        [
            new HeaderProfile
            {
                Key = ClientEmulationConstants.OpenCode,
                Name = "OpenCode CLI 终端",
                Description = "模拟 OpenCode 官方命令行终端特征（支持免密访问 OpenCode Zen 免费模型等上游识别）",
                HeadersJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["User-Agent"] = "opencode/1.15.0 ai-sdk/provider-utils/4.0.23 runtime/bun/1.3.13",
                    ["x-opencode-client"] = "cli",
                    ["x-opencode-project"] = "global",
                    ["x-opencode-request"] = "msg_${nanoid:12}",
                    ["x-opencode-session"] = "ses_${nanoid:12}"
                }, new JsonSerializerOptions { WriteIndented = true }),
                IsBuiltIn = true,
                IsEnabled = true,
                SortOrder = 1
            },
            new HeaderProfile
            {
                Key = ClientEmulationConstants.ClaudeCode,
                Name = "Claude Code 官方命令行",
                Description = "模拟 Anthropic 官方 Claude Code 终端命令行特征（防封控与免审查优化）",
                HeadersJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["User-Agent"] = "claude-code/0.2.29 (external; x86_64-pc-windows-msvc)",
                    ["anthropic-client-name"] = "claude-code",
                    ["anthropic-client-version"] = "0.2.29",
                    ["anthropic-beta"] = "prompt-caching-2024-07-31,computer-use-2024-10-22"
                }, new JsonSerializerOptions { WriteIndented = true }),
                IsBuiltIn = true,
                IsEnabled = true,
                SortOrder = 2
            },
            new HeaderProfile
            {
                Key = ClientEmulationConstants.CodexCli,
                Name = "GitHub Copilot / Codex",
                Description = "模拟 VS Code GitHub Copilot 与 OpenAI Codex 客户端特征",
                HeadersJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["User-Agent"] = "GitHubCopilotChat/0.24.1 VSCode/1.96.2",
                    ["Editor-Version"] = "vscode/1.96.2",
                    ["Editor-Plugin-Version"] = "copilot-chat/0.24.1",
                    ["Openai-Organization"] = "github-copilot",
                    ["X-Request-Id"] = "${guid}",
                    ["Session-Id"] = "${guid}"
                }, new JsonSerializerOptions { WriteIndented = true }),
                IsBuiltIn = true,
                IsEnabled = true,
                SortOrder = 3
            },
            new HeaderProfile
            {
                Key = ClientEmulationConstants.Antigravity,
                Name = "Google Antigravity CLI",
                Description = "模拟 Google Antigravity 官方客户端特征（自动注入动态 requestId 与 gl-node 指纹）",
                HeadersJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["User-Agent"] = "antigravity/1.10.4 linux/x86_64",
                    ["x-goog-api-client"] = "gl-node/20.18.0 antigravity-cli/1.10.4",
                    ["requestId"] = "req-${guid:N}",
                    ["requestType"] = "agent"
                }, new JsonSerializerOptions { WriteIndented = true }),
                IsBuiltIn = true,
                IsEnabled = true,
                SortOrder = 4
            },
            new HeaderProfile
            {
                Key = ClientEmulationConstants.GeminiCli,
                Name = "Google Gemini CLI",
                Description = "模拟 Google Gemini CLI 官方工具（支持自动注入动态模型名与 project-id）",
                HeadersJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["User-Agent"] = "GeminiCLI/0.35.2/${model} (win32; x64; cloud-shell)"
                }, new JsonSerializerOptions { WriteIndented = true }),
                IsBuiltIn = true,
                IsEnabled = true,
                SortOrder = 5
            }
        ];
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
