using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Hosting;

/// <summary>
/// 全局异常处理中间件扩展方法。
/// <para>
/// 捕获未处理异常，记录详细请求信息（路径、方法、TraceId、查询字符串、请求体），
/// 并返回统一的 JSON 错误响应。OperationCanceledException 会被静默忽略，
/// 避免客户端主动断开连接时产生大量无意义日志。
/// </para>
/// </summary>
public static class GlobalExceptionHandlerExtensions
{
    /// <summary>
    /// 注册全局异常处理中间件。
    /// <para>
    /// 仅在非 Testing 环境下注册，测试环境使用框架默认行为以便于调试。
    /// 异常处理逻辑读取请求体用于日志诊断，并返回标准 JSON 错误响应。
    /// </para>
    /// </summary>
    /// <param name="app">应用程序构建器。</param>
    /// <param name="env">宿主环境信息，用于判断是否跳过注册。</param>
    public static void UseGlobalExceptionHandler(this IApplicationBuilder app, IHostEnvironment env)
    {
        if (env.IsEnvironment("Testing"))
        {
            return;
        }

        app.UseExceptionHandler(exceptionApp =>
        {
            exceptionApp.Run(async context =>
            {
                var feature = context.Features.Get<IExceptionHandlerFeature>();
                var logger = context.RequestServices.GetRequiredService<ILogger<object>>();
                if (feature?.Error is OperationCanceledException)
                {
                    return;
                }

                if (feature?.Error is not null)
                {
                    var requestBody = await RequestBodyReader.TryReadRequestBodySafelyAsync(context.Request, context.RequestAborted);

                    logger.LogError(feature.Error,
                        "未处理异常\nPath={Path}\nMethod={Method}\nTraceId={TraceId}\nQueryString={QueryString}\nRequestBody={RequestBody}",
                        context.Request.Path,
                        context.Request.Method,
                        context.TraceIdentifier,
                        context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty,
                        HttpLogFormatter.FormatBody(requestBody));
                }

                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsJsonAsync(new { message = "服务器内部异常" });
                }
            });
        });
    }
}
