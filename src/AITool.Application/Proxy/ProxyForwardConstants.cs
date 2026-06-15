namespace AITool.Application.Proxy;

/// <summary>
/// 代理转发相关的性能与诊断常量。
/// </summary>
public static class ProxyForwardConstants
{
    /// <summary>
    /// 流式响应体累积到 <c>ResponseBody</c> 诊断副本的最大字符数。
    /// <para>
    /// 流式转发时会把已转发的 SSE 行追加进 <see cref="global::System.Text.StringBuilder"/>，
    /// 最终仅用于失败/中断诊断日志（下游 <c>HttpLogFormatter.FormatBody</c> 会再次截断）。
    /// 完整响应已实时转发给客户端，这里无需保留全文。
    /// 设置上限避免长输出（几万 token）在大对象堆（LOH）上反复扩容。
    /// 转发本身不受此上限影响，仅截断本地诊断副本。
    /// </para>
    /// </summary>
    public const int MaxStreamBodyCaptureChars = 64 * 1024;
}
