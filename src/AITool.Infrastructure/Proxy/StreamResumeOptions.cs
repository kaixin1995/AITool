namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 流式响应可恢复缓冲选项。配置键：ProxyForwarding:StreamResume*(Enabled / MaxFrameBytes / MaxActive / RetainMinutes)。
/// 默认开启；纯增量能力——普通客户端（不带 X-Stream-Resume 头）行为与关闭时完全一致。
/// </summary>
public sealed class StreamResumeOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxFrameBytesPerRequest { get; init; } = StreamResumeStore.DefaultMaxFrameBytesPerRequest;
    public int MaxActiveStreams { get; init; } = StreamResumeStore.DefaultMaxActiveStreams;
    public int RetainMinutes { get; init; } = 5;
}