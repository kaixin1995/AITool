namespace AITool.Application.Sites;

/// <summary>
/// 站点套餐额度窗口（各供应商通用形状，对齐 OAuth 账号额度的窗口模型）。
/// </summary>
/// <param name="Id">窗口标识（如 five_hour / weekly_limit），同一密钥内唯一。</param>
/// <param name="Label">展示标签（如「5 小时窗口」「每周额度」）。</param>
/// <param name="UsedPercent">已用百分比（0-100，忠实搬运不裁剪，负数/超 100 由渲染层处理）。</param>
/// <param name="ResetLabel">重置时间的简短展示（如「10-12 17:44」），无重置时间为 null。</param>
/// <param name="ResetAtUtc">重置时间（UTC），供前端做相对倒计时；无重置时间为 null。</param>
public sealed record SiteQuotaWindow(
    string Id,
    string Label,
    double UsedPercent,
    string? ResetLabel,
    DateTimeOffset? ResetAtUtc);

/// <summary>
/// 站点账户余额（按量计费供应商如 DeepSeek 的 /user/balance，与窗口型额度并列展示）。
/// </summary>
/// <param name="Currency">币种代码（CNY / USD）。</param>
/// <param name="TotalBalance">总余额。</param>
/// <param name="GrantedBalance">赠送余额（未赠送为 null）。</param>
/// <param name="ToppedUpBalance">充值余额（无该字段为 null）。</param>
public sealed record SiteQuotaBalanceInfo(
    string Currency,
    decimal TotalBalance,
    decimal? GrantedBalance,
    decimal? ToppedUpBalance);

/// <summary>
/// 单次站点额度查询结果（或缓存解析结果）。窗口型（Windows）与余额型（Balances）
/// 至少其一非空即视为成功。
/// </summary>
public sealed record SiteQuotaQueryResult
{
    /// <summary>是否成功取得额度数据（至少解析出业务错误也算确定结果，见 <see cref="Error"/>）。</summary>
    public bool Success { get; init; }

    /// <summary>失败时的错误信息（面向用户展示）。</summary>
    public string? Error { get; init; }

    /// <summary>失败是否因密钥失效（上游 401/403），前端据此展示「密钥失效」状态。</summary>
    public bool CredentialInvalid { get; init; }

    /// <summary>套餐等级（智谱响应的 data.level，如「GLM Coding Plan」），未知为 null。</summary>
    public string? Level { get; init; }

    /// <summary>上游原始响应体，成功时用于落库缓存（SiteKey.LastQuotaRawJson）。</summary>
    public string RawJson { get; init; } = string.Empty;

    /// <summary>解析出的额度窗口列表（窗口型供应商）。</summary>
    public IReadOnlyList<SiteQuotaWindow> Windows { get; init; } = [];

    /// <summary>解析出的余额列表（余额型供应商，如 DeepSeek）。</summary>
    public IReadOnlyList<SiteQuotaBalanceInfo> Balances { get; init; } = [];
}

/// <summary>
/// 站点套餐额度供应商扩展点：按 base_url 识别站点所属供应商，并用站点密钥查询套餐额度。
/// <para>
/// 站点页「额度查询」Tab 的编排（<c>SiteQuotaService</c>）只依赖本接口，新增供应商时
/// 实现本接口并在 Program.cs 注册（<c>AddHttpClient</c> + <c>ISiteQuotaProvider</c>）即可，
/// 编排与前端无需改动。查询为纯手动模式（进入页面或点刷新按钮才触发），无后台巡检。
/// </para>
/// </summary>
public interface ISiteQuotaProvider
{
    /// <summary>供应商标识（如 zhipu），用于区分额度来源。</summary>
    string ProviderKey { get; }

    /// <summary>供应商展示名（如「智谱 GLM」）。</summary>
    string ProviderLabel { get; }

    /// <summary>判断站点 base_url 是否属于该供应商（host 级匹配，不认路径中出现的域名）。</summary>
    bool MatchesBaseUrl(string baseUrl);

    /// <summary>用站点密钥实时查询套餐额度。</summary>
    Task<SiteQuotaQueryResult> QueryAsync(string baseUrl, string apiKey, CancellationToken cancellationToken);

    /// <summary>
    /// 解析已缓存的原始响应（<c>SiteKey.LastQuotaRawJson</c>），还原窗口与套餐等级。
    /// 无可用数据（从未查询过或结构不识别）时返回 null。
    /// </summary>
    SiteQuotaQueryResult? ParseCached(string rawJson);
}

/// <summary>额度查询页面单个密钥的展示信息（密钥值脱敏）。</summary>
/// <param name="KeyId">密钥标识。</param>
/// <param name="KeyValueMasked">脱敏后的密钥值（前 4 + *** + 后 4）。</param>
/// <param name="Remark">备注（如「主号」「备用号」）。</param>
/// <param name="Priority">调度优先级，数字越小越优先。</param>
/// <param name="IsEnabled">密钥是否启用。</param>
/// <param name="Status">查询状态：never=从未查询，ok=成功，invalid_credential=密钥失效，error=其他错误。</param>
/// <param name="Level">套餐等级。</param>
/// <param name="Error">最近一次查询的错误信息。</param>
/// <param name="CheckedAtUtc">最近一次查询时间（含失败尝试）。</param>
/// <param name="Windows">额度窗口（失败时为上次成功值，前端置灰展示）。</param>
/// <param name="Balances">账户余额（失败时为上次成功值，前端置灰展示）。</param>
public sealed record SiteQuotaKeyInfo(
    Guid KeyId,
    string KeyValueMasked,
    string? Remark,
    int Priority,
    bool IsEnabled,
    string Status,
    string? Level,
    string? Error,
    DateTimeOffset? CheckedAtUtc,
    IReadOnlyList<SiteQuotaWindow> Windows,
    IReadOnlyList<SiteQuotaBalanceInfo> Balances);

/// <summary>额度查询页面单个站点（及其全部密钥）的展示信息。</summary>
public sealed record SiteQuotaSiteInfo(
    Guid SiteId,
    string SiteName,
    string BaseUrl,
    string ProviderKey,
    string ProviderLabel,
    IReadOnlyList<SiteQuotaKeyInfo> Keys);

/// <summary>额度查询页面总览：全部支持额度查询的站点。</summary>
public sealed record SiteQuotaOverview(IReadOnlyList<SiteQuotaSiteInfo> Sites);
