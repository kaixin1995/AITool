namespace AITool.Infrastructure.Proxy;

/// <summary>
/// SSE 心跳选项：流式响应静默超过阈值秒数后，向下游注入 `: ping` 注释帧。
/// 0 表示关闭（默认 15 秒）。配置键：ProxyForwarding:SseHeartbeatSeconds。
/// </summary>
public sealed class SseHeartbeatOptions
{
    public int Seconds { get; init; }

    public SseHeartbeatOptions(int seconds)
    {
        Seconds = Math.Max(0, seconds);
    }

    public bool Enabled => Seconds > 0;
}