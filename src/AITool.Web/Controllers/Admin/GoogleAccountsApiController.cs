using System.Collections.Concurrent;
using System.Text.Json;
using AITool.Application.Google;
using AITool.Domain.Google;
using AITool.Infrastructure.Google;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// Google 账号（GeminiCLI / Antigravity）管理接口：OAuth 登录（粘贴回调 URL）、凭证导入、
/// 额度查询、启停、编辑、删除与模型拉取。结构与 CodexApiController 对齐，
/// 走同一 OAuth 功能总开关。
/// </summary>
[Route("api/admin/google-accounts")]
[ServiceFilter(typeof(OAuthFeatureToggleAttribute))]
public sealed class GoogleAccountsApiController : ControllerBase
{
    // —— OAuth 会话暂存（state → 会话，TTL 10min）——
    private sealed record OAuthSession(string State, string AccountKind, DateTimeOffset ExpiresAt);
    private static readonly ConcurrentDictionary<string, OAuthSession> Sessions = new();

    private readonly AppDbContext _dbContext;
    private readonly IGoogleOAuthClient _oauth;
    private readonly GoogleAccountProvisioner _provisioner;
    private readonly IGoogleModelFetcher _modelFetcher;
    private readonly GoogleAccountQuotaService _quotaService;
    private readonly GoogleCredentialRefreshService _credentialRefreshService;
    private readonly ILogger<GoogleAccountsApiController> _logger;

    public GoogleAccountsApiController(
        AppDbContext dbContext,
        IGoogleOAuthClient oauth,
        GoogleAccountProvisioner provisioner,
        IGoogleModelFetcher modelFetcher,
        GoogleAccountQuotaService quotaService,
        GoogleCredentialRefreshService credentialRefreshService,
        ILogger<GoogleAccountsApiController> logger)
    {
        _dbContext = dbContext;
        _oauth = oauth;
        _provisioner = provisioner;
        _modelFetcher = modelFetcher;
        _quotaService = quotaService;
        _credentialRefreshService = credentialRefreshService;
        _logger = logger;
    }

    /// <summary>启动 OAuth 登录，返回授权 URL 与 state。kind: GeminiCli / Antigravity。</summary>
    [HttpPost("start-oauth")]
    public IActionResult StartOAuth([FromBody] GoogleStartOAuthRequest? req)
    {
        CleanupExpiredSessions();
        var kind = GoogleAccountKinds.Normalize(req?.Kind);
        var session = _oauth.CreateSession();
        var url = _oauth.BuildAuthorizeUrl(kind, session);
        Sessions[session.State] = new OAuthSession(session.State, kind, DateTimeOffset.UtcNow.AddMinutes(10));
        return Ok(new { url, state = session.State, kind });
    }

    /// <summary>完成 OAuth 登录：用户粘贴回调 URL，校验 state 后交换 token、探测项目/等级并建账号。</summary>
    [HttpPost("complete-oauth")]
    public async Task<IActionResult> CompleteOAuth([FromBody] GoogleCompleteOAuthRequest req, CancellationToken ct)
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

        GoogleTokenSet tokens;
        try
        {
            tokens = await _oauth.ExchangeCodeAsync(session.AccountKind, code, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google OAuth code exchange failed");
            return BadRequest(new { message = "授权码交换失败：" + ex.Message });
        }

        var input = await BuildProvisionInputAsync(session.AccountKind, tokens, req.DisplayName, projectId: null, ct);
        var account = await _provisioner.ProvisionFromTokensAsync(input, ct);
        return Ok(ToSummary(account));
    }

    /// <summary>导入 gcli2api 凭证 JSON（multipart 多文件 或 raw body 单文件；kind 指定接入方式）。</summary>
    [HttpPost("import-credential")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> ImportCredential([FromQuery] string? kind, [FromQuery] string? name, CancellationToken ct)
    {
        var accountKind = GoogleAccountKinds.Normalize(kind);
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
            parseResults.Add((name ?? "credential.json", await sr.ReadToEndAsync(ct)));
        }

        var summaries = new List<object>();
        var failures = new List<object>();
        foreach (var (fileName, json) in parseResults)
        {
            try
            {
                var refreshToken = ExtractCredentialField(json, "refresh_token");
                if (string.IsNullOrWhiteSpace(refreshToken))
                {
                    failures.Add(new { fileName, error = "凭证缺少 refresh_token 字段" });
                    continue;
                }

                var tokens = await _oauth.RefreshTokenAsync(accountKind, refreshToken, ct);
                var projectId = ExtractCredentialField(json, "project_id");
                var input = await BuildProvisionInputAsync(accountKind, tokens, name, projectId, ct);
                var account = await _provisioner.ProvisionFromTokensAsync(input, ct);
                summaries.Add(ToSummary(account));
            }
            catch (Exception ex)
            {
                failures.Add(new { fileName, error = ex.Message });
            }
        }

        if (failures.Count > 0)
        {
            return StatusCode(207, new { successes = summaries, failures });
        }
        return Ok(new { successes = summaries });
    }

    /// <summary>列出全部 Google 账号（含状态/额度缓存字段）。</summary>
    [HttpGet("accounts")]
    public async Task<IActionResult> ListAccounts(CancellationToken ct)
    {
        var accounts = await _dbContext.GoogleAccounts
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
        return Ok(accounts.Select(a => ToSummary(a)).ToList());
    }

    /// <summary>查询指定账号额度（forceRefresh 穿透 30s 缓存）。</summary>
    [HttpPost("accounts/{id}/refresh-quota")]
    public async Task<IActionResult> RefreshQuota(Guid id, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });

        var snapshot = await _quotaService.QueryAsync(new Application.Accounts.AccountQuotaTarget
        {
            ProviderKey = "google",
            AccountId = account.Id,
        }, forceRefresh: true, ct);
        return Ok(snapshot);
    }

    /// <summary>启停账号（同步隐藏 Site 并失效缓存）。</summary>
    [HttpPost("accounts/{id}/toggle")]
    public async Task<IActionResult> Toggle(Guid id, [FromBody] GoogleToggleRequest? req, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });

        var enabled = req?.Enabled ?? !account.IsEnabled;
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        account.IsEnabled = enabled;
        account.ManuallyDisabled = !enabled;
        await client.Updateable(account)
            .UpdateColumns(x => new { x.IsEnabled, x.ManuallyDisabled })
            .ExecuteCommandAsync(ct);

        var site = await client.Queryable<Domain.Sites.Site>().InSingleAsync(account.LinkedSiteId);
        if (site is not null && site.IsEnabled != enabled)
        {
            site.IsEnabled = enabled;
            await client.Updateable(site).UpdateColumns(x => new { x.IsEnabled }).ExecuteCommandAsync(ct);
        }

        var cache = HttpContext.RequestServices.GetRequiredService<ProxyRequestMetadataCache>();
        cache.InvalidateRouteTargets();
        cache.InvalidateGoogleAccounts();
        return Ok(ToSummary(account));
    }

    /// <summary>删除账号（级联删除隐藏 Site 与模型映射）。</summary>
    [HttpDelete("accounts/{id}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _provisioner.DeprovisionAsync(id, ct);
        return Ok(new { message = "已删除" });
    }

    /// <summary>编辑账号（重命名 + 可选替换 refresh_token 并立即刷新）。</summary>
    [HttpPut("accounts/{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] GoogleUpdateAccountRequest req, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });

        if (!string.IsNullOrWhiteSpace(req.RefreshToken))
        {
            var tokens = await _oauth.RefreshTokenAsync(account.AccountKind, req.RefreshToken, ct);
            var input = await BuildProvisionInputAsync(account.AccountKind, tokens, req.DisplayName, account.ProjectId, ct);
            account = await _provisioner.ProvisionFromTokensAsync(input, ct);
            return Ok(ToSummary(account, "凭证已更新"));
        }

        await _provisioner.UpdateAsync(id, req.DisplayName, ct);
        account = await GetAccountAsync(id, ct) ?? account;
        return Ok(ToSummary(account));
    }

    /// <summary>拉取账号可用模型（Antigravity 动态 / GeminiCli 静态清单，附现有映射状态）。</summary>
    [HttpGet("accounts/{id}/fetch-models")]
    public async Task<IActionResult> FetchModels(Guid id, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });

        // token 过期或临期先刷新（拉取对 token 有效性敏感）；经凭证刷新服务持久化并同步隐藏 Site，
        // 避免下次代理请求再吃一次 401。
        var accessToken = account.AccessToken ?? string.Empty;
        if (account.TokenExpiresAt is null || account.TokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
        {
            var refreshed = await _credentialRefreshService.RefreshAsync(account.LinkedSiteId, accessToken, ct);
            if (!string.IsNullOrWhiteSpace(refreshed))
            {
                accessToken = refreshed;
            }
        }

        var models = await _modelFetcher.FetchAsync(account.AccountKind, accessToken, ct);

        // 附带现有映射，前端区分"已导入/未导入"。
        var existing = await _dbContext.SiteModelMappings
            .Where(m => m.SiteId == account.LinkedSiteId)
            .ToListAsync(ct);
        var existingByRemote = existing.ToDictionary(m => m.RemoteModelName, m => m);

        var items = models.Select(model =>
        {
            existingByRemote.TryGetValue(model.Slug, out var mapping);
            return (object)new
            {
                remoteModelName = model.Slug,
                displayName = model.DisplayName,
                existingMappingId = mapping?.Id,
                existingDisplayName = mapping is null ? null : model.DisplayName,
                isEnabled = mapping?.IsEnabled ?? false,
            };
        }).ToList();

        return Ok(items);
    }

    /// <summary>导入选中的模型映射。</summary>
    [HttpPost("accounts/{id}/import-selected-models")]
    public async Task<IActionResult> ImportSelectedModels(Guid id, [FromBody] GoogleImportModelsRequest req, CancellationToken ct)
    {
        var account = await GetAccountAsync(id, ct);
        if (account == null) return NotFound(new { message = "账号不存在" });

        var models = (req.Models ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m.RemoteModelName))
            .Select(m => (m.RemoteModelName!.Trim(), string.IsNullOrWhiteSpace(m.DisplayName) ? m.RemoteModelName.Trim() : m.DisplayName!.Trim()))
            .ToList();
        if (models.Count == 0)
        {
            return BadRequest(new { message = "未选择任何模型" });
        }

        await _provisioner.UpsertRemoteModelsAsync(account.LinkedSiteId, models, ct);
        return Ok(new { imported = models.Count });
    }

    // —— 私有 ——

    /// <summary>
    /// 用刷新得到的 token 组装供给输入：Antigravity 经 loadCodeAssist 探测项目/tier/积分；
    /// GeminiCli 经资源管理器选择项目（唯一项目自动选、多个取含 default 的或第一个、失败回落共享默认项目）。
    /// </summary>
    private async Task<GoogleProvisionInput> BuildProvisionInputAsync(
        string accountKind,
        GoogleTokenSet tokens,
        string? displayName,
        string? projectId,
        CancellationToken ct)
    {
        var kind = GoogleAccountKinds.Normalize(accountKind);
        string? tier = null;
        int? creditAmount = null;
        string? email = null;

        if (string.Equals(kind, GoogleAccountKinds.Antigravity, StringComparison.OrdinalIgnoreCase))
        {
            var profile = await _oauth.LoadCodeAssistProfileAsync(kind, tokens.AccessToken, ct);
            projectId ??= profile.ProjectId;
            tier = profile.Tier;
            creditAmount = profile.CreditAmount;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(projectId))
            {
                var projects = await _oauth.GetUserProjectsAsync(tokens.AccessToken, ct);
                if (projects.Count == 1)
                {
                    projectId = projects[0];
                }
                else if (projects.Count > 1)
                {
                    projectId = projects.FirstOrDefault(p => p.Contains("default", StringComparison.OrdinalIgnoreCase))
                        ?? projects[0];
                }
                else
                {
                    // gcli2api 同款兜底共享项目。
                    projectId = "gemini-pro-1751713012-07fc4dfd";
                }
            }
        }

        email = await _oauth.GetUserEmailAsync(tokens.AccessToken, ct);

        return new GoogleProvisionInput
        {
            AccountKind = kind,
            DisplayName = !string.IsNullOrWhiteSpace(displayName) ? displayName : (email ?? $"{kind} 账号"),
            Email = email,
            ProjectId = projectId,
            SubscriptionTier = tier,
            CreditAmount = creditAmount,
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            TokenExpiresAt = tokens.ExpiresAt,
        };
    }

    private async Task<GoogleAccount?> GetAccountAsync(Guid id, CancellationToken ct)
    {
        return (await _dbContext.GoogleAccounts.Where(a => a.Id == id).ToListAsync(ct)).FirstOrDefault();
    }

    private static string? ExtractCredentialField(string json, string fieldName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(fieldName, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static object ToSummary(GoogleAccount a, string? message = null)
    {
        List<object>? windows = null;
        if (!string.IsNullOrEmpty(a.LastQuotaRawJson))
        {
            var parsed = GoogleQuotaParser.Parse(a.LastQuotaRawJson);
            if (parsed is not null)
            {
                windows = parsed.Select(w => (object)new
                {
                    id = w.Id,
                    label = w.Label,
                    usedPercent = w.UsedPercent,
                    resetLabel = w.ResetLabel,
                }).ToList();
            }
        }

        return new
        {
            id = a.Id,
            displayName = a.DisplayName,
            email = a.Email,
            accountKind = a.AccountKind,
            projectId = a.ProjectId,
            subscriptionTier = a.SubscriptionTier,
            creditAmount = a.CreditAmount,
            isEnabled = a.IsEnabled,
            isQuotaCooling = a.IsQuotaCooling,
            quotaCoolingUntil = a.QuotaCoolingUntil,
            windows,
            lastQuotaCheckedAt = a.LastQuotaCheckedAt,
            tokenExpiresAt = a.TokenExpiresAt,
            createdAt = a.CreatedAt,
            message,
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

public sealed class GoogleStartOAuthRequest
{
    public string? Kind { get; set; }
}

public sealed class GoogleCompleteOAuthRequest
{
    public string? CallbackUrl { get; set; }
    public string? DisplayName { get; set; }
}

public sealed class GoogleToggleRequest
{
    public bool? Enabled { get; set; }
}

public sealed class GoogleUpdateAccountRequest
{
    public string? DisplayName { get; set; }
    public string? RefreshToken { get; set; }
}

public sealed class GoogleImportModelsRequest
{
    public List<GoogleImportModelItem>? Models { get; set; }
}

public sealed class GoogleImportModelItem
{
    public string? RemoteModelName { get; set; }
    public string? DisplayName { get; set; }
}
