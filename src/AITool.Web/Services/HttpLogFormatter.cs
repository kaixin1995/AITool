namespace AITool.Web.Services;

public static class HttpLogFormatter
{
    // 异常排查时保留请求与返回主体，但限制体积避免日志文件无限膨胀。
    public static string FormatBody(string? body, int maxLength = 12000)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "<empty>";
        }

        var normalized = body.Replace("\r\n", "\n").Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return $"{normalized[..maxLength]}\n...<truncated {normalized.Length - maxLength} chars>";
    }
}
