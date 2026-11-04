using AITool.Application.OAuth;
using AITool.Application.Networking;
using AITool.Domain.Operations;
using AITool.Domain.OAuth;
using AITool.Infrastructure.Persistence;
using AITool.Web.Contracts;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 通用轮询式 OAuth 账号管理 API：设备码/授权链接登录（发起+轮询）、账号列表、启停、
/// 更新、删除、额度刷新。多厂商共用（?vendor=minimax-cn / minimax-global / …）。
/// </summary>
[ApiController]
[Route("api/admin/device-oauth")]
[ServiceFilter(typeof(OAuthFeatureToggleAttribute))]
public sealed class DeviceOAuthAccountsApiController : ControllerBase
{
    /// <summary>进行中的登录会话（授权地址发起后前端轮询；10 分钟过期，机会式清理）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingSession> Sessions = new();

    private readonly DeviceOAuthVendorRegistry _registry;
    private readonly DeviceOAuthAccountProvisioner _provisioner;
    private readonly DeviceOAuthQuotaService _quotaService;
    private readonly AppDbContext _dbContext;
    private readonly NetworkBoundDeviceOAuthVendorFactory? _networkVendors;

    public DeviceOAuthAccountsApiController(
        DeviceOAuthVendorRegistry registry,
        DeviceOAuthAccountProvisioner provisioner,
        DeviceOAuthQuotaService quotaService,
        AppDbContext dbContext,
        NetworkBoundDeviceOAuthVendorFactory? networkVendors = null)
    {
        _registry = registry;
        _provisioner = provisioner;
        _quotaService = quotaService;
        _dbContext = dbContext;
        _networkVendors = networkVendors;
    }

    /// <summary>可登录的厂商列表。</summary>
    [HttpGet("vendors")]
    public IActionResult GetVendors()
    {
        return Ok(ApiResponse.Ok(_registry.All.Select(v => new
        {
            vendorKey = v.VendorKey,
            label = v.Label,
            chatSupported = v.ChatSiteConfig is not null,
            modelFetchSupported = v.SupportsModelFetch,
            modelCatalogScope = v.ModelCatalogScope == DeviceOAuthModelCatalogScope.PublicCatalog ? "public" : "account",
        })));
    }

    /// <summary>发起登录：返回授权地址与轮询凭据。</summary>
    [HttpPost("sign-in/start")]
    public async Task<IActionResult> StartSignIn([FromBody] StartSignInRequest request, CancellationToken ct)
    {
        var vendor = _registry.Find(request.Vendor);
        if (vendor is null)
        {
            return BadRequest(ApiResponse.Fail($"未知厂商 {request.Vendor}", "unknown_vendor"));
        }

        var sessionId = Guid.NewGuid().ToString("N");
        NetworkOperationContext? network = null;
        DeviceOAuthStartResult start;
        try
        {
            if (_networkVendors is null && request.NetworkSelection is not null)
                throw new NetworkPolicyException("network_policy_unavailable", "Account network binding is unavailable.");
            if (_networkVendors is not null)
                network = await _networkVendors.CreateOperationAsync(request.NetworkSelection, "device-oauth-login:" + sessionId, ct);
            using var boundVendor = network is null ? null : _networkVendors!.ForOperation(vendor.VendorKey, network, "device-oauth-login");
            start = await (boundVendor?.Vendor ?? vendor).StartSignInAsync(ct);
        }
        catch (NetworkPolicyException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message, ex.Code));
        }
        catch (Exception ex)
        {
            // 发码失败（网络不可达/上游拒绝/响应畸形）返回业务错误而非无信息的 500。
            return Ok(ApiResponse.Fail($"发起登录失败：{ex.Message}", "sign_in_start_failed"));
        }

        Sessions[sessionId] = new PendingSession(vendor.VendorKey, start.SessionJson,
            DateTimeOffset.UtcNow.AddMinutes(15), network, request.NetworkSelection);
        PruneExpiredSessions();

        return Ok(ApiResponse.Ok(new
        {
            sessionId,
            authorizeUrl = start.AuthorizeUrl,
            userCode = start.UserCode,
            instructions = start.Instructions,
        }));
    }

    /// <summary>轮询登录结果：完成即落库建号。</summary>
    [HttpPost("sign-in/poll")]
    public async Task<IActionResult> PollSignIn([FromBody] PollSignInRequest request, CancellationToken ct)
    {
        if (!Sessions.TryGetValue(request.SessionId ?? string.Empty, out var session) || session.DeadlineUtc < DateTimeOffset.UtcNow)
        {
            Sessions.TryRemove(request.SessionId ?? string.Empty, out _);
            return BadRequest(ApiResponse.Fail("登录会话已过期，请重新发起", "session_expired"));
        }

        await session.Gate.WaitAsync(ct);
        try
        {
        // Waiters must recheck after taking the lease: only one caller may consume a code.
        if (!Sessions.TryGetValue(request.SessionId!, out var active) || !ReferenceEquals(active, session)
            || session.DeadlineUtc < DateTimeOffset.UtcNow)
            return BadRequest(ApiResponse.Fail("登录会话已过期，请重新发起", "session_expired"));
        if (session.CompletedAccountId is { } completed)
            return Ok(ApiResponse.Ok(new { status = "success", accountId = completed }));

        var vendor = _registry.Find(session.VendorKey);
        if (vendor is null)
        {
            return BadRequest(ApiResponse.Fail("厂商未注册", "unknown_vendor"));
        }

        DeviceOAuthPollResult poll;
        // 粘贴回调流（如 Claude）：带 callbackUrl 时走 CompleteWithCallback，否则轮询。
        // 厂商任何异常（网络错误、上游拒绝、响应畸形）统一兜底为登录失败，不向上抛 500。
        try
        {
            if (session.Network is not null && _networkVendors is null)
                throw new NetworkPolicyException("network_policy_unavailable", "Account network binding is unavailable.");
            using var boundVendor = session.Network is null ? null
                : _networkVendors!.ForOperation(session.VendorKey, session.Network, "device-oauth-login");
            var remoteVendor = boundVendor?.Vendor ?? vendor;
            poll = session.SuccessfulPoll ?? (!string.IsNullOrWhiteSpace(request.CallbackUrl)
                ? await remoteVendor.CompleteWithCallbackAsync(session.SessionJson, request.CallbackUrl!, ct)
                : await remoteVendor.PollSignInAsync(session.SessionJson, ct));
        }
        catch (NetworkPolicyException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message, ex.Code));
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message, "callback_not_supported"));
        }
        catch (DeviceOAuthRefreshRefusedException ex)
        {
            // 明确的鉴权拒绝是终态（授权码已消费，重试同一粘贴只会继续失败）。
            Sessions.TryRemove(request.SessionId ?? string.Empty, out _);
            return Ok(ApiResponse.Ok(new { status = "failed", error = ex.Message }));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("会话无效", StringComparison.Ordinal))
        {
            Sessions.TryRemove(request.SessionId ?? string.Empty, out _);
            return Ok(ApiResponse.Ok(new { status = "failed", error = ex.Message }));
        }
        catch (Exception ex)
        {
            // 暂时性错误（网络抖动等）保留会话：用户可继续点「检查登录状态」重试，不必重新发起。
            return Ok(ApiResponse.Ok(new { status = "pending", message = $"暂时出错，可重试：{ex.Message}" }));
        }
        if (poll.IsPending)
        {
            // 多段登录续跳（如 Kiro 的 AWS Builder ID）：更新会话并引导用户打开下一地址。
            if (!string.IsNullOrWhiteSpace(poll.UpdatedSessionJson))
            {
                session.SessionJson = poll.UpdatedSessionJson!;
            }

            return Ok(ApiResponse.Ok(new
            {
                status = "pending",
                nextUrl = poll.NextAuthorizeUrl,
                message = poll.Error,
            }));
        }

        if (poll.Tokens is null || poll.IsDenied || poll.IsExpired || !string.IsNullOrEmpty(poll.Error))
        {
            Sessions.TryRemove(request.SessionId ?? string.Empty, out _);
            return Ok(ApiResponse.Ok(new
            {
                status = "failed",
                error = poll.Error ?? (poll.IsDenied ? "用户拒绝了授权" : poll.IsExpired ? "登录码已过期" : "登录失败"),
            }));
        }

        // Preserve consumed authorization results if a DB/policy write fails; retries must not
        // exchange the same one-time code again. Nothing is published until provision commits.
        session.SuccessfulPoll = poll;
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? poll.DisplayName ?? poll.Email
            : request.DisplayName!.Trim();
        var account = await _provisioner.ProvisionAsync(
            vendor, displayName, poll.Email, poll.ExternalUserId, poll.Tokens, poll.ExtraJson, ct,
            // 请求体里用户明确命名才算自定义名；上游昵称/邮箱兜底名跟随重登刷新。
            isUserSuppliedName: !string.IsNullOrWhiteSpace(request.DisplayName),
            networkSelection: session.Selection,
            expectedProfileRevision: session.Network?.ExpectedProfileRevision);
        session.CompletedAccountId = account.Id;

        return Ok(ApiResponse.Ok(new
        {
            status = "success",
            accountId = account.Id,
        }));
        }
        catch (NetworkPolicyException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message, ex.Code));
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>账号列表（含额度缓存窗口）。</summary>
    [HttpGet("accounts")]
    public async Task<IActionResult> GetAccounts(CancellationToken ct)
    {
        var accounts = await _dbContext.DeviceOAuthAccounts
            .Where(a => !a.IsDeleted)
            .OrderBy(a => a.VendorKey)
            .ThenBy(a => a.CreatedAt)
            .ToListAsync(ct);

        var linkedSiteIds = accounts.Select(a => a.LinkedSiteId).Where(id => id != Guid.Empty).Distinct().ToList();
        var existingSiteIds = linkedSiteIds.Count == 0 ? new HashSet<Guid>()
            : (await _dbContext.Sites.Where(s => linkedSiteIds.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct)).ToHashSet();
        return Ok(ApiResponse.Ok(accounts.Select(a => ToSummary(a, existingSiteIds.Contains(a.LinkedSiteId))).ToList()));
    }

    /// <summary>启停账号。</summary>
    [HttpPost("accounts/{id:guid}/toggle")]
    public async Task<IActionResult> ToggleAccount(Guid id, [FromBody] ToggleRequest? request, CancellationToken ct)
    {
        await _provisioner.ToggleAsync(id, request?.Enabled ?? false, ct);
        return Ok(ApiResponse.Ok("已更新"));
    }

    /// <summary>更新展示名/refresh_token。</summary>
    [HttpPut("accounts/{id:guid}")]
    public async Task<IActionResult> UpdateAccount(Guid id, [FromBody] UpdateAccountRequest request, CancellationToken ct)
    {
        await _provisioner.UpdateAsync(id, request.DisplayName, request.RefreshToken, ct);
        return Ok(ApiResponse.Ok("已更新"));
    }

    /// <summary>手动刷新额度。</summary>
    [HttpPost("accounts/{id:guid}/refresh-quota")]
    public async Task<IActionResult> RefreshQuota(Guid id, CancellationToken ct)
    {
        var snapshot = await _quotaService.ForceRefreshAsync(id, ct);
        return Ok(ApiResponse.Ok(new
        {
            success = snapshot.Success,
            error = snapshot.Error,
            planType = snapshot.PlanType,
            windows = snapshot.Windows,
        }));
    }

    /// <summary>拉取账号上游模型目录（含既有映射状态，供选择导入）。</summary>
    [HttpGet("accounts/{id:guid}/fetch-models")]
    public async Task<IActionResult> FetchModels(Guid id, CancellationToken ct)
    {
        var account = await _dbContext.DeviceOAuthAccounts.InSingleAsync(id);
        if (account is null || account.IsDeleted)
        {
            return NotFound(ApiResponse.Fail("账号不存在", "account_not_found"));
        }

        var vendor = _registry.Find(account.VendorKey);
        if (vendor is null || !vendor.SupportsModelFetch)
        {
            return BadRequest(ApiResponse.Fail("该厂商不支持拉取模型", "not_supported"));
        }

        var initialSnapshot = new ForwardOAuthCredentialSnapshot("device-oauth", account.Id, account.LinkedSiteId, account.AccessToken, account.RefreshToken, account.LastRefreshAt);
        IReadOnlyList<Application.OAuth.DeviceOAuthRemoteModel> models;
        try
        {
            var refresh = HttpContext.RequestServices.GetRequiredService<DeviceOAuthCredentialRefreshService>();
            DeviceOAuthTokenSet token;
            try
            {
                token = await refresh.EnsureFreshAsync(id, false, ct);
            }
            catch (DeviceOAuthRefreshRefusedException)
            {
                HttpContext.RequestServices.GetRequiredService<ILogger<DeviceOAuthAccountsApiController>>()
                    .LogWarning("Device OAuth refresh rejected for account {AccountId}", id);
                await HttpContext.RequestServices.GetRequiredService<ForwardOAuthInvalidationService>()
                    .InvalidateAsync(initialSnapshot.AccessToken ?? string.Empty, initialSnapshot, ct);
                return Conflict(ApiResponse.Fail("账号需要重新授权", "oauth_reauthorization_required"));
            }
            catch (KeyNotFoundException)
            {
                return NotFound(ApiResponse.Fail("账号不存在", "account_not_found"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not NetworkPolicyException)
            {
                // Refresh errors may include an upstream response body containing secrets.
                return StatusCode(StatusCodes.Status503ServiceUnavailable, ApiResponse.Fail("账号令牌刷新暂时失败，请稍后重试", "oauth_refresh_unavailable"));
            }
            account = await _dbContext.DeviceOAuthAccounts.InSingleAsync(id);
            if (account is null || account.IsDeleted) return NotFound(ApiResponse.Fail("账号不存在", "account_not_found"));
            if (account.LinkedSiteId != initialSnapshot.SiteId
                || !string.Equals(account.AccessToken, token.AccessToken, StringComparison.Ordinal)
                || !string.Equals(account.RefreshToken, token.RefreshToken, StringComparison.Ordinal))
                return StatusCode(StatusCodes.Status503ServiceUnavailable, ApiResponse.Fail("账号凭据已变更，请重试", "oauth_refresh_unavailable"));
            var snapshot = new ForwardOAuthCredentialSnapshot("device-oauth", account.Id, account.LinkedSiteId, token.AccessToken, token.RefreshToken, account.LastRefreshAt);
            using var boundVendor = _networkVendors?.ForAccount(account, "device-oauth-models");
            models = await ModelCredentialRecovery.FetchAsync(_dbContext, HttpContext.RequestServices, snapshot,
                async accessToken =>
                {
                    var current = await _dbContext.DeviceOAuthAccounts.InSingleAsync(account.Id);
                    var credentials = new Application.OAuth.DeviceOAuthTokenSet
                    {
                        AccessToken = accessToken,
                        RefreshToken = accessToken != token.AccessToken && current?.AccessToken == accessToken ? current.RefreshToken : token.RefreshToken,
                        ExpiresAt = accessToken != token.AccessToken && current?.AccessToken == accessToken ? current.TokenExpiresAt : token.ExpiresAt,
                    };
                    var extraJson = accessToken != token.AccessToken && current?.AccessToken == accessToken ? current.ExtraJson : account.ExtraJson;
                    return await (boundVendor?.Vendor ?? vendor).FetchModelsAsync(credentials, extraJson, ct);
                }, ct);
        }
        catch (AITool.Infrastructure.Codex.ModelAccessTokenUnauthorizedException)
        {
            HttpContext.RequestServices.GetRequiredService<ILogger<DeviceOAuthAccountsApiController>>()
                .LogWarning("Device OAuth model credentials rejected for account {AccountId}", id);
            return Conflict(ApiResponse.Fail("账号凭据被上游拒绝，请重新授权", "oauth_reauthorization_required"));
        }
        catch (DeviceOAuthModelRefreshUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ApiResponse.Fail("账号令牌刷新暂时失败，请稍后重试", "oauth_refresh_unavailable"));
        }
        catch (NotSupportedException)
        {
            // 同一能力位可能在部署尚未提供实现；不可回退到静态 DefaultModels 冒充远端目录。
            return BadRequest(ApiResponse.Fail("该厂商尚未实现动态模型拉取", "fetch_not_implemented"));
        }

        // 既有映射状态合并进条目（existingDisplayName 供前端展示别名）。
        // 无站点厂商也可查看远端目录，但不能把 Guid.Empty 下的孤儿映射视为本账号映射。
        var hasSite = vendor.ChatSiteConfig is not null && account.LinkedSiteId != Guid.Empty
            && await _dbContext.Sites.InSingleAsync(account.LinkedSiteId) is not null;
        var mappings = hasSite
            ? await _dbContext.SiteModelMappings.Where(m => m.SiteId == account.LinkedSiteId).ToListAsync(ct)
            : new List<AITool.Domain.SiteCatalog.SiteModelMapping>();
        var mappingsByName = mappings.ToLookup(m => m.RemoteModelName, StringComparer.OrdinalIgnoreCase);
        var itemIds = mappings.Select(m => m.ModelLibraryItemId).Distinct().ToList();
        var items = itemIds.Count == 0 ? new Dictionary<Guid, AITool.Domain.Models.ModelLibraryItem>()
            : (await _dbContext.ModelLibraryItems.Where(m => itemIds.Contains(m.Id)).ToListAsync(ct)).ToDictionary(m => m.Id);

        var result = models.Where(m => !string.IsNullOrWhiteSpace(m.Slug))
            .Select(m => m with { Slug = m.Slug.Trim() })
            .DistinctBy(m => m.Slug, StringComparer.OrdinalIgnoreCase).Select(m =>
        {
            var mapping = mappingsByName[m.Slug].FirstOrDefault();
            var itemName = mapping is not null && items.TryGetValue(mapping.ModelLibraryItemId, out var it) ? it.ModelName : null;
            return new
            {
                remoteModelName = m.Slug,
                displayName = m.DisplayName,
                existingMappingId = mapping is null ? (Guid?)null : mapping.Id,
                isEnabled = mapping is not null && mapping.IsEnabled,
                existingDisplayName = itemName,
            };
        }).ToList();

        return Ok(ApiResponse.Ok(result));
    }

    /// <summary>按选择同步模型映射（选中建立/启用，未选中禁用）。</summary>
    [HttpPost("accounts/{id:guid}/import-models")]
    public async Task<IActionResult> ImportModels(Guid id, [FromBody] ImportDeviceModelsRequest? request, CancellationToken ct)
    {
        var account = await _dbContext.DeviceOAuthAccounts.InSingleAsync(id);
        if (account is null || account.IsDeleted)
        {
            return NotFound(ApiResponse.Fail("账号不存在", "account_not_found"));
        }
        var vendor = _registry.Find(account.VendorKey);
        if (vendor is null || !vendor.SupportsModelFetch)
        {
            return BadRequest(ApiResponse.Fail("该厂商不支持拉取模型", "not_supported"));
        }
        if (vendor.ChatSiteConfig is null || account.LinkedSiteId == Guid.Empty
            || await _dbContext.Sites.InSingleAsync(account.LinkedSiteId) is null)
        {
            return BadRequest(ApiResponse.Fail("该账号无关联聊天站点，模型目录仅供查看", "model_import_not_supported"));
        }

        var selections = (request?.Models ?? [])
            .Where(m => m is not null && !string.IsNullOrWhiteSpace(m.RemoteModelName))
            .Select(m => (m.RemoteModelName!.Trim(), m.DisplayName ?? string.Empty, m.Selected))
            .ToList();
        if (selections.Count == 0)
        {
            return BadRequest(ApiResponse.Fail("未收到模型清单", "empty_models"));
        }
        try
        {
            var changed = await _provisioner.SyncSelectedModelsAsync(id, selections, ct);
            return Ok(ApiResponse.Ok(new { changed }));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(ApiResponse.Fail("账号不存在", "account_not_found"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message, "model_import_not_supported"));
        }
    }

    /// <summary>
    /// 仅对源账号应用选择；其他账号权限无法从源账号清单推断，跳过并报告风险。
    /// </summary>
    [HttpPost("accounts/batch-apply-models")]
    public async Task<IActionResult> BatchApplyModels([FromBody] BatchApplyDeviceModelsRequest? request, CancellationToken ct)
    {
        if (request is null || request.SourceAccountId == Guid.Empty)
        {
            return BadRequest(ApiResponse.Fail("缺少源账号", "missing_source"));
        }
        var source = await _dbContext.DeviceOAuthAccounts.InSingleAsync(request.SourceAccountId);
        if (source is null || source.IsDeleted)
        {
            return NotFound(ApiResponse.Fail("源账号不存在", "account_not_found"));
        }

        var selections = (request.Models ?? [])
            .Where(m => m is not null && !string.IsNullOrWhiteSpace(m.RemoteModelName))
            .Select(m => (m.RemoteModelName!.Trim(), m.DisplayName ?? string.Empty, m.Selected))
            .ToList();
        if (selections.Count == 0)
        {
            return BadRequest(ApiResponse.Fail("未收到模型清单", "empty_models"));
        }

        // 保留原有分组以逐项报告跳过的账号，同厂商不代表权限相同。
        var groupMembers = await _dbContext.DeviceOAuthAccounts
            .Where(a => !a.IsDeleted && a.VendorKey == source.VendorKey)
            .ToListAsync(ct);

        var results = new List<DeviceBatchApplyResult>(groupMembers.Count);
        foreach (var member in groupMembers)
        {
            if (member.Id != source.Id)
            {
                results.Add(new DeviceBatchApplyResult(member.Id, member.DisplayName, false, 0, "无法验证该账号的模型权限，已跳过；请逐账号拉取并同步模型"));
                continue;
            }
            try
            {
                var changed = await _provisioner.SyncSelectedModelsAsync(member.Id, selections, ct);
                results.Add(new DeviceBatchApplyResult(member.Id, member.DisplayName, true, changed, null));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new DeviceBatchApplyResult(member.Id, member.DisplayName, false, 0, ex.Message));
            }
        }

        return Ok(ApiResponse.Ok(new
        {
            results,
            succeeded = results.Count(r => r.Ok),
            failed = results.Count(r => !r.Ok),
        }));
    }

    /// <summary>删除账号（级联删除隐藏站点）。</summary>
    [HttpDelete("accounts/{id:guid}")]
    public async Task<IActionResult> DeleteAccount(Guid id, CancellationToken ct)
    {
        var account = await _dbContext.DeviceOAuthAccounts.InSingleAsync(id);
        if (account is null || account.IsDeleted)
        {
            return NotFound(ApiResponse.Fail("账号不存在", "account_not_found"));
        }
        await _provisioner.DeleteAsync(id, ct);
        return Ok(ApiResponse.Ok("已删除"));
    }

    private object ToSummary(DeviceOAuthAccount account, bool hasSite)
    {
        var vendor = _registry.Find(account.VendorKey);
        var cached = vendor?.ParseCachedQuota(account.LastQuotaRawJson ?? string.Empty);
        return new
        {
            id = account.Id,
            vendorKey = account.VendorKey,
            vendorLabel = vendor?.Label ?? account.VendorKey,
            displayName = account.DisplayName,
            email = account.Email,
            isEnabled = account.IsEnabled,
            manuallyDisabled = account.ManuallyDisabled,
            disabledByFeatureToggle = account.DisabledByFeatureToggle,
            requiresReauth = account.RequiresReauth,
            tokenExpiresAt = account.TokenExpiresAt,
            linkedSiteId = account.LinkedSiteId,
            modelImportSupported = vendor?.SupportsModelFetch == true && vendor.ChatSiteConfig is not null && hasSite,
            createdAt = account.CreatedAt,
            lastQuotaCheckedAt = account.LastQuotaCheckedAt,
            planType = cached?.PlanType,
            windows = cached?.Windows ?? [],
        };
    }

    private static void PruneExpiredSessions()
    {
        foreach (var pair in Sessions)
        {
            if (pair.Value.DeadlineUtc < DateTimeOffset.UtcNow)
            {
                Sessions.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class PendingSession(string vendorKey, string sessionJson, DateTimeOffset deadlineUtc,
        NetworkOperationContext? network, AccountNetworkSelection? selection)
    {
        public string VendorKey { get; } = vendorKey;
        public string SessionJson { get; set; } = sessionJson;
        public DateTimeOffset DeadlineUtc { get; } = deadlineUtc;
        public NetworkOperationContext? Network { get; } = network;
        public AccountNetworkSelection? Selection { get; } = selection;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Guid? CompletedAccountId { get; set; }
        public DeviceOAuthPollResult? SuccessfulPoll { get; set; }
    }

    public sealed class StartSignInRequest
    {
        public string? Vendor { get; set; }
        public AccountNetworkSelection? NetworkSelection { get; set; }
    }

    public sealed class PollSignInRequest
    {
        public string? SessionId { get; set; }
        public string? DisplayName { get; set; }
        /// <summary>粘贴回调流的完整回跳地址（如 Claude 的 localhost:54545/callback?code=…）。</summary>
        public string? CallbackUrl { get; set; }
    }

    public sealed class ToggleRequest
    {
        public bool Enabled { get; set; }
    }

    public sealed class DeviceModelSelectionItem
    {
        public string? RemoteModelName { get; set; }
        public string? DisplayName { get; set; }
        public bool Selected { get; set; }
    }

    public sealed class ImportDeviceModelsRequest
    {
        public List<DeviceModelSelectionItem> Models { get; set; } = new();
    }

    public sealed class BatchApplyDeviceModelsRequest
    {
        public Guid SourceAccountId { get; set; }
        public List<DeviceModelSelectionItem> Models { get; set; } = new();
    }

    public sealed record DeviceBatchApplyResult(Guid AccountId, string DisplayName, bool Ok, int Changed, string? Error);
    public sealed class UpdateAccountRequest
    {
        public string? DisplayName { get; set; }
        public string? RefreshToken { get; set; }
    }
}
