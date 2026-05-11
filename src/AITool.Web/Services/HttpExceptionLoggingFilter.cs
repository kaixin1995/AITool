using Microsoft.AspNetCore.Mvc.Filters;

namespace AITool.Web.Services;

// 仅记录异常请求，避免把正常访问写入磁盘日志。
public sealed class HttpExceptionLoggingFilter : IAsyncExceptionFilter
{
    private readonly ILogger<HttpExceptionLoggingFilter> _logger;

    public HttpExceptionLoggingFilter(ILogger<HttpExceptionLoggingFilter> logger)
    {
        _logger = logger;
    }

    public async Task OnExceptionAsync(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException)
        {
            return;
        }

        var request = context.HttpContext.Request;
        var requestBody = await TryReadRequestBodyAsync(request, context.HttpContext.RequestAborted);

        _logger.LogError(context.Exception,
            "请求处理异常\nPath={Path}\nMethod={Method}\nTraceId={TraceId}\nQueryString={QueryString}\nRequestBody={RequestBody}",
            request.Path,
            request.Method,
            context.HttpContext.TraceIdentifier,
            request.QueryString.HasValue ? request.QueryString.Value : string.Empty,
            HttpLogFormatter.FormatBody(requestBody));
    }

    private static async Task<string> TryReadRequestBodyAsync(HttpRequest request, CancellationToken cancellationToken)
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
