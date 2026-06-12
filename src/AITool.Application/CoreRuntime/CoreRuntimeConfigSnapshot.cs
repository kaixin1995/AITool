namespace AITool.Application.CoreRuntime;

/// <summary>
/// Core 运行时完整配置快照。
/// </summary>
public sealed class CoreRuntimeConfigSnapshot
{
    /// <summary>
    /// 配置版本号，单调递增。
    /// </summary>
    public long ConfigVersion { get; set; }

    /// <summary>
    /// 配置内容哈希，用于判断配置是否发生变化。
    /// </summary>
    public string ConfigHash { get; set; } = string.Empty;

    /// <summary>
    /// 快照生成时间。
    /// </summary>
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 站点列表。
    /// </summary>
    public List<CoreRuntimeSite> Sites { get; set; } = [];

    /// <summary>
    /// 模型列表。
    /// </summary>
    public List<CoreRuntimeModel> Models { get; set; } = [];

    /// <summary>
    /// 站点模型映射列表。
    /// </summary>
    public List<CoreRuntimeSiteModelMapping> SiteModelMappings { get; set; } = [];

    /// <summary>
    /// 路由主入口列表。
    /// </summary>
    public List<CoreRuntimeRouteEntry> RouteEntries { get; set; } = [];

    /// <summary>
    /// 路由规则列表。
    /// </summary>
    public List<CoreRuntimeRouteRule> RouteRules { get; set; } = [];

    /// <summary>
    /// 访问密钥列表。
    /// </summary>
    public List<CoreRuntimeAccessKey> AccessKeys { get; set; } = [];

    /// <summary>
    /// Core 运行时设置。
    /// </summary>
    public CoreRuntimeSettings RuntimeSettings { get; set; } = new();
}

/// <summary>
/// Core 运行时站点项。
/// </summary>
public sealed class CoreRuntimeSite
{
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 站点名称。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 站点根地址。
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// 路径模式。
    /// </summary>
    public string EndpointPathMode { get; set; } = string.Empty;

    /// <summary>
    /// 站点密钥。
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 默认协议类型。
    /// </summary>
    public string ProtocolType { get; set; } = string.Empty;

    /// <summary>
    /// 是否支持 OpenAI。
    /// </summary>
    public bool SupportsOpenAi { get; set; }

    /// <summary>
    /// 是否支持 Anthropic。
    /// </summary>
    public bool SupportsAnthropic { get; set; }

    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; }
}

/// <summary>
/// Core 运行时模型项。
/// </summary>
public sealed class CoreRuntimeModel
{
    /// <summary>
    /// 模型标识。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 统一模型名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// 展示名称。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; }
}

/// <summary>
/// Core 运行时站点模型映射项。
/// </summary>
public sealed class CoreRuntimeSiteModelMapping
{
    /// <summary>
    /// 映射标识。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }

    /// <summary>
    /// 模型库项标识。
    /// </summary>
    public Guid ModelLibraryItemId { get; set; }

    /// <summary>
    /// 站点侧模型名。
    /// </summary>
    public string RemoteModelName { get; set; } = string.Empty;

    /// <summary>
    /// 最近状态。
    /// </summary>
    public string LastStatus { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// 最大并发。
    /// </summary>
    public int MaxConcurrency { get; set; }
}

/// <summary>
/// Core 运行时路由主入口项。
/// </summary>
public sealed class CoreRuntimeRouteEntry
{
    /// <summary>
    /// 主入口标识。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 主入口名称。
    /// </summary>
    public string EntryName { get; set; } = string.Empty;
}

/// <summary>
/// Core 运行时路由规则项。
/// </summary>
public sealed class CoreRuntimeRouteRule
{
    /// <summary>
    /// 规则标识。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 外部模型名。
    /// </summary>
    public string ExternalModelName { get; set; } = string.Empty;

    /// <summary>
    /// 上游模型名。
    /// </summary>
    public string UpstreamModelName { get; set; } = string.Empty;

    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }

    /// <summary>
    /// 站点模型名。
    /// </summary>
    public string SiteModelName { get; set; } = string.Empty;

    /// <summary>
    /// 全局优先级。
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// 模型优先级。
    /// </summary>
    public int ModelPriority { get; set; }

    /// <summary>
    /// 实例优先级。
    /// </summary>
    public int InstancePriority { get; set; }

    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// 时间可用性模式。
    /// </summary>
    public string AvailabilityMode { get; set; } = string.Empty;

    /// <summary>
    /// 时间范围 JSON。
    /// </summary>
    public string TimeRangesJson { get; set; } = string.Empty;
}

/// <summary>
/// Core 运行时访问密钥项。
/// </summary>
public sealed class CoreRuntimeAccessKey
{
    /// <summary>
    /// 密钥标识。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 密钥名称。
    /// </summary>
    public string KeyName { get; set; } = string.Empty;

    /// <summary>
    /// 明文密钥。
    /// </summary>
    public string PlainKey { get; set; } = string.Empty;

    /// <summary>
    /// 密钥哈希。
    /// </summary>
    public string AccessKeyHash { get; set; } = string.Empty;

    /// <summary>
    /// 掩码文本。
    /// </summary>
    public string MaskedValue { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; }
}

/// <summary>
/// Core 运行时设置。
/// </summary>
public sealed class CoreRuntimeSettings
{
    /// <summary>
    /// 代理超时秒数。
    /// </summary>
    public int ProxyRequestTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// 代理重试次数。
    /// </summary>
    public int ProxyRetryCount { get; set; } = 1;

    /// <summary>
    /// 熔断失败阈值。
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>
    /// 熔断恢复分钟数。
    /// </summary>
    public int CircuitBreakerRecoveryMinutes { get; set; } = 2;

    /// <summary>
    /// 并发模式。
    /// </summary>
    public int ConcurrencyMode { get; set; }

    /// <summary>
    /// 并发排队超时秒数。
    /// </summary>
    public int ConcurrencyQueueTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// 是否启用对话记录。
    /// </summary>
    public bool ConversationLogEnabled { get; set; } = true;

    /// <summary>
    /// 是否启用开发者功能。
    /// </summary>
    public bool DeveloperFeaturesEnabled { get; set; }
}
