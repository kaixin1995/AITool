using System.Security.Claims;
using AITool.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages;

public sealed class LoginModel : PageModel
{
    private readonly AdminAuthService _adminAuthService;

    public LoginModel(AdminAuthService adminAuthService)
    {
        _adminAuthService = adminAuthService;
    }

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string ReturnUrl { get; private set; } = "/";

    public bool IsSetupMode { get; private set; }

    public string StatusMessage { get; private set; } = string.Empty;

    public bool StatusIsError { get; private set; }

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

    private string NormalizeReturnUrl(string? returnUrl)
    {
        return !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? returnUrl
            : "/";
    }
}
