using System.Collections.Concurrent;
using System.Text.Json;
using AITool.Application.Google;
using AITool.Domain.Google;
using AITool.Infrastructure.Google;
using AITool.Infrastructure.Persistence;
using AITool.Admin.Services;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// Google 账号（Antigravity）管理接口：OAuth 登录（粘贴回调 URL）、凭证导入、
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

    /// <summary>启动 OAuth 登录，返回授权 URL 与 state（Antigravity 客户端）。</summary>
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
        var siteIds = accounts.Select(a => a.LinkedSiteId).Distinct().ToList();
        var selectedMappings = siteIds.Count == 0
            ? []
            : await _dbContext.SiteModelMappings
                .Where(mapping => siteIds.Contains(mapping.SiteId) && mapping.IsEnabled)
                .Select(mapping => new { mapping.SiteId, mapping.RemoteModelName })
                .ToListAsync(ct);
        var selectedModelsBySite = selectedMappings
            .GroupBy(mapping => mapping.SiteId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyCollection<string>)group
                    .Select(mapping => mapping.RemoteModelName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray());

        return Ok(accounts.Select(account =>
        {
            selectedModelsBySite.TryGetValue(account.LinkedSiteId, out var selectedModels);
            return ToSummary(account, selectedModels: selectedModels ?? Array.Empty<string>());
        }).ToList());
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
        if (enabled)
        {
            account.DisabledByUpstream = false;
        }
        await client.Updateable(account)
            .UpdateColumns(x => new { x.IsEnabled, x.ManuallyDisabled, x.DisabledByUpstream })
            .ExecuteCommandAsync(ct);

        var site = await client.Queryable<Domain.Sites.Site>().InSingleAsync(account.LinkedSiteId);
        if (site is not null && site.IsEnabled != enabled)
        {
            site.IsEnabled = enabled;
            await client.Updateable(site).UpdateColumns(x => new { x.IsEnabled }).ExecuteCommandAsync(ct);
        }

        // split 双宿主：账号/隐藏站点变更后推送到 Core（全量同步含账号凭证段）并失效本地缓存。
        await HttpContext.RequestServices.GetRequiredService<AdminCacheInvalidationService>()
            .InvalidateAccountCredentialsAsync(ct);
        var selectedModels = await GetSelectedModelsForSiteAsync(account.LinkedSiteId, ct);
        return Ok(ToSummary(account, selectedModels: selectedModels));
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
            var updatedSelectedModels = await GetSelectedModelsForSiteAsync(account.LinkedSiteId, ct);
            return Ok(ToSummary(account, "凭证已更新", selectedModels: updatedSelectedModels));
        }

        await _provisioner.UpdateAsync(id, req.DisplayName, ct);
        account = await GetAccountAsync(id, ct) ?? account;
        var currentSelectedModels = await GetSelectedModelsForSiteAsync(account.LinkedSiteId, ct);
        return Ok(ToSummary(account, selectedModels: currentSelectedModels));
    }

    /// <summary>拉取账号可用模型（Antigravity 动态清单，附现有映射状态）。</summary>
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

        IReadOnlyList<(string Slug, string DisplayName)> models;
        try
        {
            models = await _modelFetcher.FetchAsync(account.AccountKind, accessToken, ct);
        }
        catch (Exception ex) when (GoogleCredentialRefreshService.IsForbiddenResponse(ex))
        {
            await _credentialRefreshService.DisableAsync(account.LinkedSiteId, "model-fetch-403", ct);
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Google 上游返回 403，已自动禁用该账号，请完成验证或确认账号权限后再手动启用" });
        }

        // 附带现有映射，前端区分"已导入/未导入"。
        var existing = await _dbContext.SiteModelMappings
            .Where(m => m.SiteId == account.LinkedSiteId)
            .ToListAsync(ct);
        var mappingModelIds = existing
            .Where(mapping => mapping.ModelLibraryItemId != Guid.Empty)
            .Select(mapping => mapping.ModelLibraryItemId)
            .Distinct()
            .ToList();
        var remoteNames = models.Select(model => model.Slug).ToList();
        var modelItems = await _dbContext.ModelLibraryItems
            .Where(model => remoteNames.Contains(model.ModelName) || mappingModelIds.Contains(model.Id))
            .ToListAsync(ct);
        var modelItemsById = modelItems.ToDictionary(model => model.Id);
        var existingByRemote = existing.ToDictionary(m => m.RemoteModelName, m => m, StringComparer.OrdinalIgnoreCase);

        var items = models.Select(model =>
        {
            existingByRemote.TryGetValue(model.Slug, out var mapping);
            var existingModelItem = mapping is not null && modelItemsById.TryGetValue(mapping.ModelLibraryItemId, out var modelItem)
                ? modelItem
                : null;
            return (object)new
            {
                remoteModelName = model.Slug,
                displayName = model.Slug,
                existingMappingId = mapping?.Id,
                existingDisplayName = existingModelItem is not null
                    && !string.Equals(existingModelItem.ModelName, model.Slug, StringComparison.OrdinalIgnoreCase)
                    ? existingModelItem.ModelName
                    : null,
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

        var selections = (req.Models ?? [])
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
        return Ok(new
        {
            imported = selections.Count(m => m.Selected),
            disabled = selections.Count(m => !m.Selected)
        });
    }

    // —— 私有 ——

    /// <summary>
    /// 用刷新得到的 token 组装供给输入：Antigravity 经 loadCodeAssist 探测项目/tier/积分。
    /// </summary>
    private async Task<GoogleProvisionInput> BuildProvisionInputAsync(
        string accountKind,
        GoogleTokenSet tokens,
        string? displayName,
        string? projectId,
        CancellationToken ct)
    {
        var kind = GoogleAccountKinds.Normalize(accountKind);
        var profile = await _oauth.LoadCodeAssistProfileAsync(kind, tokens.AccessToken, ct);
        projectId ??= profile.ProjectId;
        var tier = profile.Tier;
        var creditAmount = profile.CreditAmount;

        var email = await _oauth.GetUserEmailAsync(tokens.AccessToken, ct);

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

    private async Task<IReadOnlyCollection<string>> GetSelectedModelsForSiteAsync(Guid siteId, CancellationToken ct)
    {
        return await _dbContext.SiteModelMappings
            .Where(mapping => mapping.SiteId == siteId && mapping.IsEnabled)
            .Select(mapping => mapping.RemoteModelName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .ToListAsync(ct);
    }

    private static object ToSummary(
        GoogleAccount a,
        string? message = null,
        IReadOnlyCollection<string>? selectedModels = null)
    {
        List<object>? windows = null;
        var selectedModelNames = selectedModels?
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();
        if (!string.IsNullOrEmpty(a.LastQuotaRawJson))
        {
            var parsed = GoogleQuotaParser.Parse(a.LastQuotaRawJson);
            if (parsed is not null)
            {
                if (string.Equals(a.AccountKind, GoogleAccountKinds.Antigravity, StringComparison.OrdinalIgnoreCase))
                {
                    if (selectedModelNames.Length > 0)
                    {
                        var list = new List<object>();
                        foreach (var modelName in selectedModelNames)
                        {
                            // 优先精确匹配，其次前缀/变体匹配，确保每个已勾选模型仅对应一条额度条
                            var window = parsed.FirstOrDefault(w => string.Equals(w.Id, modelName, StringComparison.OrdinalIgnoreCase))
                                         ?? parsed.FirstOrDefault(w => IsModelMatchingQuotaWindow(w.Id, modelName));
                            if (window is not null)
                            {
                                list.Add(new
                                {
                                    id = modelName,
                                    label = modelName,
                                    usedPercent = window.UsedPercent,
                                    resetLabel = window.ResetLabel,
                                });
                            }
                        }
                        windows = list;
                    }
                    else
                    {
                        windows = [];
                    }
                }
                else
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
            disabledByUpstream = a.DisabledByUpstream,
            isQuotaCooling = a.IsQuotaCooling,
            quotaCoolingUntil = a.QuotaCoolingUntil,
            selectedModels = selectedModelNames,
            windows,
            lastQuotaCheckedAt = a.LastQuotaCheckedAt,
            tokenExpiresAt = a.TokenExpiresAt,
            createdAt = a.CreatedAt,
            message,
        };
    }

    /// <summary>
    /// 判断模型名称与额度窗口标识是否匹配（支持前缀、后缀变体与分层模型池匹配，如 gemini-3.7-flash-high 匹配 gemini-3.7-flash-tiered）。
    /// </summary>
    public static bool IsModelMatchingQuotaWindow(string windowId, string modelName)
    {
        if (string.IsNullOrWhiteSpace(windowId) || string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        if (string.Equals(windowId, modelName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var cleanWindow = NormalizeQuotaModelName(windowId);
        var cleanModel = NormalizeQuotaModelName(modelName);

        if (string.Equals(cleanWindow, cleanModel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (cleanModel.StartsWith(cleanWindow, StringComparison.OrdinalIgnoreCase) ||
            cleanWindow.StartsWith(cleanModel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeQuotaModelName(string name)
    {
        var trimmed = name.Trim();
        foreach (var suffix in new[] { "-tiered", "-thinking", "-high", "-medium", "-low", "-extra-low", "-preview", "-agent" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^suffix.Length];
            }
        }
        return trimmed;
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
    /// <summary>是否启用该模型映射；旧客户端省略时按 true 兼容。</summary>
    public bool? Selected { get; set; }
}
