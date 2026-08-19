using AITool.Web.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AITool.Web.Services;

/// <summary>
/// 仅在请求发生异常时记录详细上下文，避免正常访问大量写入日志；
/// 并对后台 API（/api 开头）返回统一的 <see cref="ApiResponse"/> JSON，便于前端处理。
/// 代理端点（/v1 等）的异常仍由 UseExceptionHandler 中间件兜底。
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
        var requestBody = await HttpLogFormatter.ReadRequestBodyPreviewAsync(request, context.HttpContext.RequestAborted);

        _logger.LogError(context.Exception,
            "请求处理异常\nPath={Path}\nMethod={Method}\nTraceId={TraceId}\nQueryString={QueryString}\nRequestBody={RequestBody}",
            request.Path,
            request.Method,
            context.HttpContext.TraceIdentifier,
            request.QueryString.HasValue ? request.QueryString.Value : string.Empty,
            HttpLogFormatter.FormatBody(requestBody));

        // 后台 API 统一返回 ApiResponse JSON（500）。代理端点不设 Result，交给 UseExceptionHandler。
        if (request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Result = new ObjectResult(ApiResponse.Fail("服务器内部异常", "internal_error"))
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
            context.ExceptionHandled = true;
        }
    }

}
