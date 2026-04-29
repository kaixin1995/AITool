using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Infrastructure.Persistence;

namespace AITool.Infrastructure.Proxy;

// 基于 HttpClient 的代理转发服务，将请求转发到目标站点
public sealed class ProxyForwardService : IProxyForwardService
{
    private readonly HttpClient _httpClient;

    public ProxyForwardService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    // 将请求转发到目标站点并解析响应中的 Token 用量
    public async Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        var attempts = Math.Max(0, request.RetryCount) + 1;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, request.RequestTimeoutSeconds)));
            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var httpRequest = BuildRequestMessage(request);
                var response = await _httpClient.SendAsync(httpRequest, timeoutCts.Token);
                var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                stopwatch.Stop();

                // 从响应中提取 Token 与流式指标
                var usage = ExtractUsageMetrics(responseBody, request.ProtocolType);
                var isStreaming = IsStreamingRequest(request.RequestBody);
                var totalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds);
                var firstTokenLatencyMs = response.IsSuccessStatusCode && isStreaming ? totalDurationMs : 0;
                var streamDurationMs = 0;

                if (response.IsSuccessStatusCode && HasUsableResponse(responseBody, request.ProtocolType))
                {
                    return new ProxyForwardResult
                    {
                        Success = true,
                        StatusCode = (int)response.StatusCode,
                        ResponseBody = responseBody,
                        InputTokens = usage.InputTokens,
                        CachedTokens = usage.CachedTokens,
                        OutputTokens = usage.OutputTokens,
                        IsStreaming = isStreaming,
                        FirstTokenLatencyMs = firstTokenLatencyMs,
                        StreamDurationMs = streamDurationMs,
                        TotalDurationMs = totalDurationMs
                    };
                }

                if (attempt == attempts - 1)
                {
                    return new ProxyForwardResult
                    {
                        Success = false,
                        StatusCode = (int)response.StatusCode,
                        ResponseBody = responseBody,
                        InputTokens = usage.InputTokens,
                        CachedTokens = usage.CachedTokens,
                        OutputTokens = usage.OutputTokens,
                        IsStreaming = isStreaming,
                        FirstTokenLatencyMs = firstTokenLatencyMs,
                        StreamDurationMs = streamDurationMs,
                        TotalDurationMs = totalDurationMs,
                        ErrorMessage = BuildFailureMessage(response, responseBody, request.ProtocolType)
                    };
                }
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                if (attempt == attempts - 1)
                {
                    return new ProxyForwardResult
                    {
                        Success = false,
                        TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                        ErrorMessage = $"Request timed out after {request.RequestTimeoutSeconds}s: {ex.Message}"
                    };
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                if (attempt == attempts - 1)
                {
                    return new ProxyForwardResult
                    {
                        Success = false,
                        TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                        ErrorMessage = ex.Message
                    };
                }
            }
        }

        return new ProxyForwardResult
        {
            Success = false,
            ErrorMessage = "Unknown proxy forwarding error"
        };
    }

    // 构建发送到上游的 HTTP 请求对象
    private static HttpRequestMessage BuildRequestMessage(ProxyForwardRequest request)
    {
        var targetUrl = request.ProtocolType == "Anthropic"
            ? $"{request.TargetBaseUrl.TrimEnd('/')}/v1/messages"
            : $"{request.TargetBaseUrl.TrimEnd('/')}/v1/chat/completions";

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, targetUrl)
        {
            Content = new StringContent(ModifyRequestBody(request), Encoding.UTF8, "application/json")
        };

        // 根据协议类型设置认证头
        if (request.ProtocolType == "Anthropic")
        {
            httpRequest.Headers.Add("x-api-key", request.TargetApiKey);
            httpRequest.Headers.Add("anthropic-version", "2023-06-01");
        }
        else
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.TargetApiKey);
        }

        return httpRequest;
    }

    // 替换请求体中的模型名称为目标站点的模型名称
    private static string ModifyRequestBody(ProxyForwardRequest request)
    {
        try
        {
            using var doc = JsonDocument.Parse(request.RequestBody);
            var root = doc.RootElement;
            var dict = new Dictionary<string, object>();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("model"))
                {
                    dict["model"] = request.TargetModelName;
                }
                else
                {
                    dict[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText())!;
                }
            }
            return JsonSerializer.Serialize(dict);
        }
        catch
        {
            return request.RequestBody;
        }
    }

    // 判断原始请求是否为流式模式
    private static bool IsStreamingRequest(string requestBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            return doc.RootElement.TryGetProperty("stream", out var stream)
                && stream.ValueKind is JsonValueKind.True or JsonValueKind.False
                && stream.GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    // 根据响应内容判断当前候选是否真正返回了可用结果
    private static bool HasUsableResponse(string responseBody, string protocolType)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out _))
            {
                return false;
            }

            if (protocolType == "Anthropic")
            {
                return root.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array
                    && content.GetArrayLength() > 0;
            }

            return root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0;
        }
        catch
        {
            return false;
        }
    }

    // 为最终失败结果构造更明确的错误信息
    private static string BuildFailureMessage(HttpResponseMessage response, string responseBody, string protocolType)
    {
        if (!response.IsSuccessStatusCode)
        {
            return responseBody;
        }

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return "Upstream returned an empty response body.";
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                return error.ValueKind == JsonValueKind.String
                    ? error.GetString() ?? responseBody
                    : error.GetRawText();
            }
        }
        catch
        {
            return "Upstream returned an unreadable response body.";
        }

        return protocolType == "Anthropic"
            ? "Upstream returned no usable content blocks."
            : "Upstream returned no usable choices.";
    }

    // 从响应体中提取 Token 用量信息
    private static (int InputTokens, int CachedTokens, int OutputTokens) ExtractUsageMetrics(string responseBody, string protocolType)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (!root.TryGetProperty("usage", out var usage))
            {
                return (0, 0, 0);
            }

            if (protocolType == "Anthropic")
            {
                var input = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                var cached = 0;
                if (usage.TryGetProperty("cache_read_input_tokens", out var readCache))
                {
                    cached += readCache.GetInt32();
                }
                if (usage.TryGetProperty("cache_creation_input_tokens", out var createCache))
                {
                    cached += createCache.GetInt32();
                }
                var output = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                return (input, cached, output);
            }

            var prompt = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
            var promptDetails = usage.TryGetProperty("prompt_tokens_details", out var ptd) ? ptd : default;
            var cachedTokens = promptDetails.ValueKind == JsonValueKind.Object && promptDetails.TryGetProperty("cached_tokens", out var ct)
                ? ct.GetInt32()
                : 0;
            var completion = usage.TryGetProperty("completion_tokens", out var completionTokens) ? completionTokens.GetInt32() : 0;
            return (prompt, cachedTokens, completion);
        }
        catch
        {
            // 解析失败时返回零值
        }

        return (0, 0, 0);
    }
}
