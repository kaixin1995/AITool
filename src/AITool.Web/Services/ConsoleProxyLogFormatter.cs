namespace AITool.Web.Services;

public static class ConsoleProxyLogFormatter
{
    // 控制台只保留单行摘要，把详细内容留给本地日志文件，尽量降低运行期开销。
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
        return $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] proxy client={clientProtocol} source={requestSource} model={modelName} upstream={actualProtocolType} status={responseStatusCode} success={success} streaming={isStreaming} interrupted={isStreamInterrupted} duration_ms={totalDurationMs} request_chars={requestBodyLength} response_chars={responseBodyLength}";
    }
}
