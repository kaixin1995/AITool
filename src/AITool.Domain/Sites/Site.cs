using SqlSugar;

namespace AITool.Domain.Sites;

/// <summary>
/// 表示一个外部 AI 服务站点，用于保存接入地址、认证信息以及协议能力等基础配置。
/// </summary>
[SugarTable("Sites")]
public sealed class Site
{
    /// <summary>
    /// 站点唯一标识，用于在映射、路由和调用记录中引用该站点。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 站点名称，用于页面展示、配置管理和日志识别。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = false)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 站点根地址，作为请求该服务时拼接接口路径的基础地址。
    /// </summary>
    [SugarColumn(Length = 500, IsNullable = false)]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// 接口路径模式，用于区分根地址是否已经包含接口版本路径。
    /// </summary>
    [SugarColumn(Length = 50, IsNullable = false)]
    public string EndpointPathMode { get; set; } = "standard-root";

    /// <summary>
    /// 站点访问密钥，用于调用该外部服务时进行身份认证。
    /// </summary>
    [SugarColumn(Length = 500, IsNullable = false)]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 协议类型，用于标识站点默认按哪种协议进行请求交互。
    /// </summary>
    [SugarColumn(Length = 50, IsNullable = false)]
    public string ProtocolType { get; set; } = "OpenAI";

    /// <summary>
    /// 标记站点是否支持 OpenAI 原协议直连，用于路由或调用时判断可用接入方式。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool SupportsOpenAi { get; set; } = true;

    /// <summary>
    /// 标记站点是否支持 Anthropic 原协议直连，用于补充站点协议能力描述。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool SupportsAnthropic { get; set; }

    /// <summary>
    /// 标记站点当前是否启用，禁用后通常不再参与路由和实际调用。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 站点配置创建时间，用于记录该接入项何时被加入系统。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
