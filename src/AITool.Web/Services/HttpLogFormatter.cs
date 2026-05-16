namespace AITool.Web.Services;

/// <summary>
/// 统一整理 HTTP 请求和响应正文，避免日志内容过大或格式过乱。
/// </summary>
public static class HttpLogFormatter
{
    /// <summary>
    /// 规范化正文内容，并在超过长度上限时截断输出。
    /// </summary>
    public static string FormatBody(string? body, int maxLength = 12000)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "<empty>";
        }

        // 异常排查时保留请求与返回主体，但限制体积避免日志文件无限膨胀。
        var normalized = body.Replace("\r\n", "\n").Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return $"{normalized[..maxLength]}\n...<truncated {normalized.Length - maxLength} chars>";
    }
}
