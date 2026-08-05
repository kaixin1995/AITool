namespace AITool.Desktop.Models;

public sealed class AppSettings
{
    public string ServerUrl { get; set; } = "http://192.168.3.8:5029";
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    public DateTimeOffset? RefreshTokenExpiresAt { get; set; }
}
