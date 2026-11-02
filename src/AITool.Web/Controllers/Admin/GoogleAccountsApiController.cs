using AITool.Application.Networking;
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
/// Google 账号（Antigravity）管理接口：OAuth 登录（粘贴回调 URL）、凭证导入、
/// 额度查询、启停、编辑、删除与模型拉取。结构与 CodexApiController 对齐，
/// 走同一 OAuth 功能总开关。
/// </summary>
[Route("api/admin/google-accounts")]
[ServiceFilter(typeof(OAuthFeatureToggleAttribute))]
public sealed class GoogleAccountsApiController : ControllerBase
{
    // —— OAuth 会话暂存（state → 会话，TTL 10min）——
    private sealed record OAuthSession(string State, string AccountKind, DateTimeOffset ExpiresAt, NetworkOperationContext Network, bool UpdateNetworkPolicy,
        IReadOnlyDictionary<Guid, (string? AccessToken, string? RefreshToken, DateTimeOffset? LastRefreshAt)>? CredentialsAtStart);
    private static readonly ConcurrentDictionary<string, OAuthSession> Sessions = new();

    private readonly NetworkBoundOAuthClientFactory? _networkFactory;
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
        ILogger<GoogleAccountsApiController> logger,
        NetworkBoundOAuthClientFactory? networkFactory = null)
    {
        _networkFactory = networkFactory;
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
    public async Task<IActionResult> StartOAuth([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] GoogleStartOAuthRequest? req, CancellationToken ct = default)
    {
        try
        {
        CleanupExpiredSessions();
        var operation = await NetworkBoundOAuthClientFactory.PrepareOperationAsync(_networkFactory, req?.NetworkSelection, ct);
        var kind = GoogleAccountKinds.Normalize(req?.Kind);
        var session = _oauth.CreateSession();
        var url = _oauth.BuildAuthorizeUrl(kind, session);
        var credentialsAtStart = _dbContext is null ? null : (await _dbContext.GoogleAccounts.ToListAsync(ct))
            .ToDictionary(a => a.Id, a => (a.AccessToken, a.RefreshToken, a.LastRefreshAt));
        Sessions[session.State] = new OAuthSession(session.State, kind, DateTimeOffset.UtcNow.AddMinutes(10), operation, req?.NetworkSelection is not null, credentialsAtStart);
        return Ok(new { url, state = session.State, kind });
        }
        catch (NetworkPolicyException ex)
        {
            return BadRequest(new { code = ex.Code, message = ex.Message });
        }
    }

    /// <summary>完成 OAuth 登录：用户粘贴回调 URL，校验 state 后交换 token、探测项目/等级并建账号。</summary>
    [HttpPost("complete-oauth")]
    public async Task<IActionResult> CompleteOAuth([FromBody] GoogleCompleteOAuthRequest req, CancellationToken ct)
    {
        try
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
            using var network = _networkFactory?.CreateForOperation(session.Network, "login-exchange");
            tokens = await (network?.GoogleOAuth ?? _oauth).ExchangeCodeAsync(session.AccountKind, code, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google OAuth code exchange failed");
            return BadRequest(new { message = "授权码交换失败：" + ex.Message });
        }

        var input = await BuildProvisionInputAsync(session.AccountKind, tokens, req.DisplayName, projectId: null, ct, session.Network);
        var account = await _provisioner.ProvisionFromTokensAsync(input, ct, session.Network, session.UpdateNetworkPolicy,
            credentialsBeforeExchange: session.CredentialsAtStart);
        return Ok(ToSummary(account));
        }
        catch (NetworkPolicyException ex)
        {
            return BadRequest(new { code = ex.Code, message = ex.Message });
        }
    }

    /// <summary>导入 gcli2api 凭证 JSON（multipart 多文件 或 raw body 单文件；kind 指定接入方式）。</summary>
    [HttpPost("import-credential")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> ImportCredential([FromQuery] string? kind, [FromQuery] string? name, CancellationToken ct,
        [FromQuery] int? networkMode = null, [FromQuery] Guid? proxyProfileId = null)
    {
        try
        {
        var selection = NetworkBoundOAuthClientFactory.ImportSelection(networkMode, proxyProfileId);
        var importOperation = await NetworkBoundOAuthClientFactory.PrepareOperationAsync(_networkFactory, selection, ct);
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
            var credentialsUpdated = false;
            var unknownRefreshCompleted = false;
            try
            {
                var refreshToken = ExtractCredentialField(json, "refresh_token");
                if (string.IsNullOrWhiteSpace(refreshToken))
                {
                    failures.Add(new { fileName, error = "凭证缺少 refresh_token 字段" });
                    continue;
                }

                // File-provided identity is untrusted. An unknown token can only be sent
                // through the explicitly selected, frozen operation and must prove its identity.
                var matches = await _dbContext.GoogleAccounts.Where(a => a.RefreshToken == refreshToken).ToListAsync(ct);
                if (matches.Count > 1)
                    throw new NetworkPolicyException("account_identity_required", "Google account identity is ambiguous.");
                if (matches.Count == 0)
                {
                    if (selection is null)
                        throw new NetworkPolicyException("network_selection_required", "An explicit network selection is required for an unknown credential.");
                    using var unknownNetwork = _networkFactory!.CreateForOperation(importOperation, "credential-import");
                    using var unknownSafety = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    ct.ThrowIfCancellationRequested();
                    var unknownTokens = await unknownNetwork.GoogleOAuth.RefreshTokenAsync(accountKind, refreshToken, unknownSafety.Token);
                    unknownRefreshCompleted = true;
                    unknownTokens = unknownTokens with { RefreshToken = string.IsNullOrWhiteSpace(unknownTokens.RefreshToken) ? refreshToken : unknownTokens.RefreshToken };
                    if (string.IsNullOrWhiteSpace(unknownTokens.AccessToken))
                        throw new NetworkPolicyException("account_identity_required", "Unable to verify Google account identity.");
                    await _provisioner.ValidateImportOperationAsync(importOperation, ct);
                    var unknownInput = await BuildProvisionInputAsync(accountKind, unknownTokens, name,
                        ExtractCredentialField(json, "project_id"), ct, importOperation);
                    if (string.IsNullOrWhiteSpace(unknownInput.Email))
                        throw new NetworkPolicyException("account_identity_required", "Unable to verify Google account identity.");
                    var imported = await _provisioner.ProvisionFromTokensAsync(unknownInput, ct, importOperation, true,
                        unknownImportRefreshToken: refreshToken);
                    summaries.Add(ToSummary(imported));
                    continue;
                }
                var existing = matches[0];
                var importedRefreshToken = refreshToken;
                var operation = selection is null
                    ? importOperation with { Account = new AccountIdentity("google", existing.Id) }
                    : importOperation;
                using var refreshLease = await NetworkBoundOAuthClientFactory.LockCredentialsAsync("google", existing.Id, ct);
                var current = await _dbContext.GoogleAccounts.InSingleAsync(existing.Id)
                    ?? throw new KeyNotFoundException("Google account no longer exists.");
                // Recheck identity under the canonical lock. A waiting import of the stored
                // token must use the replacement if a concurrent refresh already rotated it.
                refreshToken = current.RefreshToken ?? throw new InvalidOperationException("Google account has no refresh_token.");
                var conflicts = await _dbContext.GoogleAccounts.Where(a => a.Id != existing.Id &&
                    (a.RefreshToken == refreshToken || a.RefreshToken == importedRefreshToken)).ToListAsync(ct);
                if (conflicts.Count != 0)
                    throw new NetworkPolicyException("account_identity_required", "Google account identity is ambiguous.");
                existing = current;
                var refreshKind = current.AccountKind;
                using var network = _networkFactory?.CreateForOperation(operation, "credential-import");
                // Once refresh starts, cancellation must not discard a successfully rotated credential.
                using var refreshSafety = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                ct.ThrowIfCancellationRequested();
                var tokens = await (network?.GoogleOAuth ?? _oauth).RefreshTokenAsync(refreshKind, refreshToken,
                    refreshSafety.Token);
                tokens = tokens with { RefreshToken = string.IsNullOrWhiteSpace(tokens.RefreshToken) ? refreshToken : tokens.RefreshToken };
                await _provisioner.PersistImportedRotationAsync(existing.Id, tokens);
                credentialsUpdated = true;
                // A policy failure cannot roll back the consumed token's replacement.
                await _provisioner.ValidateImportOperationAsync(operation, ct);
                var projectId = ExtractCredentialField(json, "project_id");
                var input = await BuildProvisionInputAsync(refreshKind, tokens, name, projectId, ct, operation);
                var publicationOperation = operation with { Account = new AccountIdentity("google", existing.Id) };
                var account = await _provisioner.ProvisionFromTokensAsync(input, ct, publicationOperation, selection is not null,
                    credentialsLocked: true);
                summaries.Add(ToSummary(account));
            }
            catch (Exception ex)
            {
                failures.Add(new { fileName, error = ex is NetworkPolicyException policy && policy.Code == "network_selection_required"
                        ? "An explicit network selection is required for an unknown credential."
                        : unknownRefreshCompleted
                            ? "Google 凭证导入失败；刷新令牌可能已轮换，如凭证失效请重新授权。"
                            : "Google 凭证导入失败，请检查凭证、账号身份和网络设置。",
                    code = (ex as NetworkPolicyException)?.Code, credentialsUpdated, networkPolicyUpdated = false });
            }
        }

        if (failures.Count > 0)
        {
            return StatusCode(207, new { successes = summaries, failures });
        }
        return Ok(new { successes = summaries });
        }
        catch (NetworkPolicyException ex)
        {
            return BadRequest(new { code = ex.Code, message = ex.Message });
        }
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
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        await client.Ado.BeginTranAsync();
        var committed = false;
        try
        {
        await client.Ado.ExecuteCommandAsync("UPDATE GoogleAccounts SET IsEnabled=IsEnabled WHERE 1=0;");
        var account = await client.Queryable<GoogleAccount>().InSingleAsync(id);
        if (account == null) { await client.Ado.CommitTranAsync(); return NotFound(new { message = "账号不存在" }); }
        var enabled = req?.Enabled ?? !account.IsEnabled;
        account.ManuallyDisabled = !enabled;
        account.IsEnabled = enabled && !account.RequiresReauth && !account.DisabledByFeatureToggle && !account.IsQuotaCooling;
        if (enabled && !account.RequiresReauth)
        {
            account.DisabledByUpstream = false;
        }
        await client.Updateable(account)
            .UpdateColumns(x => new { x.IsEnabled, x.ManuallyDisabled, x.DisabledByUpstream })
            .ExecuteCommandAsync(ct);

        var site = await client.Queryable<Domain.Sites.Site>().InSingleAsync(account.LinkedSiteId);
        if (site is not null && site.IsEnabled != account.IsEnabled)
        {
            site.IsEnabled = account.IsEnabled;
            await client.Updateable(site).UpdateColumns(x => new { x.IsEnabled }).ExecuteCommandAsync(ct);
        }

        await client.Ado.CommitTranAsync();
        committed = true;
        var cache = HttpContext.RequestServices.GetRequiredService<ProxyRequestMetadataCache>();
        cache.InvalidateRouteTargets();
        cache.InvalidateGoogleAccounts();
        var selectedModels = await GetSelectedModelsForSiteAsync(account.LinkedSiteId, ct);
        return Ok(ToSummary(account, selectedModels: selectedModels));
        }
        catch { if (!committed) await client.Ado.RollbackTranAsync(); throw; }
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
            using var refreshLease = await NetworkBoundOAuthClientFactory.LockCredentialsAsync("google", account.Id, ct);
            account = await _dbContext.GoogleAccounts.InSingleAsync(account.Id);
            if (account is null) return NotFound(new { message = "账号不存在" });
            using var network = _networkFactory?.CreateForAccount(new AccountIdentity("google", account.Id), "manual-token-refresh");
            using var refreshSafety = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var tokens = await (network?.GoogleOAuth ?? _oauth).RefreshTokenAsync(account.AccountKind, req.RefreshToken, refreshSafety.Token);
            tokens = tokens with { RefreshToken = string.IsNullOrWhiteSpace(tokens.RefreshToken) ? req.RefreshToken : tokens.RefreshToken };
            await _provisioner.PersistImportedRotationAsync(account.Id, tokens);
            var input = await BuildProvisionInputAsync(account.AccountKind, tokens, req.DisplayName, account.ProjectId, ct, new NetworkOperationContext(
                new AccountIdentity("google", account.Id), account.Id.ToString("N"), new(AITool.Domain.Operations.NetworkMode.Legacy)));
            account = await _provisioner.ProvisionFromTokensAsync(input, ct, new NetworkOperationContext(
                new AccountIdentity("google", account.Id), account.Id.ToString("N"), new(AITool.Domain.Operations.NetworkMode.Legacy)), credentialsLocked: true,
                updateDisplayName: !string.IsNullOrWhiteSpace(req.DisplayName));
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
        var credentialAccount = account;
        if (account.TokenExpiresAt is null || account.TokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
        {
            var refreshed = await _credentialRefreshService.RefreshForAccountAsync(account.Id, accessToken, ct);
            if (!string.IsNullOrWhiteSpace(refreshed))
            {
                accessToken = refreshed;
                // A newly returned access token must be paired with its persisted credentials.
                credentialAccount = await GetAccountAsync(id, ct);
            }
        }

        // Without a refresh, retain the original snapshot even if another request rotates credentials.
        var credentialSnapshot = credentialAccount is null ? null : new ForwardOAuthCredentialSnapshot("google", credentialAccount.Id, credentialAccount.LinkedSiteId, credentialAccount.AccessToken, credentialAccount.RefreshToken, credentialAccount.LastRefreshAt);
        IReadOnlyList<(string Slug, string DisplayName)> models;
        try
        {
            using var network = _networkFactory?.CreateForAccount(new AccountIdentity("google", account.Id), "models");
            models = await (network?.GoogleModels ?? _modelFetcher).FetchAsync(account.AccountKind, accessToken, ct);
        }
        catch (AITool.Infrastructure.Codex.ModelAccessTokenUnauthorizedException ex)
        {
            if (credentialSnapshot is not null)
                await HttpContext.RequestServices.GetRequiredService<ForwardOAuthInvalidationService>().InvalidateAsync(accessToken, credentialSnapshot, ct);
            return StatusCode(StatusCodes.Status401Unauthorized, new { message = ex.Message });
        }
        catch (Exception ex) when (
            ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden }
            // Compatibility with the previous GoogleModelFetcher error format only.
            || ex is InvalidOperationException && ex.Message.StartsWith("fetchAvailableModels returned 403:", StringComparison.Ordinal))
        {
            var disabled = credentialSnapshot is not null && await DisableForModelForbiddenAsync(accessToken, credentialSnapshot, ct);
            return StatusCode(StatusCodes.Status403Forbidden, new { message = disabled
                ? "Google 上游返回 403，已自动禁用该账号，请完成验证或确认账号权限后再手动启用"
                : "Google 上游返回 403，账号凭证已变更或账号不存在，未禁用当前账号" });
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

        var selections = NormalizeSelections(req.Models);
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

    /// <summary>
    /// 批量应用模型选择到全部 Google（Antigravity）账号：同厂商模型清单一致，
    /// 任一账号拉取一次、勾选结果应用到整组（与单账号导入同语义：选中建立/启用，未选中禁用）。
    /// 逐账号顺序应用，单账号失败不中断整组。
    /// </summary>
    [HttpPost("accounts/batch-apply-models")]
    public async Task<IActionResult> BatchApplyModels([FromBody] GoogleBatchApplyModelsRequest? req, CancellationToken ct)
    {
        if (req is null || req.SourceAccountId == Guid.Empty)
        {
            return BadRequest(new { message = "缺少源账号" });
        }
        var source = await GetAccountAsync(req.SourceAccountId, ct);
        if (source == null) return NotFound(new { message = "源账号不存在" });

        var selections = NormalizeSelections(req.Models);
        if (selections.Count == 0)
        {
            return BadRequest(new { message = "未收到模型清单" });
        }

        var members = await _dbContext.GoogleAccounts.ToListAsync(ct);
        var results = new List<GoogleBatchApplyResult>(members.Count);
        foreach (var member in members)
        {
            try
            {
                await _provisioner.SyncRemoteModelsAsync(member.LinkedSiteId, selections, ct);
                results.Add(new GoogleBatchApplyResult(member.Id, member.DisplayName, true, null));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new GoogleBatchApplyResult(member.Id, member.DisplayName, false, ex.Message));
            }
        }

        return Ok(new
        {
            results,
            succeeded = results.Count(r => r.Ok),
            failed = results.Count(r => !r.Ok),
        });
    }

    private static List<(string Slug, string DisplayName, bool Selected)> NormalizeSelections(List<GoogleImportModelItem>? rawList)
    {
        return (rawList ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m.RemoteModelName))
            .Select(m => (
                Slug: m.RemoteModelName!.Trim(),
                DisplayName: string.IsNullOrWhiteSpace(m.DisplayName) ? m.RemoteModelName.Trim() : m.DisplayName!.Trim(),
                Selected: m.Selected ?? true))
            .GroupBy(m => m.Slug, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
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
        CancellationToken ct, NetworkOperationContext? operation = null)
    {
        using var network = operation is null ? null : _networkFactory?.CreateForOperation(operation, "account-metadata");
        var oauth = network?.GoogleOAuth ?? _oauth;
        var kind = GoogleAccountKinds.Normalize(accountKind);
        var profile = await oauth.LoadCodeAssistProfileAsync(kind, tokens.AccessToken, ct);
        projectId ??= profile.ProjectId;
        var tier = profile.Tier;
        var creditAmount = profile.CreditAmount;

        var email = await oauth.GetUserEmailAsync(tokens.AccessToken, ct);

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

    // An upstream 403 applies only to the complete credential version sent. Guard
    // the account and site update atomically, including refresh-only rotations.
    private async Task<bool> DisableForModelForbiddenAsync(string sentToken, ForwardOAuthCredentialSnapshot snapshot, CancellationToken ct)
    {
        if (!string.Equals(sentToken, snapshot.AccessToken, StringComparison.Ordinal)) return false;
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        await client.Ado.BeginTranAsync();
        int changed;
        try
        {
            changed = await client.Updateable<GoogleAccount>()
                .SetColumns(a => new GoogleAccount { IsEnabled = false, DisabledByUpstream = true })
                .Where(a => a.Id == snapshot.AccountId && a.LinkedSiteId == snapshot.SiteId
                    && a.AccessToken == snapshot.AccessToken && a.RefreshToken == snapshot.RefreshToken
                    && a.LastRefreshAt == snapshot.LastRefreshAt)
                .ExecuteCommandAsync(ct);
            if (changed != 0)
                await client.Updateable<Domain.Sites.Site>().SetColumns(s => new Domain.Sites.Site { IsEnabled = false })
                    .Where(s => s.Id == snapshot.SiteId).ExecuteCommandAsync(ct);
            await client.Ado.CommitTranAsync();
        }
        catch
        {
            await client.Ado.RollbackTranAsync();
            throw;
        }
        if (changed != 0)
        {
            var cache = HttpContext.RequestServices.GetRequiredService<ProxyRequestMetadataCache>();
            cache.InvalidateRouteTargets();
            cache.InvalidateGoogleAccounts();
        }
        return changed != 0;
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
                // Antigravity 真实额度只有两桶（Claude/GPT-OSS 系列、Gemini 系列），
                // 解析层已按前缀聚合，直接透出，不再按勾选模型过滤。
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
            disabledByUpstream = a.DisabledByUpstream,
            requiresReauth = a.RequiresReauth,
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
    public AccountNetworkSelection? NetworkSelection { get; set; }
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

public sealed class GoogleBatchApplyModelsRequest
{
    public Guid SourceAccountId { get; set; }
    public List<GoogleImportModelItem>? Models { get; set; }
}

public sealed record GoogleBatchApplyResult(Guid AccountId, string? DisplayName, bool Ok, string? Error);

public sealed class GoogleImportModelItem
{
    public string? RemoteModelName { get; set; }
    public string? DisplayName { get; set; }
    /// <summary>是否启用该模型映射；旧客户端省略时按 true 兼容。</summary>
    public bool? Selected { get; set; }
}
