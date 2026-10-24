using AITool.Application.OAuth;
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

    public DeviceOAuthAccountsApiController(
        DeviceOAuthVendorRegistry registry,
        DeviceOAuthAccountProvisioner provisioner,
        DeviceOAuthQuotaService quotaService,
        AppDbContext dbContext)
    {
        _registry = registry;
        _provisioner = provisioner;
        _quotaService = quotaService;
        _dbContext = dbContext;
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

        var start = await vendor.StartSignInAsync(ct);
        var sessionId = Guid.NewGuid().ToString("N");
        Sessions[sessionId] = new PendingSession(vendor.VendorKey, start.SessionJson, DateTimeOffset.UtcNow.AddMinutes(15));
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
            poll = !string.IsNullOrWhiteSpace(request.CallbackUrl)
                ? await vendor.CompleteWithCallbackAsync(session.SessionJson, request.CallbackUrl!, ct)
                : await vendor.PollSignInAsync(session.SessionJson, ct);
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
                Sessions[request.SessionId!] = new PendingSession(session.VendorKey, poll.UpdatedSessionJson!, session.DeadlineUtc);
            }

            return Ok(ApiResponse.Ok(new
            {
                status = "pending",
                nextUrl = poll.NextAuthorizeUrl,
                message = poll.Error,
            }));
        }

        Sessions.TryRemove(request.SessionId ?? string.Empty, out _);
        if (poll.Tokens is null || poll.IsDenied || poll.IsExpired || !string.IsNullOrEmpty(poll.Error))
        {
            return Ok(ApiResponse.Ok(new
            {
                status = "failed",
                error = poll.Error ?? (poll.IsDenied ? "用户拒绝了授权" : poll.IsExpired ? "登录码已过期" : "登录失败"),
            }));
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? poll.DisplayName ?? poll.Email
            : request.DisplayName!.Trim();
        var account = await _provisioner.ProvisionAsync(
            vendor, displayName, poll.Email, poll.ExternalUserId, poll.Tokens, poll.ExtraJson, ct);

        return Ok(ApiResponse.Ok(new
        {
            status = "success",
            accountId = account.Id,
        }));
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

        return Ok(ApiResponse.Ok(accounts.Select(ToSummary).ToList()));
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
        await _provisioner.UpdateAsync(id, request.DisplayName ?? string.Empty, request.RefreshToken, ct);
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

    /// <summary>删除账号（级联删除隐藏站点）。</summary>
    [HttpDelete("accounts/{id:guid}")]
    public async Task<IActionResult> DeleteAccount(Guid id, CancellationToken ct)
    {
        await _provisioner.DeleteAsync(id, ct);
        return Ok(ApiResponse.Ok("已删除"));
    }

    private object ToSummary(DeviceOAuthAccount account)
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

    private sealed record PendingSession(string VendorKey, string SessionJson, DateTimeOffset DeadlineUtc);

    public sealed class StartSignInRequest
    {
        public string? Vendor { get; set; }
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

    public sealed class UpdateAccountRequest
    {
        public string? DisplayName { get; set; }
        public string? RefreshToken { get; set; }
    }
}
