using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AITool.Application.Detection;
using AITool.Application.Sites;
using AITool.Domain.Models;
using AITool.Domain.Sites;

namespace AITool.Infrastructure.OpenAI;

/// <summary>
/// OpenAI 兼容站点的模型探测实现，通过发送最小化聊天请求验证模型可用性
/// </summary>
public sealed class OpenAiModelProbeService : IModelProbeService
{
    /// <summary>
    /// 用于发送模型探测请求的 HTTP 客户端
    /// </summary>
    private readonly HttpClient _httpClient;

    /// <summary>
    /// 注入 HTTP 客户端
    /// </summary>
    public OpenAiModelProbeService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// 向站点发送一次最小化 chat completion 请求，测量响应耗时并判断模型是否可用
    /// </summary>
    public async Task<ModelProbeResult> ProbeAsync(Site site, ModelLibraryItem model, CancellationToken cancellationToken)
    {
        var url = SiteEndpointPathResolver.BuildUrl(site.BaseUrl, site.EndpointPathMode, "chat/completions");

        var requestBody = new
        {
            model = model.ModelName,
            messages = new[] { new { role = "user", content = "hi" } },
            max_tokens = 1
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("Authorization", $"Bearer {site.ApiKey}");

        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            sw.Stop();

            if (response.IsSuccessStatusCode)
            {
                return new ModelProbeResult { Success = true, DurationMs = (int)sw.ElapsedMilliseconds };
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorMessage = ExtractErrorMessage(errorBody);
            return new ModelProbeResult { Success = false, DurationMs = (int)sw.ElapsedMilliseconds, ErrorMessage = errorMessage };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ModelProbeResult { Success = false, DurationMs = (int)sw.ElapsedMilliseconds, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// 从 OpenAI 错误响应体中提取可读的错误信息
    /// </summary>
    private static string? ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var message = doc.RootElement
                .GetProperty("error")
                .GetProperty("message")
                .GetString();
            return message;
        }
        catch
        {
            // 无法解析时截断原始响应
            return body.Length > 200 ? body[..200] : body;
        }
    }
}
