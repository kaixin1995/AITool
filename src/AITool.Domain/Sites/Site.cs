namespace AITool.Domain.Sites;

// 站点实体，记录外部 AI 服务站点信息
public sealed class Site
{
    // 站点主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 站点名称
    public string Name { get; set; } = string.Empty;

    // 站点根地址
    public string BaseUrl { get; set; } = string.Empty;

    // 站点访问密钥
    public string ApiKey { get; set; } = string.Empty;

    // 协议类型，当前用于区分 OpenAI 兼容站点
    public string ProtocolType { get; set; } = "OpenAI";

    // 是否启用该站点
    public bool IsEnabled { get; set; } = true;

    // 创建时间
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
