using AITool.Domain.Proxy;

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
    /// 站点密钥列表（多 Key）。每个站点可有多条密钥，转发链路按 Key 展开多候选路由。
    /// </summary>
    public List<CoreRuntimeSiteKey> SiteKeys { get; set; } = [];

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
    /// 托管 OAuth 账号凭证（Codex/Google/Kimi）。
    /// Core 无数据库，401 即刷凭证所需的 refresh token 与 Google 项目标识从这里读取。
    /// </summary>
    public List<CoreRuntimeAccountCredential> AccountCredentials { get; set; } = [];

    /// <summary>
    /// 请求头模板档案（HeaderProfile），Core 侧客户端特征模拟的模板来源。
    /// </summary>
    public List<CoreRuntimeHeaderProfile> HeaderProfiles { get; set; } = [];

    /// <summary>
    /// 网络代理档案（ProxyProfile），Core 侧出口代理解析来源。
    /// </summary>
    public List<CoreRuntimeProxyProfile> ProxyProfiles { get; set; } = [];

    /// <summary>
    /// Core 运行时设置。
    /// </summary>
    public CoreRuntimeSettings RuntimeSettings { get; set; } = new();
}

/// <summary>
/// Core 运行时托管账号凭证项。
/// <para>
/// 双宿主设计：master 单体在转发进程内直接写库刷新凭证；split 的 Core 无库，
/// 刷新走「纯 HTTP 刷新 → 本快照即时回写 → credential-refreshed 事件 → Admin 持久化」链路。
/// 凭证经 127.0.0.1 内网通道传输，与 Sites.ApiKey 同级敏感。
/// </para>
/// </summary>
public sealed class CoreRuntimeAccountCredential
{
    /// <summary>
    /// 提供商标识：Codex | Google | kimi_oauth（与各实体 ManagedSource 一致）。
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// 账号标识（Admin 侧对应账号表主键）。
    /// </summary>
    public Guid AccountId { get; set; }

    /// <summary>
    /// 关联的隐藏站点标识。
    /// </summary>
    public Guid LinkedSiteId { get; set; }

    /// <summary>
    /// 刷新令牌（Google 会轮换，以 Admin 下发为准）。
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// Google 项目标识（Gemini 封套 project 字段；仅 Google 账号有值）。
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Google 账号类型（Antigravity；仅 Google 账号有值）。
    /// </summary>
    public string? AccountKind { get; set; }

    /// <summary>
    /// Kimi 设备标识（刷新时随请求上送；仅 Kimi 账号有值）。
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// 账号是否启用。
    /// </summary>
    public bool IsEnabled { get; set; } = true;
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

    /// <summary>
    /// 自定义请求头 JSON（Codex 隐藏 Site 注入 Originator/Chatgpt-Account-Id/User-Agent 等）。
    /// Core 转发时通过 MergeExtraHeaders 解析并注入上游请求，缺失会导致 Codex 请求被上游拒绝。
    /// </summary>
    public string? ExtraHeadersJson { get; set; }

    /// <summary>
    /// 托管提供商标识（Codex | Google | kimi_oauth；自建站点为空）。401 即刷回调按此分流。
    /// </summary>
    public string? ManagedSource { get; set; }

    /// <summary>
    /// 是否支持 OpenAI Responses 原生接口。
    /// </summary>
    public bool SupportsResponses { get; set; }

    /// <summary>
    /// 站点维度客户端特征模拟预设（None | OpenCode | ClaudeCode | CodexCli | Antigravity | Kimi | 自定义 Key）。
    /// </summary>
    public string ClientEmulation { get; set; } = "None";

    /// <summary>
    /// 站点专用出口网络代理地址（http/https/socks5，可为 ProxyProfile Key）。
    /// </summary>
    public string? EgressProxyUrl { get; set; }
}

/// <summary>
/// Core 运行时站点密钥项（多 Key）。
/// </summary>
public sealed class CoreRuntimeSiteKey
{
    /// <summary>
    /// 密钥标识。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 所属站点标识。
    /// </summary>
    public Guid SiteId { get; set; }

    /// <summary>
    /// 实际密钥值。
    /// </summary>
    public string KeyValue { get; set; } = string.Empty;

    /// <summary>
    /// 备注。
    /// </summary>
    public string? Remark { get; set; }

    /// <summary>
    /// 优先级，数字越小越优先。
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 创建时间。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
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

    /// <summary>
    /// 强制覆盖的思考等级。空=不干预，非空=强制覆盖转发给上游的思考等级。
    /// </summary>
    public string OverrideReasoningEffort { get; set; } = string.Empty;

    /// <summary>
    /// 关联的兼容规则集 Id（可空）。为空表示不应用任何规则集。
    /// </summary>
    public Guid? CompatibilityProfileId { get; set; }
    /// <summary>
    /// 模型维度默认客户端特征模拟预设。
    /// </summary>
    public string ClientEmulation { get; set; } = "None";
    /// <summary>
    /// 模型维度默认自定义转发请求头（JSON）。
    /// </summary>
    public string? ExtraHeadersJson { get; set; }
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
    /// 站点模型级的强制思考等级（如 low / medium / high / xhigh / max）。
    /// 优先级最高：非空时覆盖模型库级设置与客户端透传值；为空时回退模型库级，均为空则透传。
    /// </summary>
    public string? OverrideReasoningEffort { get; set; }

    /// <summary>
    /// 最近状态。
    /// </summary>
    public string LastStatus { get; set; } = string.Empty;
    /// <summary>
    /// 映射维度客户端特征模拟预设（三层优先级最高）。
    /// </summary>
    public string ClientEmulation { get; set; } = "None";
    /// <summary>
    /// 映射维度自定义转发请求头（JSON，支持  等动态占位符）。
    /// </summary>
    public string? ExtraHeadersJson { get; set; }
    /// <summary>
    /// 映射专属出口网络代理地址。
    /// </summary>
    public string? EgressProxyUrl { get; set; }

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

    /// <summary>
    /// 强制覆盖的思考等级。空=不干预，非空=强制覆盖转发给上游的思考等级。
    /// 由 Admin 在构建快照时按 model.OverrideReasoningEffort 填充（派生数据，Core 直接透传）。
    /// </summary>
    public string OverrideReasoningEffort { get; set; } = string.Empty;

    /// <summary>
    /// 该规则关联模型解析后的兼容规则集（已按 isPassthrough 筛选前的原始列表）。
    /// 由 Admin 在构建快照时按 model.CompatibilityProfileId 解析填充（派生数据，Core 直接透传）。
    /// 为空表示不应用任何规则。
    /// </summary>
    public IReadOnlyList<CompatibilityRule> CompatibilityRules { get; set; } = Array.Empty<CompatibilityRule>();
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

    /// <summary>
    /// 允许访问的路由入口名称（JSON 数组）。空串表示允许全部路由。
    /// </summary>
    public string AllowedRouteNames { get; set; } = string.Empty;
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
    /// 上游返回 429（速率限制）时的连续重试次数，默认 0（一次 429 即失败并回退下一路由）。
    /// 设为 N&gt;0 时：同一上游连续 N 次 429 才判定该路由失败；期间任一次成功即算成功。
    /// </summary>
    public int RateLimitRetryCount { get; set; }

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

    /// <summary>
    /// 调用追踪开关（开发者总闸开启时生效）。关闭后 Core 侧跳过追踪采集分配。
    /// </summary>
    public bool DeveloperTraceEnabled { get; set; } = true;

    /// <summary>
    /// 诊断抓包开关（开发者总闸开启时生效）。
    /// </summary>
    public bool DeveloperFailureDumpEnabled { get; set; } = true;

    /// <summary>
    /// 请求模拟器页开关。
    /// </summary>
    public bool DeveloperSimulatorEnabled { get; set; } = true;

    /// <summary>
    /// 协议诊断与 AI 自愈页开关。
    /// </summary>
    public bool DeveloperProtocolDiagnosticsEnabled { get; set; } = true;

    /// <summary>
    /// SQL 迁移页开关。
    /// </summary>
    public bool DeveloperSqlMigrationsEnabled { get; set; } = true;
}

/// <summary>
/// Core 运行时请求头模板档案项（HeaderProfile 的 Key→模板头 JSON 投影，来自 Admin 的 client-header-profiles.json）。
/// </summary>
public sealed class CoreRuntimeHeaderProfile
{
    /// <summary>档案 Key（ClientEmulation 引用它）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>模板头 JSON（含 ${guid}/${model} 等占位符，Core 侧由 ClientEmulationEngine 求值）。</summary>
    public string HeadersJson { get; set; } = "{}";

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// Core 运行时网络代理档案项（ProxyProfile 的 Key→URL 投影，来自 Admin 数据库 ProxyProfiles 表）。
/// </summary>
public sealed class CoreRuntimeProxyProfile
{
    /// <summary>档案 Key（EgressProxyUrl 可引用它）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>代理地址（http/https/socks4/4a/5）。</summary>
    public string ProxyUrl { get; set; } = string.Empty;

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; } = true;
}
