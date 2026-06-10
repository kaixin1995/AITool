namespace AITool.Core.Services;

/// <summary>
/// 后台页面使用的只读查询元数据服务。
/// 当前阶段先作为 `ProxyRequestMetadataCache` 的管理侧门面，逐步把 Admin 查询职责从运行时缓存对象中剥离出来。
/// </summary>
public sealed class AdminQueryMetadataService
{
    /// <summary>
    /// 底层代理请求元数据缓存。
    /// </summary>
    private readonly ProxyRequestMetadataCache _metadataCache;

    /// <summary>
    /// 初始化后台查询元数据服务。
    /// </summary>
    public AdminQueryMetadataService(ProxyRequestMetadataCache metadataCache)
    {
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 获取聊天页可调试模型列表。
    /// </summary>
    public Task<IReadOnlyList<CachedChatModel>> GetChatModelsAsync(CancellationToken cancellationToken)
    {
        return _metadataCache.GetChatModelsAsync(cancellationToken);
    }

    /// <summary>
    /// 获取聊天页全部站点模型候选。
    /// </summary>
    public Task<IReadOnlyList<CachedChatTarget>> GetChatTargetsAsync(CancellationToken cancellationToken)
    {
        return _metadataCache.GetChatTargetsAsync(cancellationToken);
    }

    /// <summary>
    /// 获取聊天页按模型筛选后的站点模型候选。
    /// </summary>
    public Task<IReadOnlyList<CachedChatTarget>> GetChatTargetsAsync(Guid modelId, CancellationToken cancellationToken)
    {
        return _metadataCache.GetChatTargetsAsync(modelId, cancellationToken);
    }

    /// <summary>
    /// 获取后台并发面板依赖的模型并发限制。
    /// </summary>
    public Task<IReadOnlyDictionary<string, int>> GetModelConcurrencyLimitsAsync(CancellationToken cancellationToken)
    {
        return _metadataCache.GetModelConcurrencyLimitsAsync(cancellationToken);
    }

    /// <summary>
    /// 获取启用站点名称字典。
    /// </summary>
    public Task<IReadOnlyDictionary<Guid, string>> GetEnabledSiteNamesAsync(CancellationToken cancellationToken)
    {
        return _metadataCache.GetEnabledSiteNamesAsync(cancellationToken);
    }

    /// <summary>
    /// 获取路由主入口列表。
    /// </summary>
    public Task<IReadOnlyList<RouteEntryListItem>> GetRouteEntriesAsync(CancellationToken cancellationToken)
    {
        return _metadataCache.GetRouteEntriesAsync(cancellationToken);
    }

    /// <summary>
    /// 获取可选站点实例列表。
    /// </summary>
    public Task<IReadOnlyList<SiteInstanceItem>> GetRouteSiteInstancesAsync(CancellationToken cancellationToken)
    {
        return _metadataCache.GetRouteSiteInstancesAsync(cancellationToken);
    }

    /// <summary>
    /// 获取可配置路由模型列表。
    /// </summary>
    public Task<IReadOnlyList<RouteModelItem>> GetRouteModelsAsync(CancellationToken cancellationToken)
    {
        return _metadataCache.GetRouteModelsAsync(cancellationToken);
    }

    /// <summary>
    /// 获取按模型发现的可用站点列表。
    /// </summary>
    public Task<IReadOnlyList<DiscoveredSiteItem>> GetDiscoveredSitesAsync(string modelName, CancellationToken cancellationToken)
    {
        return _metadataCache.GetDiscoveredSitesAsync(modelName, cancellationToken);
    }

    /// <summary>
    /// 获取按主入口聚合的路由规则列表。
    /// </summary>
    public Task<IReadOnlyList<RouteRuleListItem>> GetRouteRulesAsync(string modelName, CancellationToken cancellationToken)
    {
        return _metadataCache.GetRouteRulesAsync(modelName, cancellationToken);
    }

    /// <summary>
    /// 获取开发者页默认访问密钥。
    /// </summary>
    public Task<string> GetDeveloperDefaultAccessKeyAsync(CancellationToken cancellationToken)
    {
        return _metadataCache.GetDeveloperDefaultAccessKeyAsync(cancellationToken);
    }

    /// <summary>
    /// 获取开发者页调试模型列表。
    /// </summary>
    public Task<IReadOnlyList<ClientSimulatorModelItemViewModel>> GetDeveloperDebugModelsAsync(CancellationToken cancellationToken)
    {
        return _metadataCache.GetDeveloperDebugModelsAsync(cancellationToken);
    }

    /// <summary>
    /// 获取指定标识的已启用模型，未找到或已禁用时返回 null。
    /// </summary>
    public Task<CachedEnabledModel?> GetEnabledModelAsync(Guid modelId, CancellationToken cancellationToken)
    {
        return _metadataCache.GetEnabledModelAsync(modelId, cancellationToken);
    }

    /// <summary>
    /// 获取当前运行时设置快照。
    /// </summary>
    public Task<CachedProxyRuntimeSettings> GetRuntimeSettingsAsync(CancellationToken cancellationToken)
    {
        return _metadataCache.GetRuntimeSettingsAsync(cancellationToken);
    }

    /// <summary>
    /// 获取指定模型名称的路由目标列表，用于调试对话选路。
    /// </summary>
    public Task<IReadOnlyList<CachedProxyRouteTarget>> GetRouteTargetsForModelAsync(string modelName, CancellationToken cancellationToken)
    {
        return _metadataCache.GetRouteTargetsForModelAsync(modelName, cancellationToken);
    }

    /// <summary>
    /// 获取指定模型的兜底站点映射，用于无路由规则时的回退。
    /// </summary>
    public Task<CachedFallbackTarget?> GetFallbackTargetAsync(Guid modelId, CancellationToken cancellationToken)
    {
        return _metadataCache.GetFallbackTargetAsync(modelId, cancellationToken);
    }
}
