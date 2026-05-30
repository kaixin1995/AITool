namespace AITool.Application.Conversations;

/// <summary>
/// 对话记录写入服务接口。
/// </summary>
public interface IConversationLogService
{
    /// <summary>
    /// 记录一条结构化对话。
    /// </summary>
    Task LogAsync(ConversationTurnEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// 结构化对话记录条目。
/// </summary>
public sealed class ConversationTurnEntry
{
    /// <summary>
    /// 请求链路标识。
    /// </summary>
    public Guid RequestId { get; set; }

    /// <summary>
    /// 工具来源。
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
    /// 请求模型名称。
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
    /// 来源入口。
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// 用户输入文本。
    /// </summary>
    public string UserInputText { get; set; } = string.Empty;

    /// <summary>
    /// AI 输出 Markdown。
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
    /// 是否流式。
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
