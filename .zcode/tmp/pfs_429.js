const fs = require('fs');
const path = "D:/Code/AI-Tool/src/AITool.Infrastructure/Proxy/ProxyForwardService.cs";
let raw = fs.readFileSync(path, 'utf8');
const eol = raw.includes('\r\n') ? '\r\n' : '\n';
let src = raw.replace(/\r\n/g, '\n');
const edit = (oldS, newS, expect, tag) => {
  const n = src.split(oldS).length - 1;
  if (n !== expect) throw new Error('[' + tag + '] 匹配 ' + n + ': ' + oldS.slice(0, 60));
  src = src.split(oldS).join(newS);
};

// 1) 两个方法的计数器声明（ForwardAsync 与 ForwardStreamingAsync 共用同一文本）
edit(
`        var tokenRefreshAttempted = false;
        var rateLimit429Count = 0;
        var requestBody = string.IsNullOrWhiteSpace(request.PreparedRequestBody)`,
`        var tokenRefreshAttempted = false;
        var rateLimit429Count = 0;
        // 实际执行的 429 重试次数（不随连续计数归零），用于结果上报与 usage 链路展示。
        var rateLimit429Retries = 0;
        var requestBody = string.IsNullOrWhiteSpace(request.PreparedRequestBody)`, 2, '两方法声明');

// 3) 两处 429 重试分支：计数 + 退避
edit(
`                        rateLimit429Count++;
                        // 阈值 = Max(1, N)：N=0/1 都表示一次 429 即失败；N=3 表示连续 3 次 429 才失败。
                        if (rateLimit429Count < Math.Max(1, request.RateLimitRetryCount))
                        {
                            attempt--;
                            continue;
                        }`,
`                        rateLimit429Count++;
                        // 阈值 = Max(1, N)：N=0/1 都表示一次 429 即失败；N=3 表示连续 3 次 429 才失败。
                        if (rateLimit429Count < Math.Max(1, request.RateLimitRetryCount))
                        {
                            rateLimit429Retries++;
                            attempt--;
                            // 429 退避：零延迟重击已被限流的上游只会继续吃 429 并加重封禁风险。
                            // 优先尊重上游 Retry-After（封顶 10s），否则固定 1.5s。
                            var backoff = Resolve429RetryDelay(response);
                            if (backoff > TimeSpan.Zero)
                            {
                                await Task.Delay(backoff, cancellationToken);
                            }
                            continue;
                        }`, 2, '429重试分支');

// 4) 非流式 429 耗尽返回打标
edit(
`                        return new ProxyForwardResult
                        {
                            Success = false,
                            StatusCode = (int)response.StatusCode,
                            ResponseBody = errorBody,
                            TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                            IsStreaming = isStreaming,
                            ErrorMessage = errorBody
                        };
                    }
                    else
                    {
                        rateLimit429Count = 0;
                    }`,
`                        return new ProxyForwardResult
                        {
                            Success = false,
                            StatusCode = (int)response.StatusCode,
                            ResponseBody = errorBody,
                            TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                            IsStreaming = isStreaming,
                            ErrorMessage = errorBody,
                            RateLimitRetryCount = rateLimit429Retries
                        };
                    }
                    else
                    {
                        rateLimit429Count = 0;
                    }`, 1, '非流式429耗尽');

// 5) 流式 429 耗尽返回打标
edit(
`                        return new ProxyForwardResult
                        {
                            Success = false,
                            StatusCode = (int)response.StatusCode,
                            ResponseBody = errorBody,
                            TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                            IsStreaming = true,
                            ErrorMessage = errorBody
                        };
                    }
                    else
                    {
                        rateLimit429Count = 0;
                    }`,
`                        return new ProxyForwardResult
                        {
                            Success = false,
                            StatusCode = (int)response.StatusCode,
                            ResponseBody = errorBody,
                            TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                            IsStreaming = true,
                            ErrorMessage = errorBody,
                            RateLimitRetryCount = rateLimit429Retries
                        };
                    }
                    else
                    {
                        rateLimit429Count = 0;
                    }`, 1, '流式429耗尽');

// 6) 非流式成功返回打标
edit(
`                    return new ProxyForwardResult
                    {
                        Success = true,
                        StatusCode = (int)response.StatusCode,
                        ResponseBody = responseBody,
                        InputTokens = usage.InputTokens,
                        CachedTokens = usage.CachedTokens,
                        OutputTokens = usage.OutputTokens,
                        IsStreaming = false,
                        TotalDurationMs = totalDurationMs
                    };`,
`                    return new ProxyForwardResult
                    {
                        Success = true,
                        StatusCode = (int)response.StatusCode,
                        ResponseBody = responseBody,
                        InputTokens = usage.InputTokens,
                        CachedTokens = usage.CachedTokens,
                        OutputTokens = usage.OutputTokens,
                        IsStreaming = false,
                        TotalDurationMs = totalDurationMs,
                        RateLimitRetryCount = rateLimit429Retries
                    };`, 1, '非流式成功');

// 7) 两处 return streamingResult 打标（缩进不同，分别处理）
edit(
`                    if (streamingResult.Success
                        || streamingResult.IsCanceled
                        || streamingResult.HasStartedStreaming
                        || attempt >= attempts - 1)
                    {
                        return streamingResult;
                    }`,
`                    if (streamingResult.Success
                        || streamingResult.IsCanceled
                        || streamingResult.HasStartedStreaming
                        || attempt >= attempts - 1)
                    {
                        streamingResult.RateLimitRetryCount = rateLimit429Retries;
                        return streamingResult;
                    }`, 1, '流式返回-深缩进');
edit(
`                if (streamingResult.Success
                    || streamingResult.IsCanceled
                    || streamingResult.HasStartedStreaming
                    || attempt >= attempts - 1)
                {
                    return streamingResult;
                }`,
`                if (streamingResult.Success
                    || streamingResult.IsCanceled
                    || streamingResult.HasStartedStreaming
                    || attempt >= attempts - 1)
                {
                    streamingResult.RateLimitRetryCount = rateLimit429Retries;
                    return streamingResult;
                }`, 1, '流式返回-浅缩进');

// 8) 退避解析 helper
edit(
`    /// <summary>
    /// 构建走指定出口代理的 Handler（连接池参数与主客户端口径一致）。
    /// </summary>`,
`    /// <summary>
    /// 429 重试的默认退避间隔与上限。
    /// </summary>
    private static readonly TimeSpan RateLimitRetryDefaultDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan RateLimitRetryMaxDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 解析 429 重试的退避间隔：优先尊重上游 Retry-After 响应头（秒数或日期，封顶 10 秒），
    /// 头缺失或非法时回退固定 1.5 秒。零延迟重击已被限流的上游只会继续吃 429 并加重封禁风险。
    /// </summary>
    internal static TimeSpan Resolve429RetryDelay(HttpResponseMessage? response)
    {
        var retryAfter = response?.Headers?.RetryAfter;
        if (retryAfter is null)
        {
            return RateLimitRetryDefaultDelay;
        }

        TimeSpan? suggested = retryAfter.Delta;
        if (suggested is null && retryAfter.Date is { } retryAt)
        {
            suggested = retryAt - DateTimeOffset.UtcNow;
        }

        if (suggested is { } value && value > TimeSpan.Zero)
        {
            return value > RateLimitRetryMaxDelay ? RateLimitRetryMaxDelay : value;
        }

        return RateLimitRetryDefaultDelay;
    }

    /// <summary>
    /// 构建走指定出口代理的 Handler（连接池参数与主客户端口径一致）。
    /// </summary>`, 1, 'helper');

fs.writeFileSync(path, src.split('\n').join(eol), 'utf8');
console.log('ProxyForwardService 完成（8 组编辑全部命中）');
