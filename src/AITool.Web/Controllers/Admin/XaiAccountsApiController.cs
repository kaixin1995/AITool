using System.Text.Json;
using AITool.Application.Common;
using AITool.Application.Xai;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Xai;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Xai;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 请求模型：轮询/交换 Token。
/// </summary>
public sealed class XaiPollTokenRequest
{
    public string DeviceCode { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
}

/// <summary>
/// 请求模型：编辑 xAI 账号。
/// </summary>
public sealed class XaiUpdateAccountRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
}

/// <summary>
/// 请求模型：切换启用状态。
/// </summary>
public sealed class XaiToggleAccountRequest
{
    public bool IsEnabled { get; set; }
}

/// <summary>
/// 模型选择项 DTO。
/// </summary>
public sealed class XaiModelSelectionDto
{
    public string? RemoteModelName { get; set; }
    public string? DisplayName { get; set; }
    public bool? Selected { get; set; }
}

/// <summary>
/// 请求模型：导入选中的模型。
/// </summary>
public sealed class XaiImportModelsRequest
{
    public List<XaiModelSelectionDto>? Models { get; set; }
    public List<XaiModelSelectionDto>? Selections { get; set; }
}

/// <summary>
/// xAI (Grok) 账号管理 API 控制器。
/// 提供 RFC 8628 设备码授权登录、凭证导入（支持 ~/.grok/auth.json 形态）、模型拉取、
/// 启停、Token 手动刷新与额度刷新等功能。
/// </summary>
[Route("api/admin/xai-accounts")]
[ServiceFilter(typeof(OAuthFeatureToggleAttribute))]
public sealed class XaiAccountsApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IXaiOAuthClient _oauth;
    private readonly XaiAccountProvisioner _provisioner;
    private readonly IXaiModelFetcher _modelFetcher;
    private readonly XaiQuotaService _quotaService;
    private readonly ILogger<XaiAccountsApiController> _logger;

    public XaiAccountsApiController(
        AppDbContext dbContext,
        IXaiOAuthClient oauth,
        XaiAccountProvisioner provisioner,
        IXaiModelFetcher modelFetcher,
        XaiQuotaService quotaService,
        ILogger<XaiAccountsApiController> logger)
    {
        _dbContext = dbContext;
        _oauth = oauth;
        _provisioner = provisioner;
        _modelFetcher = modelFetcher;
        _quotaService = quotaService;
        _logger = logger;
    }

    /// <summary>
    /// 发起 xAI 设备授权流程，获取 User Code 与验证链接。
    /// </summary>
    [HttpPost("start-device-flow")]
    public async Task<IActionResult> StartDeviceFlow(CancellationToken ct)
    {
        try
        {
            var deviceCodeResp = await _oauth.StartDeviceFlowAsync(ct);
            return Ok(deviceCodeResp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start xAI device flow");
            return BadRequest(new { message = "发起 xAI 设备授权失败：" + ex.Message });
        }
    }

    /// <summary>
    /// 轮询或完成设备授权：换取 Token 并自动创建/更新 XaiAccount 与隐藏 Site。
    /// </summary>
    [HttpPost("poll-token")]
    public async Task<IActionResult> PollToken([FromBody] XaiPollTokenRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.DeviceCode))
        {
            return BadRequest(new { message = "DeviceCode 不能为空" });
        }

        try
        {
            var exchange = await _oauth.ExchangeDeviceCodeAsync(req.DeviceCode, ct);
            if (exchange.IsPending)
            {
                return Ok(new { status = "pending", message = "等待用户在浏览器中授权" });
            }
            if (exchange.IsSlowDown)
            {
                return Ok(new { status = "slow_down", message = "请求过于频繁，请放慢轮询节奏" });
            }
            if (!exchange.IsSuccess || exchange.TokenSet == null)
            {
                return Ok(new
                {
                    status = "error",
                    error = exchange.Error,
                    errorDescription = exchange.ErrorDescription ?? "授权失败或已过期"
                });
            }

            var tokens = exchange.TokenSet;
            var input = new XaiProvisionInput
            {
                DisplayName = !string.IsNullOrWhiteSpace(req.DisplayName) ? req.DisplayName : (tokens.Email ?? "Grok 账号"),
                Email = tokens.Email,
                ExternalUserId = tokens.ExternalUserId,
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                TokenType = tokens.TokenType,
                Scope = tokens.Scope,
                TokenExpiresAt = tokens.ExpiresAt
            };

            var account = await _provisioner.ProvisionFromTokensAsync(input, ct);
            return Ok(new
            {
                status = "success",
                account = ToSummary(account)
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while polling xAI token");
            return BadRequest(new { message = "Token 交换异常：" + ex.Message });
        }
    }

    /// <summary>
    /// 获取全部 xAI 账号列表。
    /// </summary>
    [HttpGet("accounts")]
    public async Task<IActionResult> ListAccounts(CancellationToken ct)
    {
        var accounts = await _dbContext.XaiAccounts
            .Where(a => !a.IsDeleted)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        return Ok(accounts.Select(ToSummary));
    }

    /// <summary>
    /// 切换账号启用/禁用状态。
    /// </summary>
    [HttpPost("accounts/{id:guid}/toggle")]
    public async Task<IActionResult> ToggleAccount(Guid id, [FromBody] XaiToggleAccountRequest? req, CancellationToken ct)
    {
        var isEnabled = req?.IsEnabled ?? true;
        await _provisioner.ToggleAsync(id, isEnabled, ct);
        return Ok(new { success = true });
    }

    /// <summary>
    /// 编辑账号展示名与（可选）refresh_token。
    /// </summary>
    [HttpPut("accounts/{id:guid}")]
    public async Task<IActionResult> UpdateAccount(Guid id, [FromBody] XaiUpdateAccountRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.DisplayName))
        {
            return BadRequest(new { message = "展示名不能为空" });
        }

        try
        {
            var account = await _provisioner.UpdateAsync(id, req.DisplayName, req.RefreshToken, ct);
            return Ok(ToSummary(account));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update xAI account {Id}", id);
            return BadRequest(new { message = "更新失败：" + ex.Message });
        }
    }

    /// <summary>
    /// 手动刷新账号 Token。
    /// </summary>
    [HttpPost("accounts/{id:guid}/refresh-token")]
    public async Task<IActionResult> RefreshToken(Guid id, CancellationToken ct)
    {
        var account = await _dbContext.XaiAccounts.InSingleAsync(id);
        if (account == null || account.IsDeleted)
        {
            return NotFound(new { message = "账号不存在" });
        }
        if (string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            return BadRequest(new { message = "账号没有 refresh_token" });
        }

        try
        {
            var tokens = await _oauth.RefreshTokenAsync(account.RefreshToken, ct);

            account.AccessToken = tokens.AccessToken;
            if (!string.IsNullOrWhiteSpace(tokens.RefreshToken)) account.RefreshToken = tokens.RefreshToken;
            account.Scope = tokens.Scope ?? account.Scope;
            account.TokenExpiresAt = tokens.ExpiresAt;
            account.LastRefreshAt = DateTimeOffset.UtcNow;
            account.RequiresReauth = false;
            account.UpdatedAt = DateTimeOffset.UtcNow;
            await _dbContext.Client.Updateable(account).ExecuteCommandAsync(ct);

            var site = await _dbContext.Sites.InSingleAsync(account.LinkedSiteId);
            if (site != null && !string.IsNullOrWhiteSpace(tokens.AccessToken))
            {
                site.ApiKey = tokens.AccessToken;
                await _dbContext.Client.Updateable(site).UpdateColumns(s => new { s.ApiKey }).ExecuteCommandAsync(ct);
            }

            return Ok(ToSummary(account));
        }
        catch (XaiRefreshTokenInvalidException ex)
        {
            account.RequiresReauth = true;
            account.UpdatedAt = DateTimeOffset.UtcNow;
            await _dbContext.Client.Updateable(account)
                .UpdateColumns(a => new { a.RequiresReauth, a.UpdatedAt })
                .ExecuteCommandAsync(ct);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to manually refresh xAI account {Id}", id);
            return BadRequest(new { message = "刷新 Token 失败：" + ex.Message });
        }
    }

    /// <summary>
    /// 手动刷新账号额度（实时查询 grok.com 账单端点并持久化）。
    /// </summary>
    [HttpPost("accounts/{id:guid}/refresh-quota")]
    public async Task<IActionResult> RefreshQuota(Guid id, CancellationToken ct)
    {
        var snapshot = await _quotaService.ForceRefreshAsync(id, ct);
        if (!snapshot.Success)
        {
            return BadRequest(new { message = snapshot.Error ?? "额度查询失败" });
        }

        return Ok(new
        {
            checkedAt = snapshot.CheckedAt.ToString("o"),
            planType = snapshot.PlanType,
            windows = snapshot.Windows.Select(w => new
            {
                id = w.Id,
                label = w.Label,
                usedPercent = w.UsedPercent,
                resetLabel = w.ResetLabel
            })
        });
    }

    /// <summary>
    /// 删除 xAI 账号及其关联隐藏 Site。
    /// </summary>
    [HttpDelete("accounts/{id:guid}")]
    public async Task<IActionResult> DeleteAccount(Guid id, CancellationToken ct)
    {
        await _provisioner.DeleteAsync(id, ct);
        return Ok(new { success = true });
    }

    /// <summary>
    /// 获取当前账号可用的模型清单并对比已有映射状态。
    /// </summary>
    [HttpGet("accounts/{id:guid}/fetch-models")]
    public async Task<IActionResult> FetchModels(Guid id, CancellationToken ct)
    {
        var account = await _dbContext.XaiAccounts.InSingleAsync(id);
        if (account == null || account.IsDeleted)
        {
            return NotFound(new { message = "账号不存在" });
        }

        var models = await _modelFetcher.FetchAsync(account.AccessToken ?? string.Empty, ct);
        var existingMappings = await _dbContext.SiteModelMappings
            .Where(m => m.SiteId == account.LinkedSiteId)
            .ToListAsync(ct);

        var existingDict = existingMappings.ToDictionary(m => m.RemoteModelName, StringComparer.OrdinalIgnoreCase);

        var items = models.Select(m =>
        {
            // xAI 公开名即上游 ID，映射直接按 ID 命中。
            SiteModelMapping? mapping = null;
            var hasExisting = existingDict.TryGetValue(m.Slug, out mapping!);
            return new
            {
                remoteModelName = m.Slug,
                displayName = m.DisplayName,
                existingMappingId = hasExisting ? mapping!.Id : (Guid?)null,
                isEnabled = hasExisting ? mapping!.IsEnabled : true,
                existingDisplayName = (string?)null
            };
        }).ToList();

        return Ok(items);
    }

    /// <summary>
    /// 保存/导入选中的模型映射。
    /// </summary>
    [HttpPost("accounts/{id:guid}/import-selected-models")]
    public async Task<IActionResult> ImportSelectedModels(Guid id, [FromBody] XaiImportModelsRequest req, CancellationToken ct)
    {
        var account = await _dbContext.XaiAccounts.InSingleAsync(id);
        if (account == null || account.IsDeleted)
        {
            return NotFound(new { message = "账号不存在" });
        }

        var rawList = req.Models ?? req.Selections ?? new();
        var selections = rawList
            .Where(m => !string.IsNullOrWhiteSpace(m.RemoteModelName))
            .Select(m => (
                Slug: m.RemoteModelName!.Trim(),
                DisplayName: string.IsNullOrWhiteSpace(m.DisplayName) ? m.RemoteModelName.Trim() : m.DisplayName!.Trim(),
                Selected: m.Selected ?? true))
            .GroupBy(m => m.Slug, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();

        if (selections.Count == 0)
        {
            return BadRequest(new { message = "未收到模型清单" });
        }

        await _provisioner.SyncRemoteModelsAsync(account.LinkedSiteId, selections, ct);
        return Ok(new { success = true });
    }

    /// <summary>
    /// 导入凭证文件：支持平铺 JSON（access_token/refresh_token）或 Grok CLI 的
    /// ~/.grok/auth.json（scope → { key, refresh_token, expires_at }，优先 OIDC 条目）。
    /// </summary>
    [HttpPost("import-credential")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> ImportCredential([FromQuery] string? name, CancellationToken ct)
    {
        var parseResults = new List<(string FileName, string Json)>();

        if (Request.HasFormContentType && Request.Form.Files.Count > 0)
        {
            foreach (var file in Request.Form.Files)
            {
                using var sr = new StreamReader(file.OpenReadStream());
                parseResults.Add((file.FileName, await sr.ReadToEndAsync(ct)));
            }
        }
        else
        {
            using var sr = new StreamReader(Request.Body);
            parseResults.Add((name ?? "grok_credential.json", await sr.ReadToEndAsync(ct)));
        }

        var successes = new List<object>();
        var failures = new List<object>();

        foreach (var (fileName, json) in parseResults)
        {
            try
            {
                var (accessToken, refreshToken, expiresAt, email, displayName) = ParseCredentialJson(json);
                if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(refreshToken))
                {
                    failures.Add(new { fileName, error = "凭证 JSON 缺少 access_token 或 refresh_token" });
                    continue;
                }

                var resolvedDisplayName = !string.IsNullOrWhiteSpace(displayName)
                    ? displayName
                    : Path.GetFileNameWithoutExtension(fileName);

                var input = new XaiProvisionInput
                {
                    DisplayName = resolvedDisplayName,
                    Email = email,
                    AccessToken = accessToken ?? string.Empty,
                    RefreshToken = refreshToken,
                    TokenType = "bearer",
                    TokenExpiresAt = expiresAt
                    // TokenExpiresAt 留空时由后台 XaiTokenRefreshService 首轮扫描刷新一次获取真实过期时间。
                };

                // 若只有 refresh_token，则先刷新一次获取 access_token。
                if (string.IsNullOrWhiteSpace(input.AccessToken) && !string.IsNullOrWhiteSpace(input.RefreshToken))
                {
                    var tokens = await _oauth.RefreshTokenAsync(input.RefreshToken, ct);
                    input.AccessToken = tokens.AccessToken;
                    if (!string.IsNullOrWhiteSpace(tokens.RefreshToken)) input.RefreshToken = tokens.RefreshToken;
                    input.TokenExpiresAt = tokens.ExpiresAt;
                    input.ExternalUserId = tokens.ExternalUserId;
                    input.Email = tokens.Email ?? input.Email;
                }

                var account = await _provisioner.ProvisionFromTokensAsync(input, ct);
                successes.Add(ToSummary(account));
            }
            catch (Exception ex)
            {
                failures.Add(new { fileName, error = ex.Message });
            }
        }

        return Ok(new { successes, failures });
    }

    /// <summary>
    /// 解析凭证 JSON：优先 Grok CLI auth.json 形态（对象值为 scope 条目，key=access token，
    /// 优先 https://auth.x.ai:: 前缀的 OIDC 条目），回退平铺字段。
    /// </summary>
    private static (string? AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, string? Email, string? DisplayName) ParseCredentialJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? accessToken = null;
        string? refreshToken = null;
        DateTimeOffset? expiresAt = null;
        string? email = null;
        string? displayName = null;

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var entry = property.Value;
                var key = entry.TryGetProperty("key", out var keyProp) ? keyProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var isOidc = property.Name.StartsWith("https://auth.x.ai::", StringComparison.OrdinalIgnoreCase);
                var isLegacy = property.Name.Contains("/sign-in", StringComparison.OrdinalIgnoreCase);
                if (!isOidc && !isLegacy)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(accessToken))
                {
                    accessToken = key;
                }
                if (entry.TryGetProperty("refresh_token", out var rt) && !string.IsNullOrWhiteSpace(rt.GetString()))
                {
                    refreshToken = rt.GetString();
                }
                if (entry.TryGetProperty("expires_at", out var exp) && exp.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(exp.GetString(), out var parsedExp))
                {
                    expiresAt = parsedExp;
                }

                // OIDC 条目优先：命中即不再看 legacy。
                if (isOidc)
                {
                    break;
                }
            }

            if (root.TryGetProperty("email", out var em)) email = em.GetString();
            if (root.TryGetProperty("display_name", out var dn)) displayName = dn.GetString();
            if (root.TryGetProperty("access_token", out var at) && string.IsNullOrWhiteSpace(accessToken)) accessToken = at.GetString();
            if (root.TryGetProperty("refresh_token", out var rt2) && string.IsNullOrWhiteSpace(refreshToken)) refreshToken = rt2.GetString();
        }

        return (accessToken, refreshToken, expiresAt, email, displayName);
    }

    private object ToSummary(XaiAccount a)
    {
        // 从最近一次额度查询的 Base64 缓存恢复窗口，供前端卡片直接渲染。
        var windows = RestoreWindows(a.LastQuotaRawJson);

        return new
        {
            id = a.Id,
            displayName = a.DisplayName,
            email = a.Email,
            userId = a.ExternalUserId,
            planType = "SuperGrok",
            isEnabled = a.IsEnabled,
            requiresReauth = a.RequiresReauth,
            isQuotaCooling = false,
            quotaCoolingUntil = (string?)null,
            lastQuotaCheckedAt = a.LastQuotaCheckedAt?.ToString("o"),
            windows = RestoreWindows(a.LastQuotaRawJson),
            tokenExpiresAt = a.TokenExpiresAt?.ToString("o"),
            createdAt = a.CreatedAt.ToString("o"),
            linkedSiteId = a.LinkedSiteId
        };
    }

    private IEnumerable<object> RestoreWindows(string? lastQuotaRawJson)
    {
        if (string.IsNullOrWhiteSpace(lastQuotaRawJson))
        {
            return [];
        }

        try
        {
            var snapshot = GrokQuotaParser.Parse(Convert.FromBase64String(lastQuotaRawJson), DateTimeOffset.UtcNow);
            var resetAt = snapshot.ResetsAtSeconds is { } seconds
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : (DateTimeOffset?)null;
            return
            [
                new
                {
                    id = "grok_credits",
                    label = GrokQuotaParser.DescribeWindow(snapshot.ResetsAtSeconds, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                    usedPercent = snapshot.UsedPercent,
                    resetLabel = resetAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "N/A"
                }
            ];
        }
        catch (Exception)
        {
            return [];
        }
    }
}
