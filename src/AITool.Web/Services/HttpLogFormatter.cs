using System.Text;
using Microsoft.AspNetCore.Http;

namespace AITool.Web.Services;

/// <summary>
/// 统一整理 HTTP 请求和响应正文，避免日志内容过大或格式过乱。
/// </summary>
public static class HttpLogFormatter
{
    public const int DefaultMaxBodyLength = 12000;

    /// <summary>
    /// 规范化正文内容，并在超过长度上限时截断输出。
    /// 先截断再做换行归一：对多 MB 的失败正文只复制截断后的片段，避免热路径整串复制。
    /// </summary>
    public static string FormatBody(string? body, int maxLength = DefaultMaxBodyLength)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "<empty>";
        }

        // 异常排查时保留请求与返回主体，但限制体积避免日志文件无限膨胀。
        var isTruncated = body.Length > maxLength;
        var slice = isTruncated ? body[..maxLength] : body;
        var normalized = slice.Replace("\r\n", "\n").Trim();
        if (!isTruncated)
        {
            return normalized;
        }

        // 截断长度按原始正文计（归一化会吃掉 \r 字符，不适合再作为截断基准）。
        return $"{normalized}\n...<truncated {body.Length - maxLength} chars>";
    }

    public static async Task<string> ReadRequestBodyPreviewAsync(
        HttpRequest request,
        CancellationToken cancellationToken,
        int maxLength = DefaultMaxBodyLength)
    {
        maxLength = Math.Max(1, maxLength);
        try
        {
            request.EnableBuffering();
            if (request.Body.CanSeek) request.Body.Position = 0;

            using var reader = new StreamReader(request.Body, leaveOpen: true);
            var buffer = new char[Math.Min(4096, maxLength + 1)];
            var builder = new StringBuilder(Math.Min(maxLength, 4096));
            var totalRead = 0;
            while (totalRead <= maxLength)
            {
                var read = await reader.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, maxLength + 1 - totalRead)),
                    cancellationToken);
                if (read == 0) break;
                builder.Append(buffer, 0, read);
                totalRead += read;
            }

            if (builder.Length <= maxLength) return builder.ToString();

            const string suffix = "\n...<request body preview truncated>";
            var prefixLength = Math.Max(0, maxLength - suffix.Length);
            return builder.ToString(0, prefixLength) + suffix;
        }
        catch (OperationCanceledException)
        {
            return "<canceled>";
        }
        catch
        {
            return "<unavailable>";
        }
        finally
        {
            try
            {
                if (request.Body.CanSeek) request.Body.Position = 0;
            }
            catch
            {
            }
        }
    }
}
