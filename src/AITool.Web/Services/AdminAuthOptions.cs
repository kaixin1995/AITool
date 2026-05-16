namespace AITool.Web.Services;

/// <summary>
/// 后台登录认证配置。
/// </summary>
public sealed class AdminAuthOptions
{
    /// <summary>
    /// 配置节名称，用于从配置文件中读取后台认证设置。
    /// </summary>
    public const string SectionName = "AdminAuth";

    /// <summary>
    /// 后台登录密码的 MD5 哈希值。
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;
}
