using AppVersionInfo = AITool.Infrastructure.Hosting.AppVersionInfo;
using AITool.Application.Common;
using AITool.Application.Operations;
using AITool.Admin.Services;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// 后台认证 API：登录、刷新 token、首次设密码、登出、登录状态查询。
/// <para>
/// 认证采用 JWT：登录成功后签发 access + refresh token 对，前端在后续请求中携带
/// <c>Authorization: Bearer {access}</c>。access 过期后用 refresh 换新。
/// </para>
/// <para>
/// 密码哈希兼容新旧两种格式：旧 MD5 在登录成功后透明升级为 PBKDF2。
/// </para>
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public sealed class AuthApiController : ControllerBase
{
    /// <summary>
    /// 后台认证服务（密码校验/设置）。
    /// </summary>
    private readonly AdminAuthService _adminAuthService;
    private readonly JwtTokenService _tokenService;
    private readonly ISystemRuntimeSettingsService _settingsService;
    private readonly LoginRateLimitService _rateLimiter;
    /// <summary>
    /// 当前应用版本号与编译时间，用于在 status 接口返回给前端展示。
    /// </summary>
    private readonly AppVersionInfo _appVersion;

    public AuthApiController(
        AdminAuthService adminAuthService,
        JwtTokenService tokenService,
        ISystemRuntimeSettingsService settingsService,
        LoginRateLimitService rateLimiter,
        AppVersionInfo appVersion)
    {
        _adminAuthService = adminAuthService;
        _tokenService = tokenService;
        _settingsService = settingsService;
        _rateLimiter = rateLimiter;
        _appVersion = appVersion;
    }

    /// <summary>
    /// 查询当前登录状态与功能开关（前端用于决定显示登录页还是后台、菜单显隐）。
    /// 该端点不需要认证（即使未登录也可调用）。
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        var hasPassword = _adminAuthService.HasPasswordConfigured();
        var isAuthenticated = User.Identity?.IsAuthenticated == true;

        // 即使未配置密码也读取设置（首次访问时表已通过 EnsureCreated 建好）。
        var settings = await _settingsService.GetOrCreateAsync(cancellationToken);

        var payload = new
        {
            hasPassword,
            isAuthenticated,
            // 版本号与编译时间：前端右上角展示，便于确认运行的程序是否是最新版本。
            version = _appVersion.Value,
            buildTime = _appVersion.BuildTime,
            features = new
            {
                oauthEnabled = settings.OAuthFeaturesEnabled,
                oauthInspectionEnabled = settings.OAuthInspectionEnabled,
                developerEnabled = settings.DeveloperFeaturesEnabled
            }
        };

        // 该端点直接返回数据对象（非 ApiResponse 包装），因为它面向登录前场景，
        // 前端在最早期就需要读取它判断是否进登录页，统一包装反而增加耦合。
        return Ok(payload);
    }

    /// <summary>
    /// 登录：校验密码，成功后签发 access + refresh token。
    /// 若密码是旧 MD5 格式，校验通过后透明升级为 PBKDF2。
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var clientIp = GetClientIp();

        // 暴力破解防护：检查 IP 是否被锁定
        if (clientIp != null)
        {
            var lockRemaining = _rateLimiter.CheckLocked(clientIp);
            if (lockRemaining != null)
            {
                return Ok(ApiResponse.Fail($"登录失败次数过多，请 {lockRemaining} 秒后再试", "rate_limited"));
            }
        }

        if (string.IsNullOrWhiteSpace(request?.Password))
        {
            return BadRequest(ApiResponse.Fail("密码不能为空", "password_required"));
        }

        if (!_adminAuthService.HasPasswordConfigured())
        {
            return BadRequest(ApiResponse.Fail("尚未设置后台密码，请先完成初始化设置", "setup_required"));
        }

        if (!_adminAuthService.VerifyPassword(request.Password, out var needsUpgrade))
        {
            // 记录失败
            if (clientIp != null) _rateLimiter.RecordFailure(clientIp);
            return Ok(ApiResponse.Fail("密码错误", "invalid_credentials"));
        }

        // 登录成功：清除失败记录
        if (clientIp != null) _rateLimiter.RecordSuccess(clientIp);

        // 旧 MD5 密码透明升级为 PBKDF2。
        if (needsUpgrade)
        {
            try
            {
                await _adminAuthService.UpgradePasswordAsync(request.Password, cancellationToken);
            }
            catch
            {
                // 升级失败不影响本次登录（密码已验证通过），下次登录会再次尝试。
            }
        }

        var tokens = _tokenService.IssueTokens(subjectId: "admin");
        return Ok(ApiResponse.Ok(new
        {
            accessToken = tokens.AccessToken,
            refreshToken = tokens.RefreshToken,
            accessTokenExpiresAt = tokens.AccessTokenExpiresAt,
            refreshTokenExpiresAt = tokens.RefreshTokenExpiresAt
        }, "登录成功"));
    }

    /// <summary>
    /// 用 refresh token 换发新的 access + refresh token。
    /// 旧 refresh token 立即作废（轮换）。
    /// </summary>
    [HttpPost("refresh")]
    public IActionResult Refresh([FromBody] RefreshRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.RefreshToken))
        {
            return BadRequest(ApiResponse.Fail("refreshToken 不能为空", "refresh_token_required"));
        }

        var tokens = _tokenService.Refresh(request.RefreshToken);
        if (tokens is null)
        {
            return Ok(ApiResponse.Fail("refresh token 无效或已过期，请重新登录", "invalid_refresh_token"));
        }

        return Ok(ApiResponse.Ok(new
        {
            accessToken = tokens.AccessToken,
            refreshToken = tokens.RefreshToken,
            accessTokenExpiresAt = tokens.AccessTokenExpiresAt,
            refreshTokenExpiresAt = tokens.RefreshTokenExpiresAt
        }));
    }

    /// <summary>
    /// 登出：吊销当前 refresh token。access token 无状态无法主动吊销，会自然过期。
    /// </summary>
    [HttpPost("logout")]
    public IActionResult Logout([FromBody] LogoutRequest? request)
    {
        if (!string.IsNullOrWhiteSpace(request?.RefreshToken))
        {
            _tokenService.Revoke(request.RefreshToken);
        }
        return Ok(ApiResponse.Ok("已登出"));
    }

    /// <summary>
    /// 首次设置后台密码（仅在尚未配置密码时可用）。设置成功后自动签发 token，等同于登录。
    /// </summary>
    [HttpPost("setup")]
    public async Task<IActionResult> Setup([FromBody] SetupRequest request, CancellationToken cancellationToken)
    {
        if (_adminAuthService.HasPasswordConfigured())
        {
            return BadRequest(ApiResponse.Fail("后台密码已设置，如需修改请联系管理员", "already_setup"));
        }

        if (string.IsNullOrWhiteSpace(request?.Password))
        {
            return BadRequest(ApiResponse.Fail("密码不能为空", "password_required"));
        }

        if (request.Password.Length < 6)
        {
            return BadRequest(ApiResponse.Fail("密码长度至少 6 位", "password_too_short"));
        }

        if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
        {
            return BadRequest(ApiResponse.Fail("两次输入的密码不一致", "password_mismatch"));
        }

        await _adminAuthService.SetPasswordAsync(request.Password, cancellationToken);

        var tokens = _tokenService.IssueTokens(subjectId: "admin");
        return Ok(ApiResponse.Ok(new
        {
            accessToken = tokens.AccessToken,
            refreshToken = tokens.RefreshToken,
            accessTokenExpiresAt = tokens.AccessTokenExpiresAt,
            refreshTokenExpiresAt = tokens.RefreshTokenExpiresAt
        }, "密码设置成功"));
    }

    /// <summary>
    /// 获取客户端真实 IP。
    /// 仅当直连 IP 是回环地址（说明前面有反向代理）时才信任 X-Forwarded-For，
    /// 防止攻击者伪造该头绕过暴力破解防护。
    /// </summary>
    private string? GetClientIp()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (remoteIp is null) return null;

        // 只有请求来自本地（反向代理场景）才信任 X-Forwarded-For
        if (System.Net.IPAddress.IsLoopback(remoteIp))
        {
            var forwarded = Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                return forwarded.Split(',')[0].Trim();
            }
        }

        return remoteIp.ToString();
    }
}

/// <summary>
/// 登录请求。
/// </summary>
public sealed class LoginRequest
{
    /// <summary>
    /// 明文密码。
    /// </summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// 首次设置密码请求。
/// </summary>
public sealed class SetupRequest
{
    /// <summary>
    /// 明文密码。
    /// </summary>
    public string Password { get; set; } = string.Empty;
    /// <summary>
    /// 确认密码（需与 Password 一致）。
    /// </summary>
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>
/// 刷新 token 请求。
/// </summary>
public sealed class RefreshRequest
{
    /// <summary>
    /// 之前签发的 refresh token。
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;
}

/// <summary>
/// 登出请求。
/// </summary>
public sealed class LogoutRequest
{
    /// <summary>
    /// 要吊销的 refresh token。
    /// </summary>
    public string? RefreshToken { get; set; }
}
