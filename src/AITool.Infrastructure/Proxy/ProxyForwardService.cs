using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 基于 HttpClient 的代理转发服务，将请求转发到目标站点
/// </summary>
public sealed class ProxyForwardService : IProxyForwardService
{
    /// <summary>
    /// 用于发送代理请求的 HTTP 客户端
    /// </summary>
    private readonly HttpClient _httpClient;
    /// <summary>
    /// 日志记录器，用于记录转发超时和异常
    /// </summary>
    private readonly ILogger<ProxyForwardService> _logger;

    /// <summary>
    /// 注入 HTTP 客户端和日志记录器
    /// </summary>
    public ProxyForwardService(HttpClient httpClient, ILogger<ProxyForwardService> logger)
    {
        // 真实超时统一交给每次请求的 CancellationToken 控制，避免 HttpClient 默认 100 秒提前截断。
        httpClient.Timeout = global::System.Threading.Timeout.InfiniteTimeSpan;
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// 将请求转发到目标站点并解析响应中的 Token 用量
    /// </summary>
    public async Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        var attempts = Math.Max(0, request.RetryCount) + 1;
        var requestBody = string.IsNullOrWhiteSpace(request.PreparedRequestBody)
            ? ModifyRequestBody(request.RequestBody, request.TargetModelName)
            : request.PreparedRequestBody;
        var isStreaming = request.EnableStreaming || IsStreamingRequest(request.RequestBody);

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, request.RequestTimeoutSeconds)));
            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var httpRequest = BuildRequestMessage(request, requestBody);
                var response = await _httpClient.SendAsync(
                    httpRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    stopwatch.Stop();
                    var errorBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                    if (attempt == attempts - 1)
                    {
                        return new ProxyForwardResult
                        {
                            Success = false,
                            StatusCode = (int)response.StatusCode,
                            ResponseBody = errorBody,
                            TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                            IsStreaming = isStreaming,
                            ErrorMessage = errorBody
                        };
                    }
                    continue;
                }

                if (isStreaming)
                {
                    return await ProcessStreamingResponseAsync(response, stopwatch, request, isStreaming, null, cancellationToken);
                }

                var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                stopwatch.Stop();

                var usage = ExtractUsageMetrics(responseBody, request.ProtocolType);
                var totalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds);

                if (HasUsableResponse(responseBody, request.ProtocolType))
                {
                    return new ProxyForwardResult
                    {
                        Success = true,
                        StatusCode = (int)response.StatusCode,
                        ResponseBody = responseBody,
                        InputTokens = usage.InputTokens,
                        CachedTokens = usage.CachedTokens,
                        OutputTokens = usage.OutputTokens,
                        IsStreaming = false,
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
                        IsStreaming = false,
                        TotalDurationMs = totalDurationMs,
                        ErrorMessage = BuildFailureMessage(responseBody, request.ProtocolType)
                    };
                }
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                if (attempt == attempts - 1)
                {
                    _logger.LogError(ex,
                        "代理请求超时。Protocol={Protocol}, Target={Target}, Streaming={Streaming}, TimeoutSeconds={TimeoutSeconds}",
                        request.ProtocolType,
                        request.TargetBaseUrl,
                        isStreaming,
                        request.RequestTimeoutSeconds);
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
                    _logger.LogError(ex,
                        "代理请求失败。Protocol={Protocol}, Target={Target}, Streaming={Streaming}",
                        request.ProtocolType,
                        request.TargetBaseUrl,
                        isStreaming);
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

    /// <summary>
    /// 直接把上游 SSE 数据块逐段交给调用方，供控制器做实时协议转换与下游刷新。
    /// </summary>
    public async Task<ProxyForwardResult> ForwardStreamingAsync(
        ProxyForwardRequest request,
        Func<string, CancellationToken, Task> onSseDataAsync,
        CancellationToken cancellationToken = default)
    {
        var attempts = Math.Max(0, request.RetryCount) + 1;
        var requestBody = string.IsNullOrWhiteSpace(request.PreparedRequestBody)
            ? ModifyRequestBody(request.RequestBody, request.TargetModelName)
            : request.PreparedRequestBody;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, request.RequestTimeoutSeconds)));
            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var httpRequest = BuildRequestMessage(request, requestBody);
                var response = await _httpClient.SendAsync(
                    httpRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    stopwatch.Stop();
                    var errorBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                    if (attempt == attempts - 1)
                    {
                        return new ProxyForwardResult
                        {
                            Success = false,
                            StatusCode = (int)response.StatusCode,
                            ResponseBody = errorBody,
                            TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                            IsStreaming = true,
                            ErrorMessage = errorBody
                        };
                    }

                    continue;
                }

                return await ProcessStreamingResponseAsync(response, stopwatch, request, true, onSseDataAsync, cancellationToken);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                if (attempt == attempts - 1)
                {
                    _logger.LogError(ex,
                        "代理流式请求超时。Protocol={Protocol}, Target={Target}, TimeoutSeconds={TimeoutSeconds}",
                        request.ProtocolType,
                        request.TargetBaseUrl,
                        request.RequestTimeoutSeconds);
                    return new ProxyForwardResult
                    {
                        Success = false,
                        TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                        IsStreaming = true,
                        ErrorMessage = $"Request timed out after {request.RequestTimeoutSeconds}s: {ex.Message}"
                    };
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                if (attempt == attempts - 1)
                {
                    _logger.LogError(ex,
                        "代理流式请求失败。Protocol={Protocol}, Target={Target}",
                        request.ProtocolType,
                        request.TargetBaseUrl);
                    return new ProxyForwardResult
                    {
                        Success = false,
                        TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                        IsStreaming = true,
                        ErrorMessage = ex.Message
                    };
                }
            }
        }

        return new ProxyForwardResult
        {
            Success = false,
            IsStreaming = true,
            ErrorMessage = "Unknown proxy forwarding error"
        };
    }

    /// <summary>
    /// 逐行读取 SSE 流，追踪首字延迟并提取 Token 用量。
    /// </summary>
    private async Task<ProxyForwardResult> ProcessStreamingResponseAsync(
        HttpResponseMessage response,
        Stopwatch stopwatch,
        ProxyForwardRequest request,
        bool isStreaming,
        Func<string, CancellationToken, Task>? onSseDataAsync,
        CancellationToken cancellationToken)
    {
        var totalDurationMs = 0;
        var firstTokenLatencyMs = 0;
        var inputTokens = 0;
        var cachedTokens = 0;
        var outputTokens = 0;
        var hasFirstContent = false;
        var receivedDoneEvent = false;
        var receivedAnthropicMessageStop = false;

        var sb = new StringBuilder();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break;

                sb.AppendLine(line);

                if (onSseDataAsync is not null)
                {
                    await onSseDataAsync(line, cancellationToken);
                }

                // 跳过空行和注释行
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith(':')) continue;

                // SSE 格式：data: {...}
                if (!line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase)) continue;

                var jsonText = line["data: ".Length..];
                if (string.Equals(jsonText, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    receivedDoneEvent = true;
                    continue;
                }

                // 首次收到有效内容时记录首字延迟
                if (!hasFirstContent)
                {
                    hasFirstContent = true;
                    firstTokenLatencyMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds);
                }

                // 从 SSE 数据块中提取 usage 和 token 信息
                try
                {
                    using var doc = JsonDocument.Parse(jsonText);
                    var root = doc.RootElement;

                    if (request.ProtocolType == "Anthropic"
                        && root.TryGetProperty("type", out var eventType)
                        && string.Equals(eventType.GetString(), "message_stop", StringComparison.OrdinalIgnoreCase))
                    {
                        receivedAnthropicMessageStop = true;
                    }

                    if (root.TryGetProperty("usage", out var usage))
                    {
                        var extracted = ExtractUsageFromElement(usage, request.ProtocolType);
                        if (extracted.InputTokens > 0) inputTokens = extracted.InputTokens;
                        if (extracted.CachedTokens > 0) cachedTokens = extracted.CachedTokens;
                        if (extracted.OutputTokens > 0) outputTokens = extracted.OutputTokens;
                    }

                    // Anthropic message_start 事件中的 usage 嵌套在 message 里
                    if (request.ProtocolType == "Anthropic"
                        && root.TryGetProperty("message", out var message)
                        && message.TryGetProperty("usage", out var msgUsage))
                    {
                        var extracted = ExtractUsageFromElement(msgUsage, request.ProtocolType);
                        if (extracted.InputTokens > 0) inputTokens = extracted.InputTokens;
                        if (extracted.OutputTokens > 0) outputTokens = extracted.OutputTokens;
                    }
                }
                catch
                {
                    // 非 JSON 的 data 行忽略
                }
            }
        }
        catch (Exception ex) when (hasFirstContent)
        {
            stopwatch.Stop();
            totalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds);
            _logger.LogError(ex,
                "代理流在返回首包后异常中断。Protocol={Protocol}, Target={Target}",
                request.ProtocolType,
                request.TargetBaseUrl);

            return new ProxyForwardResult
            {
                Success = true,
                StatusCode = (int)response.StatusCode,
                ResponseBody = sb.ToString(),
                InputTokens = inputTokens,
                CachedTokens = cachedTokens,
                OutputTokens = outputTokens,
                IsStreaming = isStreaming,
                HasStartedStreaming = true,
                IsStreamInterrupted = true,
                FirstTokenLatencyMs = firstTokenLatencyMs,
                StreamDurationMs = Math.Max(0, totalDurationMs - firstTokenLatencyMs),
                TotalDurationMs = totalDurationMs,
                ErrorMessage = ex.Message
            };
        }

        stopwatch.Stop();
        totalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds);
        var streamCompletedNormally = request.ProtocolType == "Anthropic"
            ? receivedAnthropicMessageStop
            : receivedDoneEvent;

        return new ProxyForwardResult
        {
            Success = true,
            StatusCode = (int)response.StatusCode,
            ResponseBody = sb.ToString(),
            InputTokens = inputTokens,
            CachedTokens = cachedTokens,
            OutputTokens = outputTokens,
            IsStreaming = isStreaming,
            HasStartedStreaming = hasFirstContent,
            IsStreamInterrupted = hasFirstContent && !streamCompletedNormally,
            FirstTokenLatencyMs = firstTokenLatencyMs,
            StreamDurationMs = Math.Max(0, totalDurationMs - firstTokenLatencyMs),
            TotalDurationMs = totalDurationMs,
            ErrorMessage = hasFirstContent && !streamCompletedNormally ? "stream interrupted before normal completion" : null
        };
    }

    /// <summary>
    /// 构建发送到上游的 HTTP 请求对象
    /// </summary>
    private static HttpRequestMessage BuildRequestMessage(ProxyForwardRequest request, string requestBody)
    {
        var targetPath = string.IsNullOrWhiteSpace(request.TargetPath)
            ? request.ProtocolType == "Anthropic"
                ? "/v1/messages"
                : "/v1/chat/completions"
            : request.TargetPath!;
        var targetUrl = $"{request.TargetBaseUrl.TrimEnd('/')}/{targetPath.TrimStart('/')}";

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, targetUrl)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };

        // 根据协议类型设置认证头
        if (request.ProtocolType == "Anthropic")
        {
            httpRequest.Headers.Add("x-api-key", request.TargetApiKey);
            httpRequest.Headers.Add(
                "anthropic-version",
                request.ForwardHeaders.TryGetValue("anthropic-version", out var anthropicVersion) && !string.IsNullOrWhiteSpace(anthropicVersion)
                    ? anthropicVersion
                    : "2023-06-01");
        }
        else
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.TargetApiKey);
        }

        foreach (var header in request.ForwardHeaders)
        {
            if (string.Equals(header.Key, "anthropic-version", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return httpRequest;
    }

    /// <summary>
    /// 替换请求体中的模型名称为目标站点的模型名称
    /// </summary>
    private static string ModifyRequestBody(string requestBody, string targetModelName)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            var root = doc.RootElement;
            var dict = new Dictionary<string, object>();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("model"))
                {
                    dict["model"] = targetModelName;
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
            return requestBody;
        }
    }

    /// <summary>
    /// 判断原始请求是否为流式模式
    /// </summary>
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

    /// <summary>
    /// 根据响应内容判断非流式响应是否真正返回了可用结果
    /// </summary>
    internal static bool HasUsableResponse(string responseBody, string protocolType)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            // error 属性存在且非 null 时才视为错误（Responses 格式中 error 为 null 是正常情况）
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                return false;
            }

            if (protocolType == "Anthropic")
            {
                return root.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array
                    && content.GetArrayLength() > 0;
            }

            // Chat Completions 格式：检查 choices 数组
            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                return true;
            }

            // Responses 格式：检查 output 数组
            if (root.TryGetProperty("output", out var output)
                && output.ValueKind == JsonValueKind.Array
                && output.GetArrayLength() > 0)
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 为非流式失败结果构造更明确的错误信息
    /// </summary>
    internal static string BuildFailureMessage(string responseBody, string protocolType)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return "Upstream returned an empty response body.";
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
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

    /// <summary>
    /// 从响应体中提取 Token 用量信息（非流式响应）
    /// </summary>
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

            return ExtractUsageFromElement(usage, protocolType);
        }
        catch
        {
            // 解析失败时返回零值
        }

        return (0, 0, 0);
    }

    /// <summary>
    /// 从 usage JSON 元素中提取 Token 用量
    /// </summary>
    private static (int InputTokens, int CachedTokens, int OutputTokens) ExtractUsageFromElement(JsonElement usage, string protocolType)
    {
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

        var openAiInputTokens = usage.TryGetProperty("input_tokens", out var inputTokens)
            ? inputTokens.GetInt32()
            : usage.TryGetProperty("prompt_tokens", out var promptTokens)
                ? promptTokens.GetInt32()
                : 0;

        // OpenAI Chat Completions 与 Responses 的缓存字段结构不同，这里统一兼容两种格式。
        var inputDetails = usage.TryGetProperty("input_tokens_details", out var itd)
            ? itd
            : usage.TryGetProperty("prompt_tokens_details", out var ptd)
                ? ptd
                : default;
        var cachedTokens = inputDetails.ValueKind == JsonValueKind.Object && inputDetails.TryGetProperty("cached_tokens", out var ct)
            ? ct.GetInt32()
            : 0;

        var openAiOutputTokens = usage.TryGetProperty("output_tokens", out var outputTokens)
            ? outputTokens.GetInt32()
            : usage.TryGetProperty("completion_tokens", out var completionTokens)
                ? completionTokens.GetInt32()
                : 0;

        return (openAiInputTokens, cachedTokens, openAiOutputTokens);
    }
}
