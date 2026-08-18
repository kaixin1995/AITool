namespace AITool.Application.Pricing;

/// <summary>
/// 单个模型的低峰档价格（USD / 百万 tokens）。未配置 offPeak 的模型恒用基准价。
/// </summary>
public sealed class ModelOffPeakPricing
{
    /// <summary>低峰输入单价。</summary>
    public decimal Input { get; set; }
    /// <summary>低峰输出单价。</summary>
    public decimal Output { get; set; }
    /// <summary>低峰缓存读单价。</summary>
    public decimal CacheRead { get; set; }
    /// <summary>低峰缓存写单价（默认与基准一致）。</summary>
    public decimal? CacheWrite { get; set; }
}

/// <summary>
/// 单个模型的价格条目（USD / 百万 tokens）。
/// </summary>
public sealed class ModelPriceEntry
{
    /// <summary>价格表键（模型 ID，小写匹配）。</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>
    /// 展示名。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>
    /// 按厂商规则解析出的厂商名（仅 GET 时由服务端填充便于分组过滤；保存时忽略）。
    /// </summary>
    public string? VendorName { get; set; }
    /// <summary>输入单价（基准/高峰档）。</summary>
    public decimal Input { get; set; }
    /// <summary>输出单价（基准/高峰档）。</summary>
    public decimal Output { get; set; }
    /// <summary>缓存读单价（基准/高峰档）。日志的缓存列是读写合并桶，统一按读价计。</summary>
    public decimal CacheRead { get; set; }
    /// <summary>缓存写单价（保留字段；当前日志无法拆分缓存读写，暂不参与计算）。</summary>
    public decimal CacheWrite { get; set; }
    /// <summary>可选的低峰档价格（DeepSeek 类错峰计价模型）。</summary>
    public ModelOffPeakPricing? OffPeak { get; set; }
    /// <summary>高峰时段窗口列表（"HH:mm-HH:mm"，支持跨午夜）。窗口内用基准价，窗口外用低峰价。</summary>
    public List<string>? PeakWindows { get; set; }
    /// <summary>高峰窗口所在时区的 UTC 偏移分钟数（北京时间 480）。</summary>
    public int PeakTimeZoneOffsetMinutes { get; set; } = 480;
}

/// <summary>
/// 模型价格表（本地 JSON 文件，非数据库）。
/// </summary>
public sealed class ModelPricingCatalog
{
    /// <summary>美元兑人民币汇率（展示换算用，计价基准恒为 USD）。</summary>
    public decimal UsdToCny { get; set; } = 6.74m;
    /// <summary>价格条目。</summary>
    public List<ModelPriceEntry> Models { get; set; } = [];
}

/// <summary>
/// 一次用量消耗的成本计算结果。
/// </summary>
public readonly record struct ModelUsageCost
{
    /// <summary>成本（USD）。未匹配到价格时为 null。</summary>
    public decimal? CostUsd { get; init; }
    /// <summary>输入段成本（USD）。未定价时为 0。</summary>
    public decimal InputCostUsd { get; init; }
    /// <summary>缓存段成本（USD）。未定价时为 0。</summary>
    public decimal CachedCostUsd { get; init; }
    /// <summary>输出段成本（USD）。未定价时为 0。</summary>
    public decimal OutputCostUsd { get; init; }
    /// <summary>参与计价时命中的价格条目 ID；未命中为 null。</summary>
    public string? MatchedPriceId { get; init; }
    /// <summary>本次计价命中的条目（供展示）。</summary>
    public ModelPriceEntry? Entry { get; init; }
}

/// <summary>
/// 模型价格服务：维护本地 JSON 价格表（首次从模板生成、编辑后实时生效），
/// 提供模型名归一化匹配与按用量的成本计算。计价基准恒为 USD，人民币仅按汇率展示换算。
/// </summary>
public interface IModelPricingService
{
    /// <summary>
    /// 获取价格表（文件变动时自动重新加载；不存在时从模板初始化）。
    /// </summary>
    Task<ModelPricingCatalog> GetCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存价格表（校验 + 写入本地 JSON + 立即刷新缓存）。
    /// </summary>
    Task<ModelPricingCatalog> SaveCatalogAsync(ModelPricingCatalog catalog, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按模型名解析价格条目：精确命中 → 去 namespace 前缀 → 去日期后缀 → 去 effort 后缀逐级重试。
    /// 返回 null 表示未定价。
    /// </summary>
    ModelPriceEntry? ResolveEntry(string? modelName);

    /// <summary>
    /// 计算一次用量的成本（USD）。峰谷条目按请求时间落在高峰窗口内外选取档位。
    /// </summary>
    /// <param name="modelName">模型名（通常为 AttemptedModel）。</param>
    /// <param name="requestedAt">请求时间（用于峰谷判断）。</param>
    /// <param name="inputTokens">不含缓存的新输入 token。</param>
    /// <param name="cachedTokens">缓存 token（读写合并桶，按缓存读单价计）。</param>
    /// <param name="outputTokens">输出 token。</param>
    ModelUsageCost CalculateCostUsd(string? modelName, DateTimeOffset requestedAt, int inputTokens, int cachedTokens, int outputTokens);
}
