namespace AITool.Admin.Services;

/// <summary>
/// JWT 签发与校验的配置选项，对应 appsettings.json 中的 "Jwt" 节。
/// </summary>
public sealed class JwtOptions
{
    /// <summary>
    /// 配置节名称。
    /// </summary>
    public const string SectionName = "Jwt";

    /// <summary>
    /// 签发者（iss claim）。
    /// </summary>
    public string Issuer { get; set; } = "AITool";

    /// <summary>
    /// 受众（aud claim）。
    /// </summary>
    public string Audience { get; set; } = "AITool";

    /// <summary>
    /// 对称签名密钥（HS256）。至少 32 字节（256 位）。
    /// 生产环境应通过环境变量或安全配置提供，不要硬编码。
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// access token 有效期（分钟）。
    /// </summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>
    /// refresh token 有效期（天）。
    /// </summary>
    public int RefreshTokenDays { get; set; } = 7;
}
