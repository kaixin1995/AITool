using AITool.Application.Networking;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Application.Proxy;
using AITool.Application.Sites;
using AITool.Protocol;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 基于 HttpClient 的代理转发服务，将请求转发到目标站点
/// </summary>
public sealed class ProxyForwardService : IProxyForwardService
{
    /// <summary>
    /// 用于发送直连代理请求的默认 HTTP 客户端
    /// </summary>
    private readonly HttpClient _httpClient;
    /// <summary>
    /// 日志记录器，用于记录转发超时和异常
    /// </summary>
    private readonly ILogger<ProxyForwardService> _logger;
    private readonly INetworkTransport? _networkTransport;
    private readonly IForwardNetworkPolicyResolver? _networkPolicyResolver;
    private readonly IForwardCredentialResolver? _credentialResolver;

    // A single public constructor keeps typed HttpClient activation unambiguous while
    // optional dependencies preserve existing source calls using just client and logger.
    public ProxyForwardService(HttpClient httpClient, ILogger<ProxyForwardService> logger,
        INetworkTransport? networkTransport = null, IForwardNetworkPolicyResolver? networkPolicyResolver = null,
        IForwardCredentialResolver? credentialResolver = null)
    {
        httpClient.Timeout = global::System.Threading.Timeout.InfiniteTimeSpan;
        _httpClient = httpClient;
        _logger = logger;
        _networkTransport = networkTransport;
        _networkPolicyResolver = networkPolicyResolver;
        _credentialResolver = credentialResolver;
    }

    private async Task<ForwardCredentialLease> ResolveCredentialAsync(ProxyForwardRequest request, string fallbackKey, CancellationToken ct)
    {
        if (_credentialResolver is null)
        {
            if (!request.AllowNonSiteTarget || request.SiteId is not null || request.AccountIdentity is not null || !string.IsNullOrWhiteSpace(request.ManagedSource))
                throw new NetworkPolicyException("forward_credential_resolver_unavailable", "Forward credential resolver is unavailable.");
            return new ForwardCredentialLease(fallbackKey);
        }
        try { return await _credentialResolver.ResolveAsync(request, ct); }
        catch (OperationCanceledException) { throw new OperationCanceledException(ct); }
        catch (NetworkPolicyException ex) when (IsSafePolicyCode(ex.Code)) { throw new NetworkPolicyException(ex.Code, "Forward credential validation failed."); }
        catch (Exception) { throw new NetworkPolicyException("forward_credential_invalid", "Forward credential validation failed."); }
    }

    private async Task<ForwardNetworkPolicyResolution> ResolveNetworkAsync(ProxyForwardRequest request,
        CancellationToken cancellationToken)
    {
        if (_networkPolicyResolver is not null)
        {
            try { return await _networkPolicyResolver.ResolveAsync(request, cancellationToken); }
            catch (OperationCanceledException) { throw new OperationCanceledException(cancellationToken); }
            catch (NetworkPolicyException ex) when (IsSafePolicyCode(ex.Code)) { throw new NetworkPolicyException(ex.Code, "Forward network policy validation failed."); }
            catch (Exception) { throw new NetworkPolicyException("forward_network_policy_failed", "Forward network policy validation failed."); }
        }
        if (request.AccountIdentity is not null || request.NetworkContext is not null)
            throw new NetworkPolicyException("forward_network_resolver_unavailable", "The account forwarding network policy resolver is unavailable.");
        if (string.IsNullOrWhiteSpace(request.EgressProxyUrl))
            return new(ResolvedNetworkConfiguration.Legacy, "ordinary-forward", true);
        throw new NetworkPolicyException("forward_network_resolver_unavailable", "An explicit egress selection requires a forwarding network policy resolver.");
    }

    private async ValueTask<INetworkTransportLease?> AcquireNetworkAsync(ForwardNetworkPolicyResolution policy,
        CancellationToken cancellationToken)
    {
        if (policy.UseDefaultClient) return null;
        if (_networkTransport is null)
            throw new NetworkPolicyException("forward_network_transport_unavailable", "The selected forwarding network transport is unavailable.");
        try { return await _networkTransport.AcquireAsync(policy.Configuration, policy.IsolationKey, "proxy-forward", cancellationToken); }
        catch (OperationCanceledException) { throw new OperationCanceledException(cancellationToken); }
        catch (NetworkPolicyException ex) when (IsSafePolicyCode(ex.Code)) { throw new NetworkPolicyException(ex.Code, "Forward network transport acquisition failed."); }
        catch (Exception) { throw new NetworkPolicyException("forward_network_transport_failed", "Forward network transport acquisition failed."); }
    }

    private static bool IsSafePolicyCode(string? code) => code is
        "forward_credential_invalid" or "forward_credential_resolver_unavailable" or
        "account_network_revision_conflict" or "account_network_identity_conflict" or "account_not_found" or
        "ambiguous_account_owner" or "invalid_account_identity" or "invalid_isolation_key" or
        "invalid_network_selection" or "invalid_profile_revision" or "invalid_proxy_profile" or
        "invalid_proxy_profile_reference" or "invalid_egress_proxy" or "proxy_credentials_unavailable" or
        "proxy_profile_disabled" or "proxy_profile_not_found" or "proxy_profile_revision_conflict" or
        "proxy_profiles_disabled" or "forward_network_transport_unavailable" or "forward_network_resolver_unavailable";

    private sealed class StartedForward(HttpRequestMessage request, Task<HttpResponseMessage> response,
        INetworkTransportLease? networkLease, string apiKey, object? credentialSnapshot) : IAsyncDisposable
    {
        public Task<HttpResponseMessage> Response { get; } = response;
        public string ApiKey { get; } = apiKey;
        public object? CredentialSnapshot { get; } = credentialSnapshot;
        public async ValueTask DisposeAsync()
        {
            request.Dispose();
            if (networkLease is not null) await networkLease.DisposeAsync();
        }
    }

    private async Task<StartedForward> StartForwardAsync(ProxyForwardRequest request, string requestBody,
        string currentApiKey, CancellationToken ct)
    {
        using var credential = await ResolveCredentialAsync(request, currentApiKey, ct);
        // The gate can have been queued behind refresh; policy must be read after that wait.
        var policy = await ResolveNetworkAsync(request, ct);
        var lease = await AcquireNetworkAsync(policy, ct);
        HttpRequestMessage? message = null;
        try
        {
            var key = credential.UseResolvedApiKey ? credential.ApiKey : currentApiKey;
            message = BuildRequestMessage(request, requestBody, key, credential.ProtectAuthenticationHeaders);
            var response = (lease?.Client ?? _httpClient).SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            return new StartedForward(message, response, lease, key, credential.CredentialSnapshot);
        }
        catch
        {
            message?.Dispose();
            if (lease is not null) await lease.DisposeAsync();
            throw;
        }
    }

    /// <summary>
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
    /// 读取上游 Retry-After 原始秒数（不封顶，供分级冷却按真实指示休息；无头/非法为 null）。
    /// </summary>
    internal static int? TryReadRetryAfterSeconds(HttpResponseMessage? response)
    {
        var retryAfter = response?.Headers?.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return (int)Math.Min(delta.TotalSeconds, int.MaxValue);
        }

        if (retryAfter.Date is { } retryAt)
        {
            var seconds = (retryAt - DateTimeOffset.UtcNow).TotalSeconds;
            if (seconds > 0)
            {
                return (int)Math.Min(seconds, int.MaxValue);
            }
        }

        return null;
    }

    /// <summary>
    /// 将请求转发到目标站点并解析响应中的 Token 用量。
    /// 上游异常仍可按单路由重试；若调用方的取消令牌已触发，则直接结束，不再继续重试。
    /// </summary>
    public async Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        var attempts = Math.Max(0, request.RetryCount) + 1;
        var maxAttempts = attempts + (request.RefreshTargetApiKeyAsync is null ? 0 : 1);
        var tokenRefreshAttempted = false;
        var currentApiKey = request.TargetApiKey;
        var rateLimit429Count = 0;
        // 实际执行的 429 重试次数（不随连续计数归零），用于结果上报与 usage 链路展示。
        var rateLimit429Retries = 0;
        var requestBody = string.IsNullOrWhiteSpace(request.PreparedRequestBody)
            ? ModifyRequestBody(request.RequestBody, request.TargetModelName)
            : request.PreparedRequestBody;
        var isStreaming = request.EnableStreaming || IsStreamingRequest(request.RequestBody);

        // 站点标记强制上游流式（WorkBuddy code 11101 等）：客户端非流式时改写请求体加 stream:true，
        // 响应 SSE 由下方聚合逻辑还原为完整 JSON，客户端无感知。
        var forceUpstreamStream = !isStreaming
            && request.ForwardHeaders.TryGetValue(ForceStreamHeader, out var forceStreamValue)
            && string.Equals(forceStreamValue.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        if (forceUpstreamStream)
        {
            requestBody = ForceStreamTrue(requestBody);
        }

        var credentialPreparationPending = request.PrepareTargetCredentialAsync is not null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, request.RequestTimeoutSeconds)));
            var stopwatch = Stopwatch.StartNew();

            try
            {
                if (credentialPreparationPending)
                {
                    await ResolveNetworkAsync(request, timeoutCts.Token);
                    await TryPrepareTargetCredentialAsync(request, currentApiKey, cancellationToken, request.EnableStreaming);
                    credentialPreparationPending = false;
                }
                await using var started = await StartForwardAsync(request, requestBody, currentApiKey, timeoutCts.Token);
                var sentApiKey = started.ApiKey;
                using var response = await started.Response;

                if (!response.IsSuccessStatusCode)
                {
                    stopwatch.Stop();
                    string errorBody;
                    try { errorBody = await response.Content.ReadAsStringAsync(timeoutCts.Token); }
                    catch (Exception ex) when (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && ex is not OperationCanceledException && !timeoutCts.Token.IsCancellationRequested) { errorBody = string.Empty; }
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        && request.RefreshTargetApiKeyAsync is not null
                        && !tokenRefreshAttempted)
                    {
                        // Preflight before the credential refresh callback can perform network I/O.
                        await ResolveNetworkAsync(request, cancellationToken);
                        tokenRefreshAttempted = true;
                        var refreshedApiKey = await request.RefreshTargetApiKeyAsync(
                            sentApiKey,
                            cancellationToken);
                        if (!string.IsNullOrWhiteSpace(refreshedApiKey))
                        {
                            currentApiKey = refreshedApiKey;
                            credentialPreparationPending = request.PrepareTargetCredentialAsync is not null;
                            attempt--;
                            continue;
                        }
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        && request.InvalidateTargetCredentialAsync is not null)
                    {
                        try
                        {
                            await request.InvalidateTargetCredentialAsync(sentApiKey, started.CredentialSnapshot, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception ex) { _logger.LogWarning(ex, "Unable to invalidate rejected forwarding credential"); }
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden
                        && request.DisableTargetCredentialAsync is not null)
                    {
                        await TryDisableTargetCredentialAsync(request, cancellationToken);
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

                    // 429 速率限制：连续 RateLimitRetryCount 次 429 才判定该候选失败（默认 0/1 = 一次即失败）。
                    // 重试不消耗通用重试预算；预算耗尽后立即返回失败，由上层立刻顺位到下一个路由候选，
                    // 避免对同一个已被限流的上游继续消耗通用重试（既浪费又加重封禁风险）。
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        rateLimit429Count++;
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
                        }

                        return new ProxyForwardResult
                        {
                            Success = false,
                            StatusCode = (int)response.StatusCode,
                            ResponseBody = errorBody,
                            TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                            IsStreaming = isStreaming,
                            ErrorMessage = errorBody,
                            RateLimitRetryCount = rateLimit429Retries,
                            RetryAfterSeconds = TryReadRetryAfterSeconds(response)
                        };
                    }
                    else
                    {
                        rateLimit429Count = 0;
                    }

                    if (attempt >= attempts - 1)
                    {
                        return new ProxyForwardResult
                        {
                            Success = false,
                            StatusCode = (int)response.StatusCode,
                            ResponseBody = errorBody,
                            TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                            IsStreaming = isStreaming,
                            ErrorMessage = errorBody,
                            RateLimitRetryCount = rateLimit429Retries,
                            RetryAfterSeconds = TryReadRetryAfterSeconds(response)
                        };
                    }
                    continue;
                }

                if (isStreaming)
                {
                    var streamingResult = await ProcessStreamingResponseAsync(
                        response,
                        stopwatch,
                        request,
                        isStreaming,
                        null,
                        cancellationToken);

                    if (streamingResult.Success
                        || streamingResult.IsCanceled
                        || streamingResult.HasStartedStreaming
                        || attempt >= attempts - 1)
                    {
                        streamingResult.RateLimitRetryCount = rateLimit429Retries;
                        return streamingResult;
                    }

                    continue;
                }

                var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                stopwatch.Stop();

                // 上游(Codex /responses）强制 stream=true，客户端非流式时上游会返回 SSE 流；
                // 透明聚合成完整 JSON，上层 usage 提取与协议转换均无感。
                if (!isStreaming && IsSseContent(response, responseBody))
                {
                    var aggregated = TryExtractResponsesCompletion(responseBody);
                    if (string.IsNullOrEmpty(aggregated))
                    {
                        // OpenAI Chat Completions 格式的 SSE（WorkBuddy 等强制流式上游）：
                        // 聚合 chat.completion.chunk 为完整 chat.completion 对象。
                        aggregated = TryAggregateChatCompletionSse(responseBody);
                    }

                    if (!string.IsNullOrEmpty(aggregated))
                    {
                        responseBody = aggregated;
                    }
                    else
                    {
                        _logger.LogWarning("Failed to aggregate upstream SSE into response JSON.");
                        return new ProxyForwardResult { Success = false, StatusCode = 502, ErrorMessage = "Upstream stream was incomplete or invalid.", IsStreaming = false, TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds) };
                    }
                }

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
                        TotalDurationMs = totalDurationMs,
                        RateLimitRetryCount = rateLimit429Retries
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
                        ErrorMessage = BuildFailureMessage(responseBody, request.ProtocolType),
                        RateLimitRetryCount = rateLimit429Retries
                    };
                }
            }
            catch (UpstreamReferenceException)
            {
                // A failed pre-exposure ownership commit is terminal, even before first content.
                throw;
            }
            catch (NetworkPolicyException ex)
            {
                return new ProxyForwardResult
                {
                    Success = false,
                    StatusCode = 503,
                    IsStreaming = request.EnableStreaming,
                    ErrorMessage = $"Network policy blocked forwarding ({ex.Code})."
                };
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // 客户端已经主动取消当前请求，此时继续内部重试或让上层回退后续路由都没有意义。
                stopwatch.Stop();
                return new ProxyForwardResult
                {
                    Success = false,
                    TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                    IsStreaming = isStreaming,
                    IsCanceled = true,
                    ErrorMessage = ex.Message
                };
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
                // 客户端断开（token 取消或 IO 异常）：不重试、不 fallback，直接返回取消。
                if (cancellationToken.IsCancellationRequested
                    || ex is System.IO.IOException
                    || ex is ObjectDisposedException)
                {
                    return new ProxyForwardResult
                    {
                        Success = false,
                        TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                        IsCanceled = true,
                        ErrorMessage = ex.Message
                    };
                }
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
        var maxAttempts = attempts + (request.RefreshTargetApiKeyAsync is null ? 0 : 1);
        var tokenRefreshAttempted = false;
        var currentApiKey = request.TargetApiKey;
        var rateLimit429Count = 0;
        // 实际执行的 429 重试次数（不随连续计数归零），用于结果上报与 usage 链路展示。
        var rateLimit429Retries = 0;
        var requestBody = string.IsNullOrWhiteSpace(request.PreparedRequestBody)
            ? ModifyRequestBody(request.RequestBody, request.TargetModelName)
            : request.PreparedRequestBody;

        var credentialPreparationPending = request.PrepareTargetCredentialAsync is not null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, request.RequestTimeoutSeconds)));
            var stopwatch = Stopwatch.StartNew();

            try
            {
                if (credentialPreparationPending)
                {
                    await ResolveNetworkAsync(request, timeoutCts.Token);
                    await TryPrepareTargetCredentialAsync(request, currentApiKey, cancellationToken, request.EnableStreaming);
                    credentialPreparationPending = false;
                }
                await using var started = await StartForwardAsync(request, requestBody, currentApiKey, timeoutCts.Token);
                var sentApiKey = started.ApiKey;
                using var response = await started.Response;

                if (!response.IsSuccessStatusCode)
                {
                    stopwatch.Stop();
                    string errorBody;
                    try { errorBody = await response.Content.ReadAsStringAsync(timeoutCts.Token); }
                    catch (Exception ex) when (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && ex is not OperationCanceledException && !timeoutCts.Token.IsCancellationRequested) { errorBody = string.Empty; }
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        && request.RefreshTargetApiKeyAsync is not null
                        && !tokenRefreshAttempted)
                    {
                        // Preflight before the credential refresh callback can perform network I/O.
                        await ResolveNetworkAsync(request, cancellationToken);
                        tokenRefreshAttempted = true;
                        var refreshedApiKey = await request.RefreshTargetApiKeyAsync(
                            sentApiKey,
                            cancellationToken);
                        if (!string.IsNullOrWhiteSpace(refreshedApiKey))
                        {
                            currentApiKey = refreshedApiKey;
                            credentialPreparationPending = request.PrepareTargetCredentialAsync is not null;
                            attempt--;
                            continue;
                        }
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        && request.InvalidateTargetCredentialAsync is not null)
                    {
                        try
                        {
                            await request.InvalidateTargetCredentialAsync(sentApiKey, started.CredentialSnapshot, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception ex) { _logger.LogWarning(ex, "Unable to invalidate rejected forwarding credential"); }
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden
                        && request.DisableTargetCredentialAsync is not null)
                    {
                        await TryDisableTargetCredentialAsync(request, cancellationToken);
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

                    // 429 速率限制：连续 RateLimitRetryCount 次 429 才判定该候选失败（默认 0/1 = 一次即失败）。
                    // 预算耗尽后立即返回失败，由上层立刻顺位到下一个路由候选。
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        rateLimit429Count++;
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
                        }

                        return new ProxyForwardResult
                        {
                            Success = false,
                            StatusCode = (int)response.StatusCode,
                            ResponseBody = errorBody,
                            TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                            IsStreaming = true,
                            ErrorMessage = errorBody,
                            RateLimitRetryCount = rateLimit429Retries,
                            RetryAfterSeconds = TryReadRetryAfterSeconds(response)
                        };
                    }
                    else
                    {
                        rateLimit429Count = 0;
                    }

                    if (attempt >= attempts - 1)
                    {
                        return new ProxyForwardResult
                        {
                            Success = false,
                            StatusCode = (int)response.StatusCode,
                            ResponseBody = errorBody,
                            TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                            IsStreaming = true,
                            ErrorMessage = errorBody,
                            RateLimitRetryCount = rateLimit429Retries,
                            RetryAfterSeconds = TryReadRetryAfterSeconds(response)
                        };
                    }

                    continue;
                }

                var streamingResult = await ProcessStreamingResponseAsync(
                    response,
                    stopwatch,
                    request,
                    true,
                    onSseDataAsync,
                    cancellationToken);

                if (streamingResult.Success
                    || streamingResult.IsCanceled
                    || streamingResult.HasStartedStreaming
                    || attempt >= attempts - 1)
                {
                    streamingResult.RateLimitRetryCount = rateLimit429Retries;
                    return streamingResult;
                }

                continue;
            }
            catch (UpstreamReferenceException)
            {
                // A failed pre-exposure ownership commit is terminal, even before first content.
                throw;
            }
            catch (NetworkPolicyException ex)
            {
                return new ProxyForwardResult
                {
                    Success = false,
                    StatusCode = 503,
                    IsStreaming = request.EnableStreaming,
                    ErrorMessage = $"Network policy blocked forwarding ({ex.Code})."
                };
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // 客户端已经主动取消当前请求，此时继续内部重试或让上层回退后续路由都没有意义。
                stopwatch.Stop();
                return new ProxyForwardResult
                {
                    Success = false,
                    TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                    IsStreaming = true,
                    IsCanceled = true,
                    ErrorMessage = ex.Message
                };
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
                // 客户端断开（token 取消或 IO 异常）：不重试、不 fallback，直接返回取消。
                if (cancellationToken.IsCancellationRequested
                    || ex is System.IO.IOException
                    || ex is ObjectDisposedException)
                {
                    return new ProxyForwardResult
                    {
                        Success = false,
                        TotalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds),
                        IsStreaming = true,
                        IsCanceled = true,
                        ErrorMessage = ex.Message
                    };
                }
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

    private async Task TryDisableTargetCredentialAsync(
        ProxyForwardRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await request.DisableTargetCredentialAsync!(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Unable to auto-disable target credential after upstream 403");
        }
    }

    private async Task TryPrepareTargetCredentialAsync(
        ProxyForwardRequest request,
        string apiKey,
        CancellationToken cancellationToken,
        bool isStreaming)
    {
        if (request.PrepareTargetCredentialAsync is null)
        {
            return;
        }

        try
        {
            await request.PrepareTargetCredentialAsync(apiKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NetworkPolicyException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                isStreaming
                    ? "托管凭证流式请求前准备失败，继续尝试上游请求。Target={Target}"
                    : "托管凭证请求前准备失败，继续尝试上游请求。Target={Target}",
                request.TargetBaseUrl);
        }
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

        // 流式空闲超时（默认关闭）：每次成功读取后重新计时，防止上游"发完响应头后挂起"
        // 永久占用连接与并发槽。用单个 linked CTS 复用计时器，避免逐行分配。
        var idleTimeout = request.StreamIdleTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(request.StreamIdleTimeoutSeconds)
            : TimeSpan.Zero;
        CancellationTokenSource? idleCts = idleTimeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;

        try
        {
            while (true)
            {
                string? line;
                if (idleCts is not null)
                {
                    idleCts.CancelAfter(idleTimeout);
                    try
                    {
                        line = await reader.ReadLineAsync(idleCts.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        stopwatch.Stop();
                        totalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds);
                        var idleMessage = $"上游流式响应空闲超过 {request.StreamIdleTimeoutSeconds} 秒，已判定挂起并终止";
                        _logger.LogWarning(
                            "代理流空闲超时终止。Protocol={Protocol}, Target={Target}, IdleSeconds={Seconds}, HasContent={HasContent}",
                            request.ProtocolType, request.TargetBaseUrl, request.StreamIdleTimeoutSeconds, hasFirstContent);
                        return new ProxyForwardResult
                        {
                            // 已有内容时按"中断"处理（客户端已收到部分数据，不算路由失败）；
                            // 尚无内容时按失败处理，允许上层回退到下一条路由。
                            Success = hasFirstContent,
                            StatusCode = (int)response.StatusCode,
                            ResponseBody = sb.ToString(),
                            InputTokens = inputTokens,
                            CachedTokens = cachedTokens,
                            OutputTokens = outputTokens,
                            IsStreaming = isStreaming,
                            HasStartedStreaming = hasFirstContent,
                            IsStreamInterrupted = hasFirstContent,
                            FirstTokenLatencyMs = firstTokenLatencyMs,
                            StreamDurationMs = Math.Max(0, totalDurationMs - firstTokenLatencyMs),
                            TotalDurationMs = totalDurationMs,
                            ErrorMessage = idleMessage
                        };
                    }
                }
                else
                {
                    line = await reader.ReadLineAsync(cancellationToken);
                }

                if (line == null) break;

                // 仅累积诊断副本，达到上限后停止追加（转发本身不受影响）。
                if (sb.Length < ProxyForwardConstants.MaxStreamBodyCaptureChars)
                {
                    sb.AppendLine(line);
                }

                if (onSseDataAsync is not null)
                {
                    await onSseDataAsync(line, cancellationToken);
                }

                // 跳过空行和注释行
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith(':')) continue;

                // SSE 格式：data: {...}（规范允许无空格写法 "data:{...}"，统一兼容）
                if (!ProxyProtocolBridge.TryExtractSseFieldPayload(line, "data", out var jsonText)) continue;

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

                    // Responses 协议（如 Codex 上游）以 response.completed 事件结束流，而非 [DONE]。
                    // 不限定协议类型：个别 OpenAI 协议中间层（包装 Responses 上游）也会以该事件收尾。
                    if (root.TryGetProperty("type", out var responsesEventType)
                        && string.Equals(responsesEventType.GetString(), "response.completed", StringComparison.OrdinalIgnoreCase))
                    {
                        receivedDoneEvent = true;
                    }

                    // usage 兼容顶层与 response.usage 嵌套两种形态（newapi 类中间层）；
                    // "usage": null 分片跳过（include_usage 开启后常规分片会带 null）。
                    var usageElement = root.TryGetProperty("usage", out var topLevelUsage) && topLevelUsage.ValueKind == JsonValueKind.Object
                        ? topLevelUsage
                        : root.TryGetProperty("response", out var responseWrapper)
                          && responseWrapper.ValueKind == JsonValueKind.Object
                          && responseWrapper.TryGetProperty("usage", out var nestedUsage)
                          && nestedUsage.ValueKind == JsonValueKind.Object
                            ? nestedUsage
                            : default;
                    if (usageElement.ValueKind == JsonValueKind.Object)
                    {
                        var extracted = ExtractUsageFromElement(usageElement, request.ProtocolType);
                        if (extracted.InputTokens > 0) inputTokens = extracted.InputTokens;
                        if (extracted.CachedTokens > 0) cachedTokens = extracted.CachedTokens;
                        if (extracted.OutputTokens > 0) outputTokens = extracted.OutputTokens;
                    }

                    // Gemini 上游（v1internal 封套）：usage 用 usageMetadata 表达，且流没有
                    // [DONE]/message_stop 标记——candidates[0].finishReason 出现即视为正常完成。
                    if (string.Equals(request.ProtocolType, "Gemini", StringComparison.OrdinalIgnoreCase))
                    {
                        var geminiUsage = root.TryGetProperty("usageMetadata", out var topMeta) && topMeta.ValueKind == JsonValueKind.Object
                            ? topMeta
                            : root.TryGetProperty("response", out var geminiWrapper)
                              && geminiWrapper.ValueKind == JsonValueKind.Object
                              && geminiWrapper.TryGetProperty("usageMetadata", out var nestedMeta)
                              && nestedMeta.ValueKind == JsonValueKind.Object
                                ? nestedMeta
                                : default;
                        if (geminiUsage.ValueKind == JsonValueKind.Object)
                        {
                            var extracted = ExtractUsageFromElement(geminiUsage, request.ProtocolType);
                            if (extracted.InputTokens > 0) inputTokens = extracted.InputTokens;
                            if (extracted.CachedTokens > 0) cachedTokens = extracted.CachedTokens;
                            if (extracted.OutputTokens > 0) outputTokens = extracted.OutputTokens;
                        }

                        var geminiCandidates = root.TryGetProperty("response", out var wrapperForCandidates)
                            && wrapperForCandidates.ValueKind == JsonValueKind.Object
                            && wrapperForCandidates.TryGetProperty("candidates", out var wrappedCandidates)
                            && wrappedCandidates.ValueKind == JsonValueKind.Array
                                ? wrappedCandidates
                                : root.TryGetProperty("candidates", out var directCandidates)
                                  && directCandidates.ValueKind == JsonValueKind.Array
                                    ? directCandidates
                                    : default;
                        if (geminiCandidates.ValueKind == JsonValueKind.Array && geminiCandidates.GetArrayLength() > 0
                            && geminiCandidates[0].ValueKind == JsonValueKind.Object
                            && geminiCandidates[0].TryGetProperty("finishReason", out var geminiFinish)
                            && geminiFinish.ValueKind == JsonValueKind.String)
                        {
                            receivedDoneEvent = true;
                        }
                    }

                    // Anthropic message_start 事件中的 usage 嵌套在 message 里
                    if (request.ProtocolType == "Anthropic"
                        && root.TryGetProperty("message", out var message)
                        && message.ValueKind == JsonValueKind.Object
                        && message.TryGetProperty("usage", out var msgUsage)
                        && msgUsage.ValueKind == JsonValueKind.Object)
                    {
                        var extracted = ExtractUsageFromElement(msgUsage, request.ProtocolType);
                        if (extracted.InputTokens > 0) inputTokens = extracted.InputTokens;
                        // 缓存桶不能漏：message_start 通常同时携带 input 与 cache_read/cache_creation，
                        // 只更新 input/output 会把缓存用量整体丢掉。
                        if (extracted.CachedTokens > 0) cachedTokens = extracted.CachedTokens;
                        if (extracted.OutputTokens > 0) outputTokens = extracted.OutputTokens;
                    }
                }
                catch
                {
                    // 非 JSON 的 data 行忽略
                }
            }
        }
        catch (UpstreamReferenceException)
        {
            // Preserve the terminal guard exception for the controller; never expose or retry output.
            throw;
        }
        catch (NetworkPolicyException)
        {
            // Let the outer attempt return its blocked-policy result without retrying this stream.
            throw;
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            totalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds);
            return new ProxyForwardResult
            {
                Success = false,
                StatusCode = (int)response.StatusCode,
                ResponseBody = sb.ToString(),
                InputTokens = inputTokens,
                CachedTokens = cachedTokens,
                OutputTokens = outputTokens,
                IsStreaming = isStreaming,
                HasStartedStreaming = hasFirstContent,
                IsStreamInterrupted = hasFirstContent,
                IsCanceled = true,
                FirstTokenLatencyMs = firstTokenLatencyMs,
                StreamDurationMs = Math.Max(0, totalDurationMs - firstTokenLatencyMs),
                TotalDurationMs = totalDurationMs,
                ErrorMessage = ex.Message
            };
        }
        catch (Exception ex) when (hasFirstContent)
        {
            stopwatch.Stop();
            totalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds);

            // 客户端断开（token 取消，或往 Response 写抛 IOException/ObjectDisposedException）
            // 识别为取消，而非成功/中断——避免上层误判为成功或继续 fallback 后续路由。
            if (cancellationToken.IsCancellationRequested
                || ex is System.IO.IOException
                || ex is ObjectDisposedException)
            {
                return new ProxyForwardResult
                {
                    Success = false,
                    StatusCode = (int)response.StatusCode,
                    ResponseBody = sb.ToString(),
                    InputTokens = inputTokens,
                    CachedTokens = cachedTokens,
                    OutputTokens = outputTokens,
                    IsStreaming = isStreaming,
                    HasStartedStreaming = true,
                    IsStreamInterrupted = true,
                    IsCanceled = true,
                    FirstTokenLatencyMs = firstTokenLatencyMs,
                    StreamDurationMs = Math.Max(0, totalDurationMs - firstTokenLatencyMs),
                    TotalDurationMs = totalDurationMs,
                    ErrorMessage = ex.Message
                };
            }

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
        finally
        {
            idleCts?.Dispose();
        }

        stopwatch.Stop();
        totalDurationMs = (int)Math.Max(0, stopwatch.ElapsedMilliseconds);
        var streamCompletedNormally = request.ProtocolType == "Anthropic"
            ? receivedAnthropicMessageStop
            : receivedDoneEvent;
        var streamHasUsableResponse = streamCompletedNormally || hasFirstContent;

        return new ProxyForwardResult
        {
            Success = streamHasUsableResponse,
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
            ErrorMessage = !streamCompletedNormally
                ? hasFirstContent
                    ? "stream interrupted before normal completion"
                    : "empty stream response without completion event"
                : null
        };
    }

    /// <summary>
    /// 构建发送到上游的 HTTP 请求对象
    /// </summary>
    private static HttpRequestMessage BuildRequestMessage(ProxyForwardRequest request, string requestBody, string apiKey, bool trustedDeviceCredential)
    {
        var targetPath = string.IsNullOrWhiteSpace(request.TargetPath)
            ? request.ProtocolType == "Anthropic"
                ? SiteEndpointPathResolver.ResolvePath(request.TargetEndpointPathMode, "messages")
                : string.Equals(request.ProtocolType, "Responses", StringComparison.OrdinalIgnoreCase)
                    ? SiteEndpointPathResolver.ResolvePath(request.TargetEndpointPathMode, "responses")
                    : SiteEndpointPathResolver.ResolvePath(request.TargetEndpointPathMode, "chat/completions")
            : request.TargetPath!;
        var targetUrl = $"{request.TargetBaseUrl.TrimEnd('/')}/{targetPath.TrimStart('/')}";

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, targetUrl)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };

        // 根据协议类型设置认证头
        if (request.ProtocolType == "Anthropic")
        {
            if (!string.IsNullOrEmpty(apiKey))
            {
                foreach (var (name, value) in ResolveAnthropicAuthHeaders(apiKey, request.ForwardHeaders))
                {
                    httpRequest.Headers.TryAddWithoutValidation(name, value);
                }
            }

            httpRequest.Headers.Add(
                "anthropic-version",
                request.ForwardHeaders.TryGetValue("anthropic-version", out var anthropicVersion) && !string.IsNullOrWhiteSpace(anthropicVersion)
                    ? anthropicVersion
                    : "2023-06-01");
        }
        else
        {
            if (!string.IsNullOrEmpty(apiKey))
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
        }

        foreach (var header in request.ForwardHeaders)
        {
            if (trustedDeviceCredential && (string.Equals(header.Key, "authorization", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "x-api-key", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (string.Equals(header.Key, "anthropic-version", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 内部标记头（鉴权形态选择），不透传给上游。
            if (string.Equals(header.Key, AuthModeHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 内部标记头（强制上游流式），不透传给上游。
            if (string.Equals(header.Key, ForceStreamHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(header.Key, "authorization", StringComparison.OrdinalIgnoreCase))
            {
                httpRequest.Headers.Remove("Authorization");
            }

            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return httpRequest;
    }

    /// <summary>
    /// 站点侧通过 ExtraHeadersJson 携带 <c>X-AITool-Auth-Mode</c> 选择 Anthropic 鉴权形态：
    /// 默认 x-api-key；<c>bearer</c> 时改发 Authorization Bearer（Claude OAuth 订阅、
    /// MiniMax Code、Z.ai /api/anthropic 等以 Bearer 为准的上游）。
    /// </summary>
    public const string AuthModeHeader = "X-AITool-Auth-Mode";

    /// <summary>
    /// 站点侧通过 ExtraHeadersJson 携带 <c>X-AITool-Force-Stream</c> 强制上游流式请求：
    /// WorkBuddy 等上游只接受 stream:true（code 11101），客户端非流式时由转发层改写请求体
    /// 强制 stream:true，并将返回的 SSE 聚合回完整 JSON。
    /// </summary>
    public const string ForceStreamHeader = "X-AITool-Force-Stream";

    /// <summary>解析 Anthropic 协议的鉴权头集合（key 形态：默认 x-api-key / bearer 形态：Authorization Bearer）。</summary>
    public static IEnumerable<(string Name, string Value)> ResolveAnthropicAuthHeaders(
        string apiKey,
        IReadOnlyDictionary<string, string> forwardHeaders)
    {
        var bearerMode = forwardHeaders.TryGetValue(AuthModeHeader, out var mode)
                         && string.Equals(mode.Trim(), "bearer", StringComparison.OrdinalIgnoreCase);
        if (bearerMode)
        {
            yield return ("Authorization", "Bearer " + apiKey);
        }
        else
        {
            yield return ("x-api-key", apiKey);
        }
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
    /// 强制改写请求体为 stream:true（保留其余字段不变），并要求上游在流尾附带 usage
    /// （stream_options.include_usage，OpenAI 规范下流式默认不带 usage，聚合侧会拿不到计量）。
    /// 请求体不是合法 JSON 时返回原文（上游自行报错）。
    /// </summary>
    private static string ForceStreamTrue(string requestBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return requestBody;
            }

            if (root.TryGetProperty("stream", out var streamEl)
                && (streamEl.ValueKind == JsonValueKind.True
                    || (streamEl.ValueKind == JsonValueKind.String && streamEl.GetString() == "true")))
            {
                return requestBody;
            }

            var node = System.Text.Json.Nodes.JsonNode.Parse(requestBody) as System.Text.Json.Nodes.JsonObject;
            if (node is null)
            {
                return requestBody;
            }

            node["stream"] = true;
            // 兼容已有 stream_options：只补 include_usage，不覆盖其他选项。
            if (node["stream_options"] is System.Text.Json.Nodes.JsonObject options)
            {
                options["include_usage"] = true;
            }
            else
            {
                node["stream_options"] = new System.Text.Json.Nodes.JsonObject { ["include_usage"] = true };
            }
            return node.ToJsonString();
        }
        catch (JsonException)
        {
            return requestBody;
        }
    }

    /// <summary>
    /// 把 OpenAI Chat Completions 的 SSE 流聚合回完整 chat.completion JSON：
    /// 逐行解析 data: 前缀的 chat.completion.chunk，累积 delta 文本/role/tool_calls/usage，
    /// 构造完整响应对象。无法解析时返回 null。
    /// </summary>
    internal static string? TryAggregateChatCompletionSse(string sseBody)
    {
        try
        {
            string? id = null, model = null, systemFingerprint = null;
            long? promptTokens = null, completionTokens = null;
            // 多 choice（客户端 n>1）按 choice.index 分桶聚合，不能只取 choices[0]。
            var choicesByIndex = new SortedDictionary<int, ChoiceAccumulator>();
            var hasAnyChunk = false;



            foreach (var rawLine in sseBody.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var payload = line["data:".Length..].Trim();
                if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {

                    break;
                }

                if (payload.Length == 0) continue;
                // Only empty data is a keepalive; malformed nonempty data can hide lost content.
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(payload);
                }
                catch (JsonException) { return null; }

                using (doc)
                {
                    var chunk = doc.RootElement;
                    if (chunk.ValueKind == JsonValueKind.Object && chunk.TryGetProperty("error", out _)) return null;
                    if (chunk.ValueKind != JsonValueKind.Object
                        || !chunk.TryGetProperty("object", out var objectEl)
                        || !string.Equals(objectEl.GetString(), "chat.completion.chunk", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    hasAnyChunk = true;
                    if (chunk.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String) id ??= idEl.GetString();
                    if (chunk.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String) model ??= modelEl.GetString();
                    if (chunk.TryGetProperty("system_fingerprint", out var fpEl) && fpEl.ValueKind == JsonValueKind.String) systemFingerprint ??= fpEl.GetString();

                    if (chunk.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                    {
                        if (usageEl.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt64(out var ptv)) promptTokens = ptv;
                        if (usageEl.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt64(out var ctv)) completionTokens = ctv;
                    }

                    if (!chunk.TryGetProperty("choices", out var choicesEl) || choicesEl.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var choice in choicesEl.EnumerateArray())
                    {
                        if (choice.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var choiceIndex = choice.TryGetProperty("index", out var cIdxEl)
                                          && cIdxEl.ValueKind == JsonValueKind.Number
                                          && cIdxEl.TryGetInt32(out var cIdx)
                            ? cIdx
                            : 0;
                        if (!choicesByIndex.TryGetValue(choiceIndex, out var acc))
                        {
                            acc = new ChoiceAccumulator();
                            choicesByIndex[choiceIndex] = acc;
                        }

                        if (choice.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(frEl.GetString()))
                        {
                            acc.FinishReason ??= frEl.GetString();

                        }

                        if (!choice.TryGetProperty("delta", out var deltaEl) || deltaEl.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        if (deltaEl.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String)
                        {
                            acc.Role ??= roleEl.GetString();
                        }

                        if (deltaEl.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                        {
                            acc.Content.Append(contentEl.GetString());
                        }

                        if (deltaEl.TryGetProperty("reasoning_content", out var rcEl) && rcEl.ValueKind == JsonValueKind.String)
                        {
                            acc.Reasoning.Append(rcEl.GetString());
                        }

                        if (deltaEl.TryGetProperty("tool_calls", out var tcEl) && tcEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tc in tcEl.EnumerateArray())
                            {
                                if (tc.ValueKind != JsonValueKind.Object)
                                {
                                    continue;
                                }

                                var callIndex = tc.TryGetProperty("index", out var idxEl)
                                                && idxEl.ValueKind == JsonValueKind.Number
                                                && idxEl.TryGetInt32(out var idx)
                                    ? idx
                                    : 0;
                                if (!acc.ToolCallsByIndex.TryGetValue(callIndex, out var tcAcc))
                                {
                                    tcAcc = new ToolCallAccumulator();
                                    acc.ToolCallsByIndex[callIndex] = tcAcc;
                                }

                                // id/type/name 只在首个分片出现，取首个非空；arguments 逐段拼接。
                                if (tc.TryGetProperty("id", out var tcId) && tcId.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(tcId.GetString()))
                                {
                                    tcAcc.Id ??= tcId.GetString();
                                }
                                if (tc.TryGetProperty("type", out var tcType) && tcType.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(tcType.GetString()))
                                {
                                    tcAcc.Type ??= tcType.GetString();
                                }
                                if (tc.TryGetProperty("function", out var fnEl) && fnEl.ValueKind == JsonValueKind.Object)
                                {
                                    if (fnEl.TryGetProperty("name", out var fnName) && fnName.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(fnName.GetString()))
                                    {
                                        tcAcc.Name ??= fnName.GetString();
                                    }
                                    if (fnEl.TryGetProperty("arguments", out var fnArgs) && fnArgs.ValueKind == JsonValueKind.String)
                                    {
                                        tcAcc.Arguments.Append(fnArgs.GetString());
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (!hasAnyChunk || choicesByIndex.Count == 0 || choicesByIndex.Values.Any(choice => string.IsNullOrWhiteSpace(choice.FinishReason)))
            {
                return null;
            }

            var result = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = id ?? $"chatcmpl-{Guid.NewGuid():N}",
                ["object"] = "chat.completion",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = model ?? "unknown",
            };
            if (!string.IsNullOrEmpty(systemFingerprint))
            {
                result["system_fingerprint"] = systemFingerprint;
            }

            // Every choice must have an explicit finish reason before a non-streaming success.


            var choicesArray = new System.Text.Json.Nodes.JsonArray();
            foreach (var (choiceIndex, acc) in choicesByIndex)
            {
                var message = new System.Text.Json.Nodes.JsonObject
                {
                    ["role"] = acc.Role ?? "assistant",
                    ["content"] = acc.Content.Length > 0 ? acc.Content.ToString() : null,
                };
                if (acc.Reasoning.Length > 0)
                {
                    message["reasoning_content"] = acc.Reasoning.ToString();
                }
                if (acc.ToolCallsByIndex.Count > 0)
                {
                    // 对齐非流式 chat.completion 的 tool_calls 形态（id/type/function，无 index 字段）。
                    var mergedToolCalls = new System.Text.Json.Nodes.JsonArray();
                    foreach (var tcAcc in acc.ToolCallsByIndex.Values)
                    {
                        var call = new System.Text.Json.Nodes.JsonObject();
                        if (tcAcc.Id is not null)
                        {
                            call["id"] = tcAcc.Id;
                        }
                        if (tcAcc.Type is not null)
                        {
                            call["type"] = tcAcc.Type;
                        }
                        call["function"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["name"] = tcAcc.Name ?? string.Empty,
                            ["arguments"] = tcAcc.Arguments.ToString(),
                        };
                        mergedToolCalls.Add(call);
                    }
                    message["tool_calls"] = mergedToolCalls;
                }

                choicesArray.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["index"] = choiceIndex,
                    ["message"] = message,
                    ["finish_reason"] = acc.FinishReason,
                });
            }
            result["choices"] = choicesArray;

            if (promptTokens.HasValue || completionTokens.HasValue)
            {
                result["usage"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["prompt_tokens"] = promptTokens ?? 0,
                    ["completion_tokens"] = completionTokens ?? 0,
                    ["total_tokens"] = (promptTokens ?? 0) + (completionTokens ?? 0),
                };
            }

            return result.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>单 choice 的流式聚合累积器：delta 文本/reasoning/role 与按 index 合并的 tool_calls。</summary>
    private sealed class ChoiceAccumulator
    {
        public string? Role { get; set; }
        public string? FinishReason { get; set; }
        public StringBuilder Content { get; } = new();
        public StringBuilder Reasoning { get; } = new();
        public SortedDictionary<int, ToolCallAccumulator> ToolCallsByIndex { get; } = new();
    }

    /// <summary>流式 tool_calls 分片累积器：id/type/name 取首个非空，arguments 逐段拼接。</summary>
    private sealed class ToolCallAccumulator
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }

    /// <summary>
    /// 判断上游响应是否为 SSE 流（content-type 为 text/event-stream，或正文以 event:/data: 开头）。
    /// 用于识别 Codex /responses 等强制 stream=true 的上游在客户端非流式请求下返回的 SSE。
    /// </summary>
    private static bool IsSseContent(HttpResponseMessage response, string responseBody)
    {
        if (string.Equals(response.Content.Headers.ContentType?.MediaType,
                "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var span = responseBody.AsSpan().TrimStart();
        return span.StartsWith("event:", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从上游 Responses SSE 流中提取 response.completed 事件携带的完整 response 对象（JSON 文本）。
    /// Codex /responses 强制 stream=true，客户端非流式时用它把流聚合成完整 JSON 响应体。
    /// 找不到 response.completed 时返回 null（由调用方走原有失败路径）。
    /// </summary>
    private static string? TryExtractResponsesCompletion(string sseBody)
    {
        // Codex SSE 的事件类型在 data.type（不一定有 event: 行），与 TryExtractResponsesCompletedOutput 对齐。
        // Codex 实际每个 data: 行就是一个完整的事件 JSON（流式逐行解析也依赖这一点），
        // 且事件之间不一定有空行分隔，因此不能依赖空行分块。
        // 这里逐行处理：先累积 output_text.delta 文本，每个 data 行再尝试独立解析，
        // 命中 response.completed 即返回；解析失败（JSON 跨多行）则累积后重试，兼容标准 SSE 多 data 行事件块。
        // 注意：delta 累积只在主循环对独立 data 行做一次，不能放进 TryParsePayload，
        // 否则独立解析和 join 重试会重复累积同一 delta。
        var pending = new List<string>();
        // 累积 response.output_text.delta 的文本片段，用于在 response.completed.output 为空时重建 message。
        // Codex 上游的 response.completed.response.output 始终为 []，内容只在 delta 事件里。
        var deltaBuilder = new StringBuilder();

        string? TryParsePayload(string payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)
                    || typeEl.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                var eventType = typeEl.GetString();

                if (!string.Equals(eventType, "response.completed", StringComparison.OrdinalIgnoreCase)
                    || !root.TryGetProperty("response", out var response)
                    || response.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                // 命中 response.completed，取 response 对象。
                // 若 output 为空但有 delta 文本，从 delta 重建 message 项塞进 output，
                // 否则上层 HasUsableResponse 会判为空响应（"no usable choices"）。
                var responseText = response.GetRawText();
                if (deltaBuilder.Length == 0)
                {
                    return responseText;
                }

                var hasOutput = response.TryGetProperty("output", out var outputEl)
                    && outputEl.ValueKind == JsonValueKind.Array
                    && outputEl.GetArrayLength() > 0;
                if (hasOutput)
                {
                    return responseText;
                }

                return RebuildResponseWithDeltaMessage(responseText, deltaBuilder.ToString());
            }
            catch
            {
                // 非 JSON 或不匹配，继续累积
            }
            return null;
        }

        foreach (var rawLine in sseBody.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                // 空行仅作为事件块分隔，清空累积后继续（不依赖它触发判定）
                pending.Clear();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                // event: 行与注释行忽略，事件类型统一从 data.type 判定
                continue;
            }

            var data = line.Length > 5 ? line[5..] : string.Empty;
            if (data.StartsWith(' '))
            {
                data = data[1..];
            }

            // 先把当前 data 行当作完整事件独立解析（Codex / OpenAI 流式常见格式，每行即完整 JSON）。
            // delta 累积只在独立解析时做一次，避免 join 重试时重复累积。
            if (TryAccumulateDelta(data, deltaBuilder))
            {
                pending.Clear();
                continue;
            }

            var single = TryParsePayload(data);
            if (single is not null)
            {
                return single;
            }

            // 解析失败则累积跨行 JSON，重新尝试（兜底标准 SSE 多 data 行事件块）
            pending.Add(data);
            var joined = string.Join("\n", pending);
            var multi = TryParsePayload(joined);
            if (multi is not null)
            {
                return multi;
            }
        }

        return null;
    }

    /// <summary>
    /// 若 data 行是 response.output_text.delta 事件，把 delta 文本累积到 builder 并返回 true；
    /// 否则返回 false。单独提取出来避免在 TryParsePayload 的 join 重试路径里重复累积。
    /// </summary>
    private static bool TryAccumulateDelta(string payload, StringBuilder deltaBuilder)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (!string.Equals(typeEl.GetString(), "response.output_text.delta", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (root.TryGetProperty("delta", out var deltaEl) && deltaEl.ValueKind == JsonValueKind.String)
            {
                deltaBuilder.Append(deltaEl.GetString());
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在 response.completed 的 response 对象里重建 output message（当上游 output 为空但有 delta 文本时）。
    /// 用 JsonNode 改写：解析 response 文本，把累积的 delta 文本包成 message 项写入 output 数组。
    /// </summary>
    private static string RebuildResponseWithDeltaMessage(string responseJson, string deltaText)
    {
        try
        {
            var responseNode = JsonNode.Parse(responseJson);
            if (responseNode is not JsonObject obj)
            {
                return responseJson;
            }

            // output 为空或不存在时，用 delta 文本重建一条 assistant message。
            obj["output"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["status"] = "completed",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "output_text",
                            ["text"] = deltaText,
                            ["annotations"] = new JsonArray()
                        }
                    }
                }
            };
            return obj.ToJsonString();
        }
        catch
        {
            // 改写失败时回退原文，避免把可用响应变成不可用
            return responseJson;
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

            // Responses 顶层 status 终态校验：failed/cancelled 即使带部分 output 也不能当成功响应，
            // 否则上游已失败、客户端却收到转换后的"成功空回答"。
            if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
            {
                var statusValue = status.GetString();
                if (string.Equals(statusValue, "failed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(statusValue, "cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (protocolType == "Anthropic")
            {
                return root.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array
                    && content.GetArrayLength() > 0;
            }

            // Gemini 格式：v1internal 封套下（或顶层）的 candidates 数组非空即视为可用。
            if (string.Equals(protocolType, "Gemini", StringComparison.OrdinalIgnoreCase))
            {
                var geminiCandidates = root.TryGetProperty("response", out var geminiWrapper)
                    && geminiWrapper.ValueKind == JsonValueKind.Object
                    && geminiWrapper.TryGetProperty("candidates", out var wrappedCandidates)
                    && wrappedCandidates.ValueKind == JsonValueKind.Array
                        ? wrappedCandidates
                        : root.TryGetProperty("candidates", out var directCandidates)
                          && directCandidates.ValueKind == JsonValueKind.Array
                            ? directCandidates
                            : default;
                return geminiCandidates.ValueKind == JsonValueKind.Array && geminiCandidates.GetArrayLength() > 0;
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
            : string.Equals(protocolType, "Gemini", StringComparison.OrdinalIgnoreCase)
                ? "Upstream returned no usable candidates."
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

            if (root.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object)
            {
                return ExtractUsageFromElement(usage, protocolType);
            }

            // 某些中间层会保留顶层 "usage": null，同时把真实 usage 放在
            // response.usage；因此这里必须按值类型判断，不能只看属性是否存在。
            if (root.TryGetProperty("response", out var nestedResponse)
                && nestedResponse.ValueKind == JsonValueKind.Object
                && nestedResponse.TryGetProperty("usage", out var nestedUsage)
                && nestedUsage.ValueKind == JsonValueKind.Object)
            {
                return ExtractUsageFromElement(nestedUsage, protocolType);
            }

            if (string.Equals(protocolType, "Gemini", StringComparison.OrdinalIgnoreCase))
            {
                // Gemini 上游（v1internal 封套）：用量在顶层或 response.usageMetadata。
                var geminiUsage = root.TryGetProperty("usageMetadata", out var topLevelMetadata)
                    && topLevelMetadata.ValueKind == JsonValueKind.Object
                        ? topLevelMetadata
                        : root.TryGetProperty("response", out var responseWrapper)
                          && responseWrapper.ValueKind == JsonValueKind.Object
                          && responseWrapper.TryGetProperty("usageMetadata", out var nestedMetadata)
                          && nestedMetadata.ValueKind == JsonValueKind.Object
                                ? nestedMetadata
                                : default;
                if (geminiUsage.ValueKind != JsonValueKind.Object)
                {
                    return (0, 0, 0);
                }

                return ExtractUsageFromElement(geminiUsage, protocolType);
            }

            return (0, 0, 0);
        }
        catch
        {
            // 解析失败时返回零值
        }

        return (0, 0, 0);
    }

    /// <summary>
    /// 从 usage JSON 元素中提取 Token 用量（转发到 AITool.Protocol 统一实现，避免与协议层口径漂移）。
    /// </summary>
    private static (int InputTokens, int CachedTokens, int OutputTokens) ExtractUsageFromElement(JsonElement usage, string protocolType)
        => ProxyProtocolBridge.ExtractUsageFromElement(usage, protocolType);
}
