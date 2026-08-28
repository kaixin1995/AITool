namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 生成控制台代理日志的单行摘要，便于快速查看请求结果。
/// </summary>
public static class ConsoleProxyLogFormatter
{
    /// <summary>
    /// 按固定格式拼接代理调用摘要，详细内容仍由文件日志负责保存。
    /// </summary>
    public static string BuildSummary(
        string clientProtocol,
        string requestSource,
        string modelName,
        string actualProtocolType,
        int responseStatusCode,
        bool success,
        bool isStreaming,
        bool isStreamInterrupted,
        int totalDurationMs,
        int requestBodyLength,
        int responseBodyLength,
        string? siteName = null,
        string? forwardingMode = null,
        string? dumpFileName = null)
    {
        var sitePart = string.IsNullOrWhiteSpace(siteName) ? string.Empty : $" site={siteName}";
        var modePart = string.IsNullOrWhiteSpace(forwardingMode) ? string.Empty : $" mode={forwardingMode}";
        var dumpPart = string.IsNullOrWhiteSpace(dumpFileName) ? string.Empty : $" dump={dumpFileName}";

        return $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] proxy client={clientProtocol} source={requestSource} model={modelName} upstream={actualProtocolType}{sitePart}{modePart} status={responseStatusCode} success={success} streaming={isStreaming} interrupted={isStreamInterrupted} duration_ms={totalDurationMs} request_chars={requestBodyLength} response_chars={responseBodyLength}{dumpPart}";
    }
}
