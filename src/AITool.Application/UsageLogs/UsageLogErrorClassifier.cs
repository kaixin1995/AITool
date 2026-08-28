namespace AITool.Application.UsageLogs;

/// <summary>
/// 根据请求状态、HTTP 状态码和错误摘要生成稳定的错误分类。
/// </summary>
public static class UsageLogErrorClassifier
{
    /// <summary>
    /// 对使用日志条目进行纯函数分类，只返回固定分类值，不返回错误正文。
    /// </summary>
    public static string? Classify(UsageLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return Classify(entry.Status, entry.HttpStatusCode, entry.ErrorMessage, entry.IsStreamInterrupted);
    }

    /// <summary>
    /// 按固定优先级分类一次请求失败。
    /// </summary>
    public static string? Classify(
        string? status,
        int? httpStatusCode,
        string? errorMessage,
        bool isStreamInterrupted)
    {
        if (IsSuccessful(status, httpStatusCode))
        {
            return null;
        }

        if (isStreamInterrupted || ContainsAny(errorMessage, "stream interrupted", "stream-interrupted"))
        {
            return "stream-interrupted";
        }

        if (httpStatusCode == 408 || ContainsAny(errorMessage, "timeout", "timed out", "deadline exceeded"))
        {
            return "timeout";
        }

        if (httpStatusCode == 401 || ContainsAny(errorMessage, "authentication", "unauthorized", "invalid api key", "invalid token"))
        {
            return "authentication";
        }

        if (httpStatusCode == 429 || ContainsAny(errorMessage, "rate limit", "rate-limit", "ratelimit", "too many requests"))
        {
            return "rate-limit";
        }

        if (httpStatusCode == 404 || ContainsAny(errorMessage, "model not found", "model-not-found", "model_not_found"))
        {
            return "model-not-found";
        }

        if (IsUpstreamStatus(httpStatusCode) || ContainsAny(errorMessage, "upstream"))
        {
            return "upstream-error";
        }

        if (ContainsAny(errorMessage, "network", "connection", "socket", "dns", "name resolution", "no such host")
            || httpStatusCode is null or 0)
        {
            return "network-error";
        }

        return "other";
    }

    /// <summary>
    /// 成功状态不产生错误分类；同时兼容只有 HTTP 状态码的调用方。
    /// </summary>
    private static bool IsSuccessful(string? status, int? httpStatusCode)
    {
        return string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
            || (httpStatusCode is >= 200 and < 300 && !string.Equals(status, "fail", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 判断状态码是否属于上游服务错误范围。
    /// </summary>
    private static bool IsUpstreamStatus(int? httpStatusCode)
    {
        return httpStatusCode is >= 500 and <= 599;
    }

    /// <summary>
    /// 使用不区分大小写的关键字匹配错误摘要。
    /// </summary>
    private static bool ContainsAny(string? errorMessage, params string[] keywords)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        return keywords.Any(keyword => errorMessage.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}
