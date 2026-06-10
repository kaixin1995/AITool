namespace AITool.Web.Services;

/// <summary>
/// 后台管理侧的缓存失效门面。
/// 当前阶段先把后台写操作对 `ProxyRequestMetadataCache` 的直接失效调用统一收口，逐步减少页面和控制器对运行时缓存对象的直接依赖。
/// </summary>
public sealed class AdminCacheInvalidationService
{
    /// <summary>
    /// 代理请求元数据缓存。
    /// </summary>
    private readonly ProxyRequestMetadataCache _metadataCache;

    /// <summary>
    /// 初始化后台缓存失效服务。
    /// </summary>
    public AdminCacheInvalidationService(ProxyRequestMetadataCache metadataCache)
    {
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 失效访问密钥相关缓存。
    /// </summary>
    public void InvalidateAccessKeys()
    {
        _metadataCache.InvalidateAccessKeys();
    }

    /// <summary>
    /// 失效运行时设置缓存。
    /// </summary>
    public void InvalidateRuntimeSettings()
    {
        _metadataCache.InvalidateRuntimeSettings();
    }

    /// <summary>
    /// 失效模型相关缓存。
    /// </summary>
    public void InvalidateModelMetadata()
    {
        _metadataCache.InvalidateModelMetadata();
    }

    /// <summary>
    /// 失效路由相关缓存。
    /// </summary>
    public void InvalidateRouteTargets()
    {
        _metadataCache.InvalidateRouteTargets();
    }

    /// <summary>
    /// 失效后台路由配置元数据缓存。
    /// </summary>
    public void InvalidateAdminRouteMetadata()
    {
        _metadataCache.InvalidateAdminRouteMetadata();
    }

    /// <summary>
    /// 失效运行时路由缓存。
    /// </summary>
    public void InvalidateRuntimeRouteTargets()
    {
        _metadataCache.InvalidateRuntimeRouteTargets();
    }
}
