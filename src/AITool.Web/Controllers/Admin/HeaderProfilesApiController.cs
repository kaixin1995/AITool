using System.Text.Json;
using AITool.Application.Common;
using AITool.Application.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Proxy;
using AITool.Web.Contracts;
using Microsoft.Extensions.Logging;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 请求头模板与客户端特征方案管理控制器（保存在本地 client-header-profiles.json，脱离数据库存储）。
/// </summary>
[ApiController]
[Route("api/admin/developer/header-profiles")]
public class HeaderProfilesApiController : ControllerBase
{
    private readonly IHeaderProfileCatalogService _catalogService;
    private readonly ProxyRequestMetadataCache? _metadataCache;
    private readonly AiAssistantService? _aiAssistant;
    private readonly ClientReleaseFeedService? _releaseFeed;
    private readonly ILogger<HeaderProfilesApiController>? _logger;

    public HeaderProfilesApiController(
        IHeaderProfileCatalogService catalogService,
        ProxyRequestMetadataCache? metadataCache = null,
        AiAssistantService? aiAssistant = null,
        ClientReleaseFeedService? releaseFeed = null,
        ILogger<HeaderProfilesApiController>? logger = null)
    {
        _catalogService = catalogService;
        _metadataCache = metadataCache;
        _aiAssistant = aiAssistant;
        _releaseFeed = releaseFeed;
        _logger = logger;
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

    /// <summary>
    /// AI 查询该方案客户端的最新版本。只做查询与对比，不写库——
    /// 返回当前/最新版本及替换版本号后的 HeadersJson，前端展示对比后由用户确认，
    /// 确认后走既有 PUT 更新。
    /// </summary>
    [HttpPost("{id:guid}/ai-latest-version")]
    public async Task<IActionResult> AiLatestVersion(Guid id, CancellationToken cancellationToken)
    {
        var profile = await _catalogService.GetByIdAsync(id, cancellationToken);
        if (profile is null)
        {
            return NotFound(ApiResponse.Fail("请求头方案不存在", "profile_not_found"));
        }

        if (_aiAssistant is null)
        {
            return Ok(ApiResponse.Ok(new { success = false, error = "AI 助手服务不可用" }));
        }

        Dictionary<string, string> headers;
        if (string.IsNullOrWhiteSpace(profile.HeadersJson))
        {
            return Ok(ApiResponse.Ok(new { success = false, error = "该方案没有配置任何请求头，无法识别版本" }));
        }
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(profile.HeadersJson);
            if (parsed is null || parsed.Count == 0)
            {
                return Ok(ApiResponse.Ok(new { success = false, error = "该方案没有配置任何请求头，无法识别版本" }));
            }
            headers = new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            return Ok(ApiResponse.Ok(new { success = false, error = $"Headers JSON 解析失败: {ex.Message}" }));
        }

        if (!headers.TryGetValue("User-Agent", out var userAgent) || string.IsNullOrWhiteSpace(userAgent))
        {
            return Ok(ApiResponse.Ok(new { success = false, error = "该方案未配置 User-Agent 请求头，无法识别版本" }));
        }

        var currentVersion = ClientVersionText.ExtractVersion(userAgent);
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            return Ok(ApiResponse.Ok(new { success = false, error = $"无法从 User-Agent 中识别版本号：{userAgent}" }));
        }

        // 先拉官方发布源（GitHub Releases / npm）拿确定性数据，AI 只负责归纳；
        // 没有公开源的档案（如 ZCode / Antigravity）回落 AI 自身知识。
        var feed = _releaseFeed is null
            ? new ClientReleaseFeedService.ReleaseFeedResult(false, string.Empty, string.Empty)
            : await _releaseFeed.LookupAsync(profile.Key, cancellationToken);
        string prompt;
        if (feed.Success)
        {
            prompt = $@"下面是客户端「{profile.Name}」（标识：{profile.Key}）的官方发布数据，来自 {feed.SourceLabels}：

{feed.Facts}

当前 User-Agent：{userAgent}
当前使用的版本：{currentVersion}

请只根据上面的发布数据归纳：
1. version 填数据中的最新「正式版」版本号（去掉 v / rust-v 等 tag 前缀，只留版本号本身；数据里没有比当前版本更新的正式版时，填当前版本号）。
2. 数据中没有比当前版本更新的正式版（只有预发布或无更新）时，视为已是最新，known 填 true。
3. note 用一句话说明依据（来源与版本发布日期）。数据缺失或互相矛盾导致无法判断时 known 填 false。
4. 只输出一个 JSON 对象：{{""version"":""x.y.z"",""known"":true,""note"":""一句话说明""}}。不要输出其他任何文字或代码块围栏。";
        }
        else
        {
            prompt = $@"请告诉我以下软件客户端当前官方最新发布的稳定版本号。

客户端名称：{profile.Name}（标识：{profile.Key}）
当前 User-Agent：{userAgent}
当前使用的版本：{currentVersion}

要求：
1. version 只填版本号本身（例如 1.2.3，不要 v 前缀，不要整段 User-Agent）。
2. 如果你确定该客户端的最新稳定版本，known 填 true；不确定或不知道该客户端时 known 填 false，此时 version 可留空。
3. 只输出一个 JSON 对象，格式：{{""version"":""x.y.z"",""known"":true,""note"":""一句话说明""}}。不要输出其他任何文字或代码块围栏。";
        }

        var aiResult = await _aiAssistant.CompleteAsync(prompt, cancellationToken);
        if (!aiResult.Success)
        {
            return Ok(ApiResponse.Ok(new { success = false, error = aiResult.Error }));
        }

        string? latestVersion = null;
        string? note = null;
        var known = false;
        try
        {
            var json = AiAssistantService.ExtractJsonBlock(aiResult.Content!);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // known 兼容布尔与字符串两种形态（模型常把 true 输出成 "true"）。
            if (root.TryGetProperty("known", out var knownEl))
            {
                known = knownEl.ValueKind == JsonValueKind.True
                    || (knownEl.ValueKind == JsonValueKind.String
                        && bool.TryParse(knownEl.GetString(), out var knownBool) && knownBool);
            }
            if (root.TryGetProperty("version", out var versionEl)
                && (versionEl.ValueKind == JsonValueKind.String || versionEl.ValueKind == JsonValueKind.Number))
            {
                latestVersion = versionEl.ValueKind == JsonValueKind.String
                    ? versionEl.GetString()?.Trim()
                    : versionEl.GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (root.TryGetProperty("note", out var noteEl) && noteEl.ValueKind == JsonValueKind.String)
            {
                note = noteEl.GetString();
            }
        }
        catch
        {
            return Ok(ApiResponse.Ok(new { success = false, error = "AI 返回内容无法解析为版本信息，请重试或手动更新" }));
        }

        var sourceNote = feed.Success ? $"数据来源：{feed.SourceLabels}" : null;

        // 是否采信 latestVersion：发布数据在手时做确定性校验（版本号须出现在事实清单或等于当前版本），
        // 不依赖模型正确设置 known 标志——实测模型常把 known 误填为 false；
        // 无发布源的纯 AI 模式仍以 known 为准（没有事实可校验，只能靠模型自报置信度）。
        var useVersion = false;
        if (!string.IsNullOrWhiteSpace(latestVersion))
        {
            latestVersion = latestVersion.TrimStart('v', 'V');
            if (feed.Success)
            {
                useVersion = string.Equals(latestVersion, currentVersion, StringComparison.OrdinalIgnoreCase)
                             || feed.Facts.Contains(latestVersion, StringComparison.Ordinal);
            }
            else
            {
                useVersion = known;
            }
        }

        if (!useVersion || string.IsNullOrWhiteSpace(latestVersion))
        {
            // 归纳结果缺少可用的版本号：记录 AI 原始输出，便于定位新的输出形态。
            if (_logger is not null)
            {
                _logger.LogWarning(
                    "AI latest-version parse yielded no usable version (profile {Key}): raw={Raw}",
                    profile.Key, aiResult.Content is null ? string.Empty : aiResult.Content.Length <= 500 ? aiResult.Content : aiResult.Content[..500]);
            }
            return Ok(ApiResponse.Ok(new
            {
                success = true,
                upToDate = true,
                changed = false,
                currentVersion,
                latestVersion = (string?)null,
                note = note ?? "AI 表示无法确定该客户端的最新版本，请手动核实",
                sourceNote,
                currentHeadersJson = profile.HeadersJson,
                proposedHeadersJson = profile.HeadersJson
            }));
        }

        var comparison = ClientVersionText.CompareVersions(latestVersion, currentVersion);
        var upToDate = comparison <= 0;
        string proposedHeadersJson;
        if (upToDate)
        {
            proposedHeadersJson = profile.HeadersJson!;
        }
        else
        {
            var proposed = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
            foreach (var key in proposed.Keys.ToList())
            {
                if (string.Equals(key, "User-Agent", StringComparison.OrdinalIgnoreCase))
                {
                    // UA 里只替换第一个版本段（产品版本），后面的依赖库版本保持原样。
                    proposed[key] = ClientVersionText.ReplaceVersion(userAgent, latestVersion);
                }
                else if (string.Equals(proposed[key]?.Trim(), currentVersion, StringComparison.OrdinalIgnoreCase))
                {
                    // 其他头的值整体就是当前版本号（如 x-zcode-app-version / X-Msh-Version）→ 同步替换，
                    // 避免更新后 UA 与版本头不一致造成指纹矛盾。
                    proposed[key] = latestVersion;
                }
            }
            proposedHeadersJson = JsonSerializer.Serialize(proposed);
        }

        return Ok(ApiResponse.Ok(new
        {
            success = true,
            upToDate,
            changed = !upToDate,
            currentVersion,
            latestVersion,
            note,
            sourceNote,
            currentHeadersJson = profile.HeadersJson,
            proposedHeadersJson
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
