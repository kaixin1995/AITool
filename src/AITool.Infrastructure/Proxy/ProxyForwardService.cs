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

            try
            {
                using var httpRequest = BuildRequestMessage(request);
                var response = await _httpClient.SendAsync(httpRequest, timeoutCts.Token);
                var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);

                // 从响应中提取 Token 用量
                var (inputTokens, outputTokens) = ExtractTokenUsage(responseBody, request.ProtocolType);

                if (response.IsSuccessStatusCode)
                {
                    return new ProxyForwardResult
                    {
                        Success = true,
                        StatusCode = (int)response.StatusCode,
                        ResponseBody = responseBody,
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens
                    };
                }

                if (attempt == attempts - 1)
                {
                    return new ProxyForwardResult
                    {
                        Success = false,
                        StatusCode = (int)response.StatusCode,
                        ResponseBody = responseBody,
                        ErrorMessage = responseBody
                    };
                }
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == attempts - 1)
                {
                    return new ProxyForwardResult
                    {
                        Success = false,
                        ErrorMessage = $"Request timed out after {request.RequestTimeoutSeconds}s: {ex.Message}"
                    };
                }
            }
            catch (Exception ex)
            {
                if (attempt == attempts - 1)
                {
                    return new ProxyForwardResult
                    {
                        Success = false,
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

    // 从响应体中提取 Token 用量信息
    private static (int inputTokens, int outputTokens) ExtractTokenUsage(string responseBody, string protocolType)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (protocolType == "Anthropic")
            {
                if (root.TryGetProperty("usage", out var usage))
                {
                    var input = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                    var output = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                    return (input, output);
                }
            }
            else
            {
                if (root.TryGetProperty("usage", out var usage))
                {
                    var input = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                    var output = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
                    return (input, output);
                }
            }
        }
        catch
        {
            // 解析失败时返回零值
        }

        return (0, 0);
    }
}
