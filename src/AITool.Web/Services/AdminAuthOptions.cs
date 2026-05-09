namespace AITool.Web.Services;

public sealed class AdminAuthOptions
{
    public const string SectionName = "AdminAuth";

    public string PasswordHash { get; set; } = string.Empty;
}
