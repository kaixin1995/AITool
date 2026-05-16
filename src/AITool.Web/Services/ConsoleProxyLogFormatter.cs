namespace AITool.Web.Services;

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
        int responseBodyLength)
    {
        // 控制台只保留单行摘要，把详细内容留给本地日志文件，尽量降低运行期开销。
        return $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] proxy client={clientProtocol} source={requestSource} model={modelName} upstream={actualProtocolType} status={responseStatusCode} success={success} streaming={isStreaming} interrupted={isStreamInterrupted} duration_ms={totalDurationMs} request_chars={requestBodyLength} response_chars={responseBodyLength}";
    }
}
