using System.Collections.Concurrent;
using System.Text.Json;
using AITool.Application.Codex;
using AITool.Application.Common;
using AITool.Domain.Codex;
using AITool.Infrastructure.Codex;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// Codex 账号管理 API。集中暴露 OAuth 登录、凭证导入、账号列表、额度查询/重置、启用禁用、删除、编辑等。
/// 路由前缀 /api/admin/codex，自动受 /api/admin/* 鉴权保护。
/// 受 Codex 功能总开关保护：关闭时全部返回 404。
/// </summary>
[ApiController]
[Route("api/admin/codex")]
[ServiceFilter(typeof(CodexFeatureToggleAttribute))]
public sealed class CodexApiController : ControllerBase
{
    // —— OAuth 会话暂存（state → verifier，TTL 10min）——
    private sealed record OAuthSession(string State, string Verifier, DateTimeOffset ExpiresAt);
    private static readonly ConcurrentDictionary<string, OAuthSession> Sessions = new();

    private readonly AppDbContext _dbContext;
    private readonly ICodexOAuthClient _oauth;
    private readonly CodexAccountProvisioner _provisioner;
    private readonly ICodexModelFetcher _modelFetcher;
    private readonly ICodexQuotaService _quotaService;
    private readonly ICodexQuotaCooldownService _cooldownService;
    private readonly ICodexResetCreditsService _resetCreditsService;
    private readonly CodexInspectionService _inspectionService;
    private readonly ILogger<CodexApiController> _logger;

    public CodexApiController(
        AppDbContext dbContext,
        ICodexOAuthClient oauth,
        CodexAccountProvisioner provisioner,
        ICodexModelFetcher modelFetcher,
        ICodexQuotaService quotaService,
        ICodexQuotaCooldownService cooldownService,
        ICodexResetCreditsService resetCreditsService,
        CodexInspectionService inspectionService,
        ILogger<CodexApiController> logger)
    {
        _dbContext = dbContext;
        _oauth = oauth;
        _provisioner = provisioner;
        _modelFetcher = modelFetcher;
        _quotaService = quotaService;
        _cooldownService = cooldownService;
        _resetCreditsService = resetCreditsService;
        _inspectionService = inspectionService;
        _logger = logger;
    }

    /// <summary>启动 OAuth 登录，返回授权 URL 与 state。</summary>
    [HttpPost("start-oauth")]
    public IActionResult StartOAuth([FromBody] StartOAuthRequest? req)
    {
        CleanupExpiredSessions();
        var (state, verifier) = _oauth.CreateOAuthSession();
        var url = _oauth.BuildAuthorizeUrl(state, verifier);
        Sessions[state] = new OAuthSession(state, verifier, DateTimeOffset.UtcNow.AddMinutes(10));
        return Ok(new { url, state });
    }

    /// <summary>完成 OAuth 登录：用户粘贴回调 URL，校验 state 后交换 token 并建账号。</summary>
    [HttpPost("complete-oauth")]
    public async Task<IActionResult> CompleteOAuth([FromBody] CompleteOAuthRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.CallbackUrl))
        {
            return BadRequest(new { message = "回调 URL 不能为空" });
        }

        string code;
        string state;
        try
        {
            var uri = new Uri(req.CallbackUrl);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            code = query["code"] ?? string.Empty;
            state = query["state"] ?? string.Empty;
        }
        catch
        {
            return BadRequest(new { message = "回调 URL 格式无法解析" });
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return BadRequest(new { message = "回调 URL 缺少 code 或 state" });
        }

        if (!Sessions.TryRemove(state, out var session))
        {
            return BadRequest(new { message = "state 无效或已过期（10 分钟），请重新开始登录" });
        }
        if (session.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return BadRequest(new { message = "state 已过期，请重新开始登录" });
        }

        CodexTokenSet tokens;
        try
        {
            tokens = await _oauth.ExchangeCodeAsync(code, session.Verifier, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Codex OAuth code exchange failed");
            return BadRequest(new { message = "授权码交换失败：" + ex.Message });
        }

        var claims = CodexJwtParser.Parse(tokens.IdToken);
        var input = new CodexProvisionInput
        {
            DisplayName = !string.IsNullOrWhiteSpace(req.DisplayName) ? req.DisplayName : (claims?.Email ?? "Codex 账号"),
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            IdToken = tokens.IdToken,
            AccountId = claims?.AccountId,
            Email = claims?.Email,
            PlanType = claims?.PlanType,
            TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600),
        };
        var account = await _provisioner.ProvisionFromTokensAsync(input, ct);
        return Ok(ToSummary(account));
    }

    /// <summary>导入凭证文件（multipart 多文件 或 raw body 单文件）。</summary>
    [HttpPost("import-credential")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> ImportCredential([FromQuery] string? name, CancellationToken ct)
    {
        var parseResults = new List<CodexCredentialParseResult>();

        if (Request.HasFormContentType && Request.Form.Files.Count > 0)
        {
            foreach (var file in Request.Form.Files)
            {
                using var sr = new StreamReader(file.OpenReadStream());
                var json = await sr.ReadToEndAsync(ct);
                parseResults.Add(CodexCredentialParser.Parse(json, file.FileName));
            }
        }
        else
        {
            using var sr = new StreamReader(Request.Body);
            var json = await sr.ReadToEndAsync(ct);
            parseResults.Add(CodexCredentialParser.Parse(json, name));
        }

        var summaries = new List<object>();
        foreach (var r in parseResults.Where(r => r.Success))
        {
            var input = new CodexProvisionInput
            {
                DisplayName = r.DisplayName ?? "Codex 账号",
                AccessToken = r.AccessToken ?? string.Empty,
                RefreshToken = r.RefreshToken ?? string.Empty,
                IdToken = r.IdToken ?? string.Empty,
                AccountId = r.AccountId,
                Email = r.Email,
                PlanType = r.PlanType,
                TokenExpiresAt = r.TokenExpiresAt,
            };
            var account = await _provisioner.ProvisionFromTokensAsync(input, ct);
            summaries.Add(ToSummary(account));
        }

        var failures = parseResults.Where(r => !r.Success).Select(r => new { r.FileName, r.Error }).ToList();
        if (failures.Count > 0)
        {
            return StatusCode(207, new { successes = summaries, failures });
        }
        return Ok(new { successes = summaries });
    }

    /// <summary>列出全部 Codex 账号（含状态/额度缓存字段）。</summary>
    [HttpGet("accounts")]
    public async Task<IActionResult> ListAccounts(CancellationToken ct)
    {
        var accounts = await _dbContext.CodexAccounts
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
        return Ok(accounts.Select(ToSummary).ToList());
    }

    /// <summary>查询指定账号额度（forceRefresh 穿透 30s 缓存）。</summary>
    [HttpPost("accounts/{id}/refresh-quota")]
    public async Task<IActionResult> RefreshQuota(Guid id, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });

        var info = await _quotaService.QueryAsync(account, forceRefresh: true, ct);
        return Ok(info);
    }

    /// <summary>重置额度（清冷却 + 刷新 token + 恢复）。前端需二次确认。</summary>
    [HttpPost("accounts/{id}/reset-quota")]
    public async Task<IActionResult> ResetQuota(Guid id, CancellationToken ct)
    {
        try
        {
            await _cooldownService.ResetAsync(id, ct);
            return Ok(new { message = "已重置" });
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { message = "账号不存在" });
        }
    }

    /// <summary>切换启用/禁用。</summary>
    [HttpPost("accounts/{id}/toggle")]
    public async Task<IActionResult> Toggle(Guid id, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });

        account.IsEnabled = !account.IsEnabled;
        await _dbContext.UpdateAsync(account, ct);

        var site = await _dbContext.Sites.InSingleAsync(account.LinkedSiteId);
        if (site != null)
        {
            site.IsEnabled = account.IsEnabled;
            await _dbContext.UpdateAsync(site, ct);
        }
        return Ok(ToSummary(account));
    }

    /// <summary>删除账号（级联删隐藏 Site + 映射 + 路由规则）。</summary>
    [HttpDelete("accounts/{id}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try
        {
            await _provisioner.DeprovisionAsync(id, ct);
            return Ok(new { message = "已删除" });
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { message = "账号不存在" });
        }
    }

    /// <summary>编辑账号（名称 / 自动禁用阈值）。</summary>
    [HttpPut("accounts/{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAccountRequest req, CancellationToken ct)
    {
        try
        {
            await _provisioner.UpdateAsync(id, req.DisplayName, req.AutoDisableThreshold, ct);
            var account = await GetAccountAsync(id, ct);
            return Ok(ToSummary(account!));
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { message = "账号不存在" });
        }
    }

    /// <summary>手动刷新 token。</summary>
    [HttpPost("accounts/{id}/refresh-token")]
    public async Task<IActionResult> RefreshToken(Guid id, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });
        if (string.IsNullOrEmpty(account.RefreshToken))
        {
            return BadRequest(new { message = "账号无 refresh_token" });
        }

        try
        {
            var tokens = await _oauth.RefreshTokenAsync(account.RefreshToken, ct);
            account.AccessToken = tokens.AccessToken;
            account.RefreshToken = tokens.RefreshToken;
            if (!string.IsNullOrEmpty(tokens.IdToken)) account.IdToken = tokens.IdToken;
            account.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600);
            account.LastRefreshAt = DateTimeOffset.UtcNow;
            await _dbContext.UpdateAsync(account, ct);

            var site = await _dbContext.Sites.InSingleAsync(account.LinkedSiteId);
            if (site != null)
            {
                site.ApiKey = tokens.AccessToken;
                await _dbContext.UpdateAsync(site, ct);
            }
            return Ok(ToSummary(account));
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = "刷新失败：" + ex.Message });
        }
    }

    /// <summary>动态拉取上游模型目录并追加映射。</summary>
    [HttpPost("accounts/{id}/pull-models")]
    public async Task<IActionResult> PullModels(Guid id, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });
        if (string.IsNullOrEmpty(account.AccessToken))
        {
            return BadRequest(new { message = "账号无 access_token" });
        }

        List<CodexRemoteModel> models;
        try
        {
            models = (await _modelFetcher.FetchAsync(account.AccessToken, account.AccountId ?? string.Empty, ct)).ToList();
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = "拉取模型失败：" + ex.Message });
        }

        await _provisioner.UpsertRemoteModelsAsync(account.LinkedSiteId,
            models.Select(m => (m.Slug, m.DisplayName)), ct);
        return Ok(new { count = models.Count });
    }

    // —— 巡检 ——

    /// <summary>触发一轮巡检。force=true 强制真实刷新全部账号；false 允许命中缓存。</summary>
    [HttpPost("inspection/run")]
    public async Task<IActionResult> RunInspection([FromQuery] bool force, CancellationToken ct)
    {
        var result = await _inspectionService.RunManualAsync(force, ct);
        return Ok(result);
    }

    /// <summary>巡检状态（是否运行中、下次调度时间、上次完成时间）。</summary>
    [HttpGet("inspection/status")]
    public IActionResult InspectionStatus()
    {
        return Ok(_inspectionService.GetStatus());
    }

    /// <summary>上次巡检结果（每账号动作/原因/百分比）。</summary>
    [HttpGet("inspection/last-run")]
    public IActionResult InspectionLastRun()
    {
        return Ok(_inspectionService.GetLastRun());
    }

    /// <summary>巡检操作日志（最新在前）。</summary>
    [HttpGet("inspection/logs")]
    public IActionResult InspectionLogs()
    {
        return Ok(_inspectionService.GetLogs());
    }

    // —— Reset Credits ——

    /// <summary>查询账号的手动重置 credits（剩余次数 + 每张过期时间）。</summary>
    [HttpGet("accounts/{id}/reset-credits")]
    public async Task<IActionResult> GetResetCredits(Guid id, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });

        var info = await _resetCreditsService.QueryResetCreditsAsync(account, ct);
        return Ok(info);
    }

    /// <summary>消耗一张 reset credit，执行真实额度重置。</summary>
    [HttpPost("accounts/{id}/consume-reset-credit")]
    public async Task<IActionResult> ConsumeResetCredit(Guid id, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });

        var redeemRequestId = Guid.NewGuid().ToString();
        var (success, error) = await _resetCreditsService.ConsumeResetCreditAsync(account, redeemRequestId, ct);
        if (!success) return BadRequest(new { message = error });

        // 消耗成功后重新刷新额度（让前端能看到重置后的新额度）
        await _quotaService.QueryAsync(account, forceRefresh: true, ct);

        return Ok(new { message = "手动重置额度成功" });
    }

    // —— 私有 ——

    private async Task<CodexAccount?> GetAccountAsync(Guid id, CancellationToken ct)
    {
        return (await _dbContext.CodexAccounts.Where(a => a.Id == id).ToListAsync(ct)).FirstOrDefault();
    }

    private static object ToSummary(CodexAccount a)
    {
        // 从最近一次额度查询的原始响应解析窗口（供前端画进度条，无需每次单独刷新）
        List<object>? windows = null;
        double? fiveHour = null;
        double? weekly = null;
        int? resetCreditsAvailableCount = null;
        if (!string.IsNullOrEmpty(a.LastQuotaRawJson))
        {
            try
            {
                var (planType, parsedWindows) = CodexUsageParser.Parse(a.LastQuotaRawJson);
                windows = parsedWindows.Select(w => (object)new
                {
                    id = w.Id, label = w.Label,
                    usedPercent = w.UsedPercent, resetLabel = w.ResetLabel,
                }).ToList();
                fiveHour = parsedWindows.FirstOrDefault(w => w.Id == "five-hour")?.UsedPercent;
                weekly = parsedWindows.FirstOrDefault(w => w.Id == "weekly")?.UsedPercent;

                // 解析 rate_limit_reset_credits.available_count（如果存在）
                var json = System.Text.Json.JsonDocument.Parse(a.LastQuotaRawJson);
                if (json.RootElement.TryGetProperty("rate_limit_reset_credits", out var rlrcEl) ||
                    json.RootElement.TryGetProperty("rateLimitResetCredits", out rlrcEl))
                {
                    if (rlrcEl.TryGetProperty("available_count", out var countEl) ||
                        rlrcEl.TryGetProperty("availableCount", out countEl))
                    {
                        if (countEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            resetCreditsAvailableCount = countEl.GetInt32();
                        }
                        else if (countEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            if (int.TryParse(countEl.GetString(), out var c)) resetCreditsAvailableCount = c;
                        }
                    }
                }
            }
            catch { }
        }

        return new
        {
            id = a.Id,
            displayName = a.DisplayName,
            email = a.Email,
            accountId = a.AccountId,
            planType = a.PlanType,
            isEnabled = a.IsEnabled,
            isQuotaCooling = a.IsQuotaCooling,
            quotaCoolingUntil = a.QuotaCoolingUntil,
            autoDisableThreshold = a.AutoDisableThreshold,
            windows,
            fiveHourUsedPercent = fiveHour,
            weeklyUsedPercent = weekly,
            resetCreditsAvailableCount,
            lastQuotaCheckedAt = a.LastQuotaCheckedAt,
            tokenExpiresAt = a.TokenExpiresAt,
            createdAt = a.CreatedAt,
        };
    }

    private static void CleanupExpiredSessions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in Sessions)
        {
            if (kv.Value.ExpiresAt < now) Sessions.TryRemove(kv.Key, out _);
        }
    }
}

// —— 请求 DTO ——

public sealed class StartOAuthRequest
{
    public string? DisplayName { get; set; }
}

public sealed class CompleteOAuthRequest
{
    /// <summary>用户粘贴的回调 URL（含 code/state 查询参数）。</summary>
    public string? CallbackUrl { get; set; }
    public string? DisplayName { get; set; }
}

public sealed class UpdateAccountRequest
{
    public string? DisplayName { get; set; }
    public decimal? AutoDisableThreshold { get; set; }
}
