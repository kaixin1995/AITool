namespace AITool.Domain.Proxy;

/// <summary>
/// 表示一条结构化对话记录，用于按会话查看用户输入与 AI 输出。
/// </summary>
public sealed class ConversationTurnLog
{
    /// <summary>
    /// 对话记录唯一标识。
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 对应的请求链路标识。
    /// </summary>
    public Guid RequestId { get; set; }

    /// <summary>
    /// 创建时间。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 用户发起当前这轮请求的时间。
    /// </summary>
    public DateTimeOffset? UserCreatedAt { get; set; }

    /// <summary>
    /// 工具来源，例如 claude-code。
    /// </summary>
    public string SourceTool { get; set; } = string.Empty;

    /// <summary>
    /// 会话标识。
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// 会话分组键。
    /// </summary>
    public string ConversationGroupKey { get; set; } = string.Empty;

    /// <summary>
    /// 平台访问密钥标识。
    /// </summary>
    public Guid AccessKeyId { get; set; }

    /// <summary>
    /// 请求入口模型名。
    /// </summary>
    public string RequestModel { get; set; } = string.Empty;

    /// <summary>
    /// 协议类型。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;

    /// <summary>
    /// 请求路径。
    /// </summary>
    public string RequestPath { get; set; } = string.Empty;

    /// <summary>
    /// 调用来源入口。
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// 用户输入文本。
    /// </summary>
    public string UserInputText { get; set; } = string.Empty;

    /// <summary>
    /// AI 输出的 Markdown 文本。
    /// </summary>
    public string AssistantOutputMarkdown { get; set; } = string.Empty;

    /// <summary>
    /// 输入 Token 数。
    /// </summary>
    public int InputTokens { get; set; }

    /// <summary>
    /// 缓存 Token 数。
    /// </summary>
    public int CachedTokens { get; set; }

    /// <summary>
    /// 输出 Token 数。
    /// </summary>
    public int OutputTokens { get; set; }

    /// <summary>
    /// 是否为流式响应。
    /// </summary>
    public bool IsStreaming { get; set; }

    /// <summary>
    /// 状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 附加元数据 JSON。
    /// </summary>
    public string MetadataJson { get; set; } = string.Empty;
}
