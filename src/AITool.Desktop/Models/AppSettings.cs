namespace AITool.Desktop.Models;

public sealed class AppSettings
{
    public string ServerUrl { get; set; } = "http://127.0.0.1:5030";
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    public DateTimeOffset? RefreshTokenExpiresAt { get; set; }
    public string ThemeMode { get; set; } = "default";
}
