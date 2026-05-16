using Microsoft.AspNetCore.Mvc.Filters;

namespace AITool.Web.Services;

/// <summary>
/// 仅在请求发生异常时记录详细上下文，避免正常访问大量写入日志。
/// </summary>
public sealed class HttpExceptionLoggingFilter : IAsyncExceptionFilter
{
    /// <summary>
    /// 异常日志记录器。
    /// </summary>
    private readonly ILogger<HttpExceptionLoggingFilter> _logger;

    /// <summary>
    /// 初始化异常日志过滤器。
    /// </summary>
    public HttpExceptionLoggingFilter(ILogger<HttpExceptionLoggingFilter> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 捕获请求处理异常，并补充请求上下文写入日志。
    /// </summary>
    public async Task OnExceptionAsync(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException)
        {
            return;
        }

        // 发生异常时记录当前请求对象，便于还原请求现场。
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

    /// <summary>
    /// 在不影响后续请求处理的前提下，尽量读取原始请求正文。
    /// </summary>
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
