namespace AITool.Desktop.Models;

public sealed class TokenPair
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset AccessTokenExpiresAt { get; set; }
    public DateTimeOffset RefreshTokenExpiresAt { get; set; }
}

public sealed class AuthStatus
{
    public bool HasPassword { get; set; }
    public bool IsAuthenticated { get; set; }
    public AuthFeatures Features { get; set; } = new();
}

public sealed class AuthFeatures
{
    public bool OAuthEnabled { get; set; }
    public bool OAuthInspectionEnabled { get; set; }
    public bool DeveloperEnabled { get; set; }
}
