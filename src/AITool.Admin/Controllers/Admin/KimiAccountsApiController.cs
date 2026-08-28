using System.Text.Json;
using AITool.Application.Common;
using AITool.Application.Kimi;
using AITool.Domain.Kimi;
using AITool.Domain.Models;
using AITool.Domain.Sites;
using AITool.Infrastructure.Kimi;
using AITool.Infrastructure.Persistence;
using AITool.Admin.Services;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// 请求模型：发起设备授权。
/// </summary>
public sealed class KimiStartDeviceFlowRequest
{
    public string? DeviceId { get; set; }
}

/// <summary>
/// 请求模型：轮询/交换 Token。
/// </summary>
public sealed class KimiPollTokenRequest
{
    public string DeviceCode { get; set; } = string.Empty;
    public string? DeviceId { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>
/// 请求模型：编辑 Kimi 账号。
/// </summary>
public sealed class KimiUpdateAccountRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
}

/// <summary>
/// 请求模型：切换启用状态。
/// </summary>
public sealed class KimiToggleAccountRequest
{
    public bool IsEnabled { get; set; }
}

/// <summary>
/// 模型选择项 DTO。
/// </summary>
public sealed class KimiModelSelectionDto
{
    public string? RemoteModelName { get; set; }
    public string? DisplayName { get; set; }
    public bool? Selected { get; set; }
}

/// <summary>
/// 请求模型：导入选中的模型。
/// </summary>
public sealed class KimiImportModelsRequest
{
    public List<KimiModelSelectionDto>? Models { get; set; }
    public List<KimiModelSelectionDto>? Selections { get; set; }
}

/// <summary>
/// Kimi 账号管理 API 控制器。
/// 提供 RFC 8628 设备码授权登录、凭证导入/导出、模型拉取、启停、Token 手动刷新等功能。
/// </summary>
[Route("api/admin/kimi-accounts")]
[ServiceFilter(typeof(OAuthFeatureToggleAttribute))]
public sealed class KimiAccountsApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IKimiOAuthClient _oauth;
    private readonly KimiAccountProvisioner _provisioner;
    private readonly IKimiModelFetcher _modelFetcher;
    private readonly KimiCredentialRefreshService _credentialRefreshService;
    private readonly KimiQuotaService _quotaService;
    private readonly ILogger<KimiAccountsApiController> _logger;

    public KimiAccountsApiController(
        AppDbContext dbContext,
        IKimiOAuthClient oauth,
        KimiAccountProvisioner provisioner,
        IKimiModelFetcher modelFetcher,
        KimiCredentialRefreshService credentialRefreshService,
        KimiQuotaService quotaService,
        ILogger<KimiAccountsApiController> logger)
    {
        _dbContext = dbContext;
        _oauth = oauth;
        _provisioner = provisioner;
        _modelFetcher = modelFetcher;
        _credentialRefreshService = credentialRefreshService;
        _quotaService = quotaService;
        _logger = logger;
    }

    /// <summary>
    /// 发起 Kimi 设备授权流程，获取 User Code 与验证链接。
    /// </summary>
    [HttpPost("start-device-flow")]
    public async Task<IActionResult> StartDeviceFlow([FromBody] KimiStartDeviceFlowRequest? req, CancellationToken ct)
    {
        try
        {
            var deviceCodeResp = await _oauth.StartDeviceFlowAsync(req?.DeviceId, ct);
            return Ok(deviceCodeResp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start Kimi device flow");
            return BadRequest(new { message = "发起 Kimi 设备授权失败：" + ex.Message });
        }
    }

    /// <summary>
    /// 轮询或完成设备授权：换取 Token 并自动创建/更新 KimiAccount 与隐藏 Site。
    /// </summary>
    [HttpPost("poll-token")]
    public async Task<IActionResult> PollToken([FromBody] KimiPollTokenRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.DeviceCode))
        {
            return BadRequest(new { message = "DeviceCode 不能为空" });
        }

        try
        {
            var exchange = await _oauth.ExchangeDeviceCodeAsync(req.DeviceCode, req.DeviceId, ct);
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
            var input = new KimiProvisionInput
            {
                DisplayName = !string.IsNullOrWhiteSpace(req.DisplayName) ? req.DisplayName : "Kimi 账号",
                DeviceId = req.DeviceId,
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
            _logger.LogWarning(ex, "Error while polling Kimi token");
            return BadRequest(new { message = "Token 交换异常：" + ex.Message });
        }
    }

    /// <summary>
    /// 获取全部 Kimi 账号列表。
    /// </summary>
    [HttpGet("accounts")]
    public async Task<IActionResult> ListAccounts(CancellationToken ct)
    {
        var accounts = await _dbContext.KimiAccounts
            .Where(a => !a.IsDeleted)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        return Ok(accounts.Select(ToSummary));
    }

    /// <summary>
    /// 切换账号启用/禁用状态。
    /// </summary>
    [HttpPost("accounts/{id:guid}/toggle")]
    public async Task<IActionResult> ToggleAccount(Guid id, [FromBody] KimiToggleAccountRequest? req, CancellationToken ct)
    {
        var isEnabled = req?.IsEnabled ?? true;
        await _provisioner.ToggleAsync(id, isEnabled, ct);
        return Ok(new { success = true });
    }

    /// <summary>
    /// 编辑账号展示名与（可选）refresh_token。
    /// </summary>
    [HttpPut("accounts/{id:guid}")]
    public async Task<IActionResult> UpdateAccount(Guid id, [FromBody] KimiUpdateAccountRequest req, CancellationToken ct)
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
            _logger.LogWarning(ex, "Failed to update Kimi account {Id}", id);
            return BadRequest(new { message = "更新失败：" + ex.Message });
        }
    }

    /// <summary>
    /// 手动刷新账号 Token。
    /// </summary>
    [HttpPost("accounts/{id:guid}/refresh-token")]
    public async Task<IActionResult> RefreshToken(Guid id, CancellationToken ct)
    {
        try
        {
            var account = await _credentialRefreshService.RefreshKimiCredentialAsync(id, ct);
            return Ok(ToSummary(account));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to manually refresh Kimi account {Id}", id);
            return BadRequest(new { message = "刷新 Token 失败：" + ex.Message });
        }
    }

    /// <summary>
    /// 手动刷新账号额度（实时查询 /coding/v1/usages 并持久化）。
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
    /// 删除 Kimi 账号及其关联隐藏 Site。
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
        var account = await _dbContext.KimiAccounts.InSingleAsync(id);
        if (account == null || account.IsDeleted)
        {
            return NotFound(new { message = "账号不存在" });
        }

        var models = await _modelFetcher.FetchAsync(account.AccessToken ?? string.Empty, account.DeviceId, ct);
        var existingMappings = await _dbContext.SiteModelMappings
            .Where(m => m.SiteId == account.LinkedSiteId)
            .ToListAsync(ct);

        var existingDict = existingMappings.ToDictionary(m => m.RemoteModelName, StringComparer.OrdinalIgnoreCase);

        var items = models.Select(m =>
        {
            // 拉取清单的键是对外公开名，映射按上游规范 ID 命中。
            var upstream = KimiModelNormalizer.NormalizeUpstreamModel(m.Slug);
            Domain.SiteCatalog.SiteModelMapping? mapping = null;
            var hasExisting = !string.IsNullOrWhiteSpace(upstream) && existingDict.TryGetValue(upstream, out mapping!);
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
    public async Task<IActionResult> ImportSelectedModels(Guid id, [FromBody] KimiImportModelsRequest req, CancellationToken ct)
    {
        var account = await _dbContext.KimiAccounts.InSingleAsync(id);
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
    /// 导入凭证文件（CLIProxyAPI Kimi 凭证 JSON 或 raw token）。
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
            parseResults.Add((name ?? "kimi_credential.json", await sr.ReadToEndAsync(ct)));
        }

        var successes = new List<object>();
        var failures = new List<object>();

        foreach (var (fileName, json) in parseResults)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string? accessToken = null;
                string? refreshToken = null;
                string? deviceId = null;
                string? email = null;
                string? displayName = null;

                if (root.TryGetProperty("access_token", out var at)) accessToken = at.GetString();
                if (root.TryGetProperty("refresh_token", out var rt)) refreshToken = rt.GetString();
                if (root.TryGetProperty("device_id", out var di)) deviceId = di.GetString();
                if (root.TryGetProperty("email", out var em)) email = em.GetString();
                if (root.TryGetProperty("display_name", out var dn)) displayName = dn.GetString();

                // 兼容 Storage 嵌套结构
                if (root.TryGetProperty("storage", out var storage) && storage.ValueKind == JsonValueKind.Object)
                {
                    if (storage.TryGetProperty("access_token", out var sat) && string.IsNullOrWhiteSpace(accessToken)) accessToken = sat.GetString();
                    if (storage.TryGetProperty("refresh_token", out var srt) && string.IsNullOrWhiteSpace(refreshToken)) refreshToken = srt.GetString();
                    if (storage.TryGetProperty("device_id", out var sdi) && string.IsNullOrWhiteSpace(deviceId)) deviceId = sdi.GetString();
                }

                if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(refreshToken))
                {
                    failures.Add(new { fileName, error = "凭证 JSON 缺少 access_token 或 refresh_token" });
                    continue;
                }

                var resolvedDisplayName = !string.IsNullOrWhiteSpace(displayName)
                    ? displayName
                    : Path.GetFileNameWithoutExtension(fileName);

                var input = new KimiProvisionInput
                {
                    DisplayName = resolvedDisplayName,
                    Email = email,
                    DeviceId = deviceId,
                    AccessToken = accessToken ?? string.Empty,
                    RefreshToken = refreshToken,
                    TokenType = "bearer"
                    // TokenExpiresAt 留空：导入时无法得知真实过期时间，
                    // 后台 KimiTokenRefreshService 首轮扫描会刷新一次以获取真实的 expires_in。
                };

                // 若只有 refresh_token，则先刷新一次获取 access_token
                if (string.IsNullOrWhiteSpace(input.AccessToken) && !string.IsNullOrWhiteSpace(input.RefreshToken))
                {
                    var tokens = await _oauth.RefreshTokenAsync(input.RefreshToken, input.DeviceId, ct);
                    input.AccessToken = tokens.AccessToken;
                    if (!string.IsNullOrWhiteSpace(tokens.RefreshToken)) input.RefreshToken = tokens.RefreshToken;
                    input.TokenExpiresAt = tokens.ExpiresAt;
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
    /// 导出选中的 Kimi 账号凭证。
    /// </summary>
    [HttpGet("export-credential")]
    public async Task<IActionResult> ExportCredential([FromQuery] string? ids, CancellationToken ct)
    {
        var idList = (ids ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();

        var query = _dbContext.KimiAccounts.Where(a => !a.IsDeleted);
        if (idList.Count > 0)
        {
            query = query.Where(a => idList.Contains(a.Id));
        }

        var accounts = await query.ToListAsync(ct);
        var credentials = accounts.Select(a => new
        {
            type = "kimi",
            display_name = a.DisplayName,
            email = a.Email,
            device_id = a.DeviceId,
            access_token = a.AccessToken,
            refresh_token = a.RefreshToken,
            token_type = a.TokenType,
            scope = a.Scope,
            token_expires_at = a.TokenExpiresAt?.ToString("o"),
            created_at = a.CreatedAt.ToString("o")
        });

        return Ok(new { credentials });
    }

    private static object ToSummary(KimiAccount a)
    {
        // 从最近一次额度查询的原始响应恢复窗口，供前端卡片直接渲染。
        var windows = KimiQuotaParser.Parse(a.LastQuotaRawJson ?? string.Empty) ?? [];

        return new
        {
            id = a.Id,
            displayName = a.DisplayName,
            email = a.Email,
            userId = a.UserId,
            deviceId = a.DeviceId,
            planType = "Kimi Code",
            isEnabled = a.IsEnabled,
            isQuotaCooling = false,
            quotaCoolingUntil = (string?)null,
            lastQuotaCheckedAt = a.LastQuotaCheckedAt?.ToString("o"),
            windows = windows.Select(w => new
            {
                id = w.Id,
                label = w.Label,
                usedPercent = w.UsedPercent,
                resetLabel = w.ResetLabel
            }),
            tokenExpiresAt = a.TokenExpiresAt?.ToString("o"),
            createdAt = a.CreatedAt.ToString("o"),
            linkedSiteId = a.LinkedSiteId
        };
    }
}
