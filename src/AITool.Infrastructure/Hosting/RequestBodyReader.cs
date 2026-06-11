using Microsoft.AspNetCore.Http;

namespace AITool.Infrastructure.Hosting;

/// <summary>
/// 提供安全读取 HTTP 请求体的能力，用于异常日志记录等场景。
/// </summary>
public static class RequestBodyReader
{
    /// <summary>
    /// 安全读取请求体内容。启用缓冲后从头读取，读取完毕重置流位置。
    /// 读取失败或请求被取消时返回占位字符串，不会抛出异常。
    /// </summary>
    /// <param name="request">HTTP 请求对象。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>请求体文本，或占位错误信息。</returns>
    public static async Task<string> TryReadRequestBodySafelyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            request.EnableBuffering();
            using var reader = new StreamReader(request.Body, leaveOpen: true);
            request.Body.Position = 0;
            var requestBody = await reader.ReadToEndAsync(cancellationToken);
            request.Body.Position = 0;
            return requestBody;
        }
        catch (OperationCanceledException)
        {
            return "<canceled>";
        }
        catch
        {
            return "<unavailable>";
        }
    }
}
