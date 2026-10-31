using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Accounts;
using AITool.Application.Sites;
using AITool.Infrastructure.Proxy;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Web.Services;

/// <summary>
/// 路由候选策略服务：集中承载三项可独立开关的路由增强策略（全部默认关闭，不影响既有行为）。
/// <para>
/// ① 配额感知排序（RouteQuotaOrderingEnabled）：候选中「额度窗口最快重置」的优先——
/// 重置即清零、不用就是浪费（magpie smart 模式的核心思想）。
/// ② 会话亲和（SessionAffinityEnabled）：同一会话粘在上次成功应答的候选上，
/// 避免逐请求回退把上游 prompt cache 打散（magpie affinity 的简化版：无 5 分钟活跃窗口衰减，
/// 换候选发生在亲和候选被熔断/休息时）。
/// ③ 分级冷却（TieredRouteCooldownEnabled）：配额耗尽休到该 Key 额度窗口的重置点（封顶 8 天）、
/// 限流按 Retry-After（封顶 1 小时），其余失败仍走既有失败计数熔断。
/// </para>
/// </summary>
public sealed class RouteCandidatePolicyService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly RouteCircuitStateStore _circuitStore;
    private readonly SessionAffinityService _sessionAffinity;
    private readonly ILogger<RouteCandidatePolicyService> _logger;

    /// <summary>配额重置信息缓存条目（60 秒 TTL，含负缓存）。</summary>
    private sealed record QuotaResetEntry(DateTimeOffset FetchedAtUtc, DateTimeOffset? EarliestResetUtc);

    private readonly ConcurrentDictionary<Guid, QuotaResetEntry> _quotaResetCache = [];
    private static readonly TimeSpan QuotaResetCacheTtl = TimeSpan.FromSeconds(60);

    public RouteCandidatePolicyService(
        IServiceProvider serviceProvider,
        RouteCircuitStateStore circuitStore,
        SessionAffinityService sessionAffinity,
        ILogger<RouteCandidatePolicyService> logger)
    {
        _serviceProvider = serviceProvider;
        _circuitStore = circuitStore;
        _sessionAffinity = sessionAffinity;
        _logger = logger;
    }

    /// <summary>
    /// 候选预排序：会话亲和命中时把亲和候选挪到最前（不再做配额排序——会话粘性优先）；
    /// 否则在启用配额排序时按「最早重置优先」稳定排序（无配额数据的候选保持原相对次序、排在有数据的之后）。
    /// </summary>
    public async Task<List<CachedProxyRouteTarget>> PreorderCandidatesAsync(
        IReadOnlyList<CachedProxyRouteTarget> routes,
        bool quotaOrderingEnabled,
        string? conversationKey,
        CancellationToken cancellationToken)
    {
        // 性能：两个开关都关闭时零拷贝零解析原样返回（每个代理请求都会经过这里）；
        // 仅在确有会话亲和键或启用配额排序时才拷贝重排。
        if (string.IsNullOrWhiteSpace(conversationKey) && !quotaOrderingEnabled)
        {
            return [.. routes];
        }

        var list = routes.ToList();
        if (!string.IsNullOrWhiteSpace(conversationKey)
            && _sessionAffinity.TryOrderForConversation(list, conversationKey))
        {
            return list;
        }

        if (!quotaOrderingEnabled || list.Count < 2)
        {
            return list;
        }

        // 批量取全部候选的重置时间（一次 DB 往返 + 单 scope），替代逐候选查询。
        var resets = await LoadResetsBatchAsync(list, cancellationToken);

        // 稳定排序：有重置时间的按时间升序在前，无数据的保持原相对次序在后。
        var ordered = resets
            .Select((item, index) => (item.Route, item.Reset, index))
            .OrderBy(item => item.Reset.HasValue ? 0 : 1)
            .ThenBy(item => item.Reset ?? DateTimeOffset.MaxValue)
            .ThenBy(item => item.index)
            .Select(item => item.Route)
            .ToList();
        return ordered;
    }

    /// <summary>
    /// 批量取全部候选的最早重置时间：先吃 60 秒内存缓存，未命中的键合并为一次 DB 查询
    /// （SiteKeyId IN / SiteId IN 各一条）+ 单 scope 解析，替代逐候选的 scope+DB 往返。
    /// </summary>
    private async Task<List<(CachedProxyRouteTarget Route, DateTimeOffset? Reset)>> LoadResetsBatchAsync(
        List<CachedProxyRouteTarget> routes,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var result = new List<(CachedProxyRouteTarget, DateTimeOffset?)>(routes.Count);
        var misses = new List<CachedProxyRouteTarget>();
        foreach (var route in routes)
        {
            if (_quotaResetCache.TryGetValue(route.SiteKeyId ?? route.SiteId, out var cached)
                && now - cached.FetchedAtUtc < QuotaResetCacheTtl)
            {
                result.Add((route, cached.EarliestResetUtc));
            }
            else
            {
                misses.Add(route);
                result.Add((route, null)); // 占位，回填
            }
        }

        if (misses.Count == 0)
        {
            return result;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>();
            var providers = scope.ServiceProvider.GetServices<ISiteQuotaProvider>().ToList();

            var keyIds = misses.Where(r => r.SiteKeyId is not null).Select(r => r.SiteKeyId!.Value).Distinct().ToList();
            var siteIds = misses.Where(r => r.SiteKeyId is null).Select(r => r.SiteId).Distinct().ToList();
            var keys = new List<Domain.Sites.SiteKey>();
            if (keyIds.Count > 0)
            {
                keys.AddRange(await dbContext.SiteKeys.Where(k => keyIds.Contains(k.Id)).ToListAsync(cancellationToken));
            }

            if (siteIds.Count > 0)
            {
                keys.AddRange(await dbContext.SiteKeys.Where(k => siteIds.Contains(k.SiteId)).ToListAsync(cancellationToken));
            }

            var byKey = keys.ToLookup(k => k.Id);
            var bySite = keys.ToLookup(k => k.SiteId);

            // OAuth 账号表回退：隐藏站点（无任何带配额缓存的 SiteKey）从账号表取巡检数据。
            var fallbackSites = routes
                .Where(r => !HasKeyQuotaData(r, byKey, bySite))
                .Select(r => r.SiteId)
                .Distinct()
                .ToList();
            var accountResets = fallbackSites.Count > 0
                ? await LoadAccountResetsAsync(scope.ServiceProvider, fallbackSites, now, cancellationToken)
                : [];

            for (var i = 0; i < routes.Count; i++)
            {
                if (result[i].Item2 is not null || !misses.Contains(routes[i]))
                {
                    continue;
                }

                var route = routes[i];
                var hasKeyData = HasKeyQuotaData(route, byKey, bySite);
                DateTimeOffset? earliest = null;
                if (hasKeyData)
                {
                    var candidates = route.SiteKeyId is not null ? byKey[route.SiteKeyId.Value] : bySite[route.SiteId];
                    foreach (var key in candidates)
                    {
                        if (string.IsNullOrWhiteSpace(key.LastQuotaRawJson))
                        {
                            continue;
                        }

                        foreach (var provider in providers)
                        {
                            var parsed = provider.ParseCached(key.LastQuotaRawJson);
                            if (parsed is not { Success: true })
                            {
                                continue;
                            }

                            foreach (var window in parsed.Windows)
                            {
                                if (window.IsAside || window.ResetAtUtc is not { } reset || reset <= now)
                                {
                                    continue;
                                }

                                if (earliest is null || reset < earliest)
                                {
                                    earliest = reset;
                                }
                            }

                            break;
                        }
                    }
                }
                else
                {
                    // 站点无 Key 配额数据：用账号表回退结果（同样可为 null → 负缓存 60 秒）。
                    accountResets.TryGetValue(route.SiteId, out var accountReset);
                    earliest = accountReset;
                }

                _quotaResetCache[route.SiteKeyId ?? route.SiteId] = new QuotaResetEntry(now, earliest);
                result[i] = (route, earliest);
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Batch quota reset lookup failed: {Message}", ex.Message);
        }

        return result;
    }

    /// <summary>该候选的站点是否存在任何带配额缓存的 SiteKey（决定是否走账号表回退）。</summary>
    private static bool HasKeyQuotaData(
        CachedProxyRouteTarget route,
        ILookup<Guid, Domain.Sites.SiteKey> byKey,
        ILookup<Guid, Domain.Sites.SiteKey> bySite)
    {
        var candidates = route.SiteKeyId is { } keyId ? byKey[keyId] : bySite[route.SiteId];
        foreach (var key in candidates)
        {
            if (!string.IsNullOrWhiteSpace(key.LastQuotaRawJson))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 账号表配额回退（OAuth 隐藏站点无 SiteKey 行，巡检数据落在账号表）：
    /// 按站点反查各 OAuth 提供程序的账号，解析巡检缓存的最早未来重置时刻。
    /// 一个隐藏站点只属于一个账号/提供程序；无数据站点不进结果（等同 null 负缓存）。
    /// </summary>
    private static async Task<Dictionary<Guid, DateTimeOffset?>> LoadAccountResetsAsync(
        IServiceProvider serviceProvider,
        IReadOnlyList<Guid> siteIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var resets = new Dictionary<Guid, DateTimeOffset?>();
        try
        {
            foreach (var provider in serviceProvider.GetServices<IAccountQuotaProvider>())
            {
                var targets = await provider.FindByLinkedSiteIdsAsync(siteIds, cancellationToken);
                foreach (var group in targets.Where(t => !string.IsNullOrWhiteSpace(t.LastQuotaRawJson))
                             .GroupBy(t => t.LinkedSiteId))
                {
                    if (resets.ContainsKey(group.Key))
                    {
                        continue;
                    }

                    resets[group.Key] = EarliestAccountResetUtc(provider, group.First().LastQuotaRawJson!, now);
                }
            }
        }
        catch
        {
            // 回退失败按无数据处理（负缓存 60 秒兜底），不影响既有 SiteKey 路径结果。
        }

        return resets;
    }

    private static DateTimeOffset? EarliestAccountResetUtc(
        IAccountQuotaProvider provider,
        string rawJson,
        DateTimeOffset now)
    {
        var snapshot = provider.ParseCachedQuota(rawJson);
        if (snapshot is not { Success: true })
        {
            return null;
        }

        DateTimeOffset? earliest = null;
        foreach (var window in snapshot.Windows)
        {
            if (window.ResetAtUtc is not { } reset || reset <= now)
            {
                continue;
            }

            if (earliest is null || reset < earliest)
            {
                earliest = reset;
            }
        }

        return earliest;
    }
    /// <summary>
    /// 取候选对应 Key 的最早未来重置时间：多 Key 展开候选用 SiteKeyId；兼容候选回退站点级
    /// （无 Key 记录取该站点全部 Key 中最早者）。数据来自 SiteKey.LastQuota* 落库缓存，
    /// 60 秒内存 TTL；无配额数据（从未查询/查询失败/无窗口）返回 null（负缓存同样 60 秒）。
    /// </summary>
    private async Task<DateTimeOffset?> GetEarliestFutureResetAsync(CachedProxyRouteTarget route, CancellationToken cancellationToken)
    {
        var cacheKey = route.SiteKeyId ?? route.SiteId;
        var now = DateTimeOffset.UtcNow;
        if (_quotaResetCache.TryGetValue(cacheKey, out var cached)
            && now - cached.FetchedAtUtc < QuotaResetCacheTtl)
        {
            return cached.EarliestResetUtc;
        }

        DateTimeOffset? earliest = null;
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>();
            var providers = scope.ServiceProvider.GetServices<ISiteQuotaProvider>().ToList();

            var keys = route.SiteKeyId is { } keyId
                ? await dbContext.SiteKeys.Where(k => k.Id == keyId).ToListAsync(cancellationToken)
                : await dbContext.SiteKeys.Where(k => k.SiteId == route.SiteId).ToListAsync(cancellationToken);

            var hasKeyData = keys.Any(k => !string.IsNullOrWhiteSpace(k.LastQuotaRawJson));
            if (!hasKeyData)
            {
                // OAuth 隐藏站点无 Key 配额数据：回退账号表巡检缓存。
                var accountResets = await LoadAccountResetsAsync(scope.ServiceProvider, [route.SiteId], now, cancellationToken);
                accountResets.TryGetValue(route.SiteId, out var accountReset);
                _quotaResetCache[cacheKey] = new QuotaResetEntry(now, accountReset);
                return accountReset;
            }

            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key.LastQuotaRawJson))
                {
                    continue;
                }

                foreach (var provider in providers)
                {
                    var parsed = provider.ParseCached(key.LastQuotaRawJson);
                    if (parsed is not { Success: true })
                    {
                        continue;
                    }

                    foreach (var window in parsed.Windows)
                    {
                        // 非阻塞窗口（如智谱 MCP 月度）不参与重置排序。
                        if (window.IsAside || window.ResetAtUtc is not { } reset || reset <= now)
                        {
                            continue;
                        }

                        if (earliest is null || reset < earliest)
                        {
                            earliest = reset;
                        }
                    }

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Quota reset lookup failed for site {SiteId}: {Message}", route.SiteId, ex.Message);
        }

        _quotaResetCache[cacheKey] = new QuotaResetEntry(now, earliest);
        return earliest;
    }

    /// <summary>
    /// 上游失败后的路由处置：启用分级冷却时——402/配额类 429 休到重置点（无数据休 30 分钟，封顶 8 天），
    /// 纯限流 429 按 Retry-After（封顶 1 小时，缺省 2 分钟）；其余失败走既有失败计数熔断。
    /// 未启用时行为与直接调用熔断计数完全一致。
    /// </summary>
    public async Task RecordRouteFailureAsync(
        CachedProxyRouteTarget route,
        int statusCode,
        string? errorBody,
        int? retryAfterSeconds,
        bool tieredCooldownEnabled,
        CircuitRouteMeta meta,
        CancellationToken cancellationToken)
    {
        if (!tieredCooldownEnabled)
        {
            _circuitStore.Block(route.CircuitKey, meta);
            return;
        }

        var body = errorBody ?? string.Empty;

        if (statusCode == 402 || (statusCode == 429 && LooksLikeQuotaExhaustion(body)))
        {
            var reset = await GetEarliestFutureResetAsync(route, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var until = reset is { } resetAt && resetAt > now
                ? resetAt
                : now.AddMinutes(30);
            // 至少休息 1 分钟：Rest 对 ≤now 的时刻直接早退，不钳制会出现「既不休息也不计数」的空窗。
            if (until < now.AddMinutes(1)) until = now.AddMinutes(1);
            if (until > now.AddDays(8))
            {
                until = now.AddDays(8);
            }

            _circuitStore.Rest(route.CircuitKey, until, $"配额耗尽，休息至 {until.ToLocalTime():MM-dd HH:mm}", meta);
            return;
        }

        if (statusCode == 429)
        {
            var rest = TimeSpan.FromSeconds(Math.Clamp(retryAfterSeconds ?? 120, 1, 3600));
            _circuitStore.Rest(route.CircuitKey, DateTimeOffset.UtcNow.Add(rest), $"限流，休息 {Math.Round(rest.TotalMinutes)} 分钟", meta);
            return;
        }

        _circuitStore.Block(route.CircuitKey, meta);
    }

    /// <summary>
    /// 客户端错误严格回退判定：开启严格模式且状态码为 400/413/422、错误体又未命中
    /// 「换一家可能就好」词表时，返回 true（该失败应直接回给客户端，不再回退）。
    /// </summary>
    public static bool ClientErrorBlocksFallback(bool strictEnabled, AITool.Application.Proxy.ProxyForwardResult result)
        => strictEnabled
           && result.StatusCode is 400 or 413 or 422
           && !IsClientErrorFallbackWorthy(result.ResponseBody ?? result.ErrorMessage);

    /// <summary>配额耗尽类错误词表（中英文常见形态，ordinal 忽略大小写）。</summary>
    private static readonly string[] QuotaExhaustionWords =
    [
        "usage_limit_reached", "usage limit", "quota_exceeded", "insufficient_quota",
        "insufficient quota", "billing", "credit exhausted", "余额不足", "额度不足", "配额不足", "欠费", "用完",
    ];

    /// <summary>纯限流类文案：命中则按 Retry-After 走限流休息，绝不能休到额度重置点。</summary>
    private static readonly string[] RateLimitOnlyWords =
    [
        "rate limit", "rate_limit", "ratelimit", "too many requests", "per minute", "per_second",
        "try again", "slow down", "请求过于频繁", "稍后重试",
    ];

    /// <summary>判断错误体是否为配额耗尽类（区分「没额度」与「太快了」两类 429）。</summary>
    public static bool LooksLikeQuotaExhaustion(string? errorBody)
    {
        if (string.IsNullOrEmpty(errorBody))
        {
            return false;
        }

        // 配额词优先于限流词：同时含两者的报文（如「rate limit …余额不足」）描述的是配额耗尽，
        // 按重置点休息比按 Retry-After 反复撞墙更正确。
        foreach (var word in QuotaExhaustionWords)
        {
            if (errorBody.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var word in RateLimitOnlyWords)
        {
            if (errorBody.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return false; // 明确的限流文案且无配额词：不是配额耗尽
            }
        }

        return false;
    }

    /// <summary>
    /// 客户端错误（400/413/422）是否值得回退到下一候选：命中词表（配额/模型未提供/鉴权/区域等，
    /// 这些换一家站点可能就好了）才回退；纯请求形态错误（参数畸形等，换谁都一样）直接回给客户端。
    /// 对齐 magpie retryable 的 400 词表思想，用于 StrictClientErrorFallbackEnabled 开启时。
    /// </summary>
    public static bool IsClientErrorFallbackWorthy(string? errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody))
        {
            return true; // 拿不到错误体时不拦截，保持既有回退行为
        }

        var worthy =
            LooksLikeQuotaExhaustion(errorBody)
            || errorBody.Contains("model", StringComparison.OrdinalIgnoreCase)
            || errorBody.Contains("not_found", StringComparison.OrdinalIgnoreCase)
            || errorBody.Contains("no such", StringComparison.OrdinalIgnoreCase)
            || errorBody.Contains("unserved", StringComparison.OrdinalIgnoreCase)
            || errorBody.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || errorBody.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            || errorBody.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
            || errorBody.Contains("region", StringComparison.OrdinalIgnoreCase)
            || errorBody.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || errorBody.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase)
            || errorBody.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || errorBody.Contains("不存在", StringComparison.Ordinal)
            || errorBody.Contains("不支持", StringComparison.Ordinal)
            || errorBody.Contains("未开通", StringComparison.Ordinal);
        return worthy;
    }
}

/// <summary>
/// 会话亲和服务：会话键 → 上次成功应答的候选（CircuitKey），内存态、上限 512 条、24 小时过期。
/// 会话键优先取 X-Session-Id / X-Conversation-Id 请求头，否则以「模型 + 首条用户消息前 512 字符」
/// 的 SHA-256 充当（同一会话的首条用户消息在多轮请求中保持不变，是稳定的会话指纹）。
/// </summary>
public sealed class SessionAffinityService
{
    private sealed record AffinityEntry(Guid CircuitKey, DateTimeOffset LastUsedUtc);

    private const int MaxEntries = 512;
    private static readonly TimeSpan EntryTtl = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, AffinityEntry> _map = [];
    private DateTimeOffset _lastPrunedUtc = DateTimeOffset.UtcNow;

    /// <summary>解析会话指纹：请求头优先，否则哈希模型 + 首条用户消息。失败/信息不足返回 null。</summary>
    public static string? TryResolveConversationKey(Microsoft.AspNetCore.Http.HttpRequest? request, string? modelName, string? requestBody)
    {
        var headerSession = request?.Headers["X-Session-Id"].FirstOrDefault()
                            ?? request?.Headers["X-Conversation-Id"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(headerSession))
        {
            return "h:" + headerSession.Trim();
        }

        var firstUserMessage = TryExtractFirstUserMessage(requestBody);
        if (string.IsNullOrWhiteSpace(firstUserMessage))
        {
            return null;
        }

        var payload = (modelName ?? string.Empty) + "\n" + firstUserMessage;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return "m:" + Convert.ToHexString(hash);
    }

    /// <summary>从请求体提取首条用户消息文本（前 512 字符）：兼容 OpenAI messages、Responses input、completions prompt。</summary>
    internal static string? TryExtractFirstUserMessage(string? requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody) || requestBody.Length > 2_000_000)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray())
                {
                    if (message.ValueKind != JsonValueKind.Object
                        || !message.TryGetProperty("role", out var role)
                        || role.ValueKind != JsonValueKind.String
                        || !string.Equals(role.GetString(), "user", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var text = ReadContentText(message);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text.Length > 512 ? text[..512] : text;
                    }
                }
            }

            if (root.TryGetProperty("input", out var input))
            {
                if (input.ValueKind == JsonValueKind.String && input.GetString() is { Length: > 0 } promptText)
                {
                    return promptText.Length > 512 ? promptText[..512] : promptText;
                }

                if (input.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in input.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                        {
                            return s.Length > 512 ? s[..512] : s;
                        }

                        if (item.ValueKind == JsonValueKind.Object
                            && (!item.TryGetProperty("role", out var role) || string.Equals(role.GetString(), "user", StringComparison.OrdinalIgnoreCase)))
                        {
                            var text = ReadContentText(item);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                return text.Length > 512 ? text[..512] : text;
                            }
                        }
                    }
                }
            }

            if (root.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String)
            {
                var text = prompt.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text!.Length > 512 ? text![..512] : text;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    private static string? ReadContentText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return null;
        }

        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString();
            case JsonValueKind.Array:
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String
                        && !string.IsNullOrEmpty(text.GetString()))
                    {
                        return text.GetString();
                    }
                }

                break;
        }

        return null;
    }

    /// <summary>
    /// 把亲和候选挪到列表最前。返回是否命中（命中时调用方跳过配额排序）。
    /// 只重排顺序，不改变候选集合；亲和候选熔断/休息时由转发循环自然跳过（此时视为未命中）。
    /// </summary>
    public bool TryOrderForConversation(List<CachedProxyRouteTarget> routes, string conversationKey)
    {
        if (!_map.TryGetValue(conversationKey, out var entry))
        {
            return false;
        }

        var index = routes.FindIndex(r => r.CircuitKey == entry.CircuitKey);
        if (index <= 0)
        {
            return index == 0;
        }

        var pinned = routes[index];
        routes.RemoveAt(index);
        routes.Insert(0, pinned);
        return true;
    }

    /// <summary>记录会话的成功应答候选。</summary>
    public void RecordSuccess(string? conversationKey, Guid circuitKey)
    {
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            return;
        }

        PruneIfNeeded();
        // 插入即守上限：两轮小时级清理之间的写入也不能突破 512 条（逐出最久未用）。
        while (_map.Count >= MaxEntries)
        {
            // 线性最小扫描：O(n) 无排序分配（OrderBy 是 O(n log n) + 枚举器分配）。
            string? oldestKey = null;
            var oldestAt = DateTimeOffset.MaxValue;
            foreach (var pair in _map)
            {
                if (pair.Value.LastUsedUtc < oldestAt)
                {
                    oldestAt = pair.Value.LastUsedUtc;
                    oldestKey = pair.Key;
                }
            }

            if (oldestKey is null || !_map.TryRemove(oldestKey, out _))
            {
                break;
            }
        }
        _map[conversationKey] = new AffinityEntry(circuitKey, DateTimeOffset.UtcNow);
    }

    /// <summary>定期清理过期与超量条目（机会式：每小时最多一次）。</summary>
    private void PruneIfNeeded()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPrunedUtc < TimeSpan.FromHours(1))
        {
            return;
        }

        _lastPrunedUtc = now;
        foreach (var pair in _map)
        {
            if (now - pair.Value.LastUsedUtc > EntryTtl)
            {
                _map.TryRemove(pair.Key, out _);
            }
        }

        if (_map.Count <= MaxEntries)
        {
            return;
        }

        foreach (var pair in _map.OrderBy(p => p.Value.LastUsedUtc).Take(_map.Count - MaxEntries))
        {
            _map.TryRemove(pair.Key, out _);
        }
    }
}
