const fs = require('fs');
const path = "D:/Code/AI-Tool/src/AITool.Infrastructure/Proxy/ProxyForwardService.cs";
let raw = fs.readFileSync(path, 'utf8');
const eol = raw.includes('\r\n') ? '\r\n' : '\n';
let src = raw.replace(/\r\n/g, '\n');
const edit = (oldS, newS, expect, tag) => {
  const n = src.split(oldS).length - 1;
  if (n !== expect) throw new Error('[' + tag + '] 匹配 ' + n);
  src = src.split(oldS).join(newS);
  console.log(tag + ' OK');
};

// 通用失败返回（错误状态码且非 429 耗尽路径）：带上 429 重试计数，保持字段在所有路径真实。
edit(
`                    if (attempt >= attempts - 1)
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
                    continue;`,
`                    if (attempt >= attempts - 1)
                    {
                        return new ProxyForwardResult
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
                    continue;`, 1, '非流式通用失败');

edit(
`                    if (attempt >= attempts - 1)
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

                    continue;`,
`                    if (attempt >= attempts - 1)
                    {
                        return new ProxyForwardResult
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

                    continue;`, 1, '流式通用失败');

// 非流式不可用响应体失败
edit(
`                if (attempt == attempts - 1)
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
                }`,
`                if (attempt == attempts - 1)
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
                        ErrorMessage = BuildFailureMessage(responseBody, request.ProtocolType),
                        RateLimitRetryCount = rateLimit429Retries
                    };
                }`, 1, '非流式不可用响应');

fs.writeFileSync(path, src.split('\n').join(eol), 'utf8');
console.log('完成');
