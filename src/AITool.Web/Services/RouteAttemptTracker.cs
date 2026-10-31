using System.Security.Cryptography;
using System.Text.Json;

namespace AITool.Web.Services;

/// <summary>引用队列的请求内去重：仅在真正取得并发许可、开始准备转发时认领等价候选。</summary>
public sealed class RouteAttemptTracker
{
    private readonly bool _enabled;
    private readonly TimeOnly _requestTime;
    private readonly HashSet<string> _attempted = new(StringComparer.Ordinal);
    private readonly Dictionary<CachedProxyRouteTarget, string> _identities = [];

    public RouteAttemptTracker(IReadOnlyList<CachedProxyRouteTarget> routes)
    {
        _enabled = routes.Any(route => route.FallbackGroupId.HasValue);
        _requestTime = TimeOnly.FromDateTime(DateTime.Now);
    }

    public bool UsesGroupedFallback => _enabled;

    public bool CanAttempt(CachedProxyRouteTarget route)
        => !_enabled || (route.IsAvailableAt(_requestTime) && !_attempted.Contains(GetIdentity(route)));

    public bool TryStart(CachedProxyRouteTarget route)
        => !_enabled || (route.IsAvailableAt(_requestTime) && _attempted.Add(GetIdentity(route)));

    private string GetIdentity(CachedProxyRouteTarget route)
    {
        if (_identities.TryGetValue(route, out var identity)) return identity;

        // 凭证与代理地址只参与内存摘要，绝不写入日志。组/规则 ID 不参与：池同步会重建规则。
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            route.SiteId,
            route.SiteKeyId,
            route.SiteModelName,
            route.UpstreamModelName,
            route.BaseUrl,
            route.EndpointPathMode,
            route.ApiKey,
            route.ManagedSource,
            route.ProtocolType,
            route.SupportsOpenAi,
            route.SupportsAnthropic,
            route.SupportsResponses,
            Headers = route.ExtraHeaders.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new { Key = pair.Key.ToUpperInvariant(), pair.Value }),
            route.ClientEmulation,
            route.EgressProxyUrl,
            route.GoogleProjectId,
            route.OverrideReasoningEffort,
            route.CompatibilityRules,
        });
        identity = Convert.ToHexString(SHA256.HashData(bytes));
        _identities.Add(route, identity);
        return identity;
    }
}
