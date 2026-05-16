using System.Security.Claims;
using AITool.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages;

/// <summary>
/// 登录页面模型。
/// </summary>
public sealed class LoginModel : PageModel
{
    /// <summary>
    /// 后台认证服务。
    /// </summary>
    private readonly AdminAuthService _adminAuthService;

    /// <summary>
    /// 初始化登录页面模型。
    /// </summary>
    public LoginModel(AdminAuthService adminAuthService)
    {
        _adminAuthService = adminAuthService;
    }

    /// <summary>
    /// 登录密码。
    /// </summary>
    [BindProperty]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// 确认密码。
    /// </summary>
    [BindProperty]
    public string ConfirmPassword { get; set; } = string.Empty;

    /// <summary>
    /// 登录后的返回地址。
    /// </summary>
    public string ReturnUrl { get; private set; } = "/";

    /// <summary>
    /// 是否为首次设置模式。
    /// </summary>
    public bool IsSetupMode { get; private set; }

    /// <summary>
    /// 状态提示。
    /// </summary>
    public string StatusMessage { get; private set; } = string.Empty;

    /// <summary>
    /// 状态是否为错误。
    /// </summary>
    public bool StatusIsError { get; private set; }

    /// <summary>
    /// 处理页面加载请求。
    /// </summary>
    public IActionResult OnGet(string? returnUrl = null)
    {
        ReturnUrl = NormalizeReturnUrl(returnUrl);
        IsSetupMode = !_adminAuthService.HasPasswordConfigured();
        if (!IsSetupMode && User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(ReturnUrl);
        }

        return Page();
    }

    /// <summary>
    /// 处理登录提交。
    /// </summary>
    public async Task<IActionResult> OnPostLoginAsync(string? returnUrl = null)
    {
        ReturnUrl = NormalizeReturnUrl(returnUrl);
        IsSetupMode = !_adminAuthService.HasPasswordConfigured();
        if (IsSetupMode)
        {
            StatusIsError = true;
            StatusMessage = "请先设置后台登录密码。";
            return Page();
        }

        if (!_adminAuthService.VerifyPassword(Password))
        {
            StatusIsError = true;
            StatusMessage = "密码错误，请重试。";
            return Page();
        }

        await SignInAsync();
        return LocalRedirect(ReturnUrl);
    }

    /// <summary>
    /// 处理首次密码设置。
    /// </summary>
    public async Task<IActionResult> OnPostSetupAsync(string? returnUrl = null, CancellationToken cancellationToken = default)
    {
        ReturnUrl = NormalizeReturnUrl(returnUrl);
        IsSetupMode = !_adminAuthService.HasPasswordConfigured();
        if (!IsSetupMode)
        {
            return RedirectToPage(new { returnUrl = ReturnUrl });
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            StatusIsError = true;
            StatusMessage = "密码不能为空。";
            return Page();
        }

        if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
        {
            StatusIsError = true;
            StatusMessage = "两次输入的密码不一致。";
            return Page();
        }

        await _adminAuthService.SetPasswordAsync(Password, cancellationToken);
        await SignInAsync();
        return LocalRedirect(ReturnUrl);
    }

    /// <summary>
    /// 完成后台登录。
    /// </summary>
    private async Task SignInAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "admin")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    }

    /// <summary>
    /// 规范化返回地址。
    /// </summary>
    private string NormalizeReturnUrl(string? returnUrl)
    {
        return !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? returnUrl
            : "/";
    }
}
