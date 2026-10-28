using AITool.Application.Accounts;

namespace AITool.Application.OAuth;

/// <summary>
/// 通用轮询式 OAuth 厂商扩展点：每家厂商提供登录会话、token 刷新、额度查询与聊天站点配置。
/// 新增厂商只需实现本接口并注册（Infrastructure 实现端点细节，Web 层全链路复用）。
/// </summary>
public interface IDeviceOAuthVendor
{
    /// <summary>厂商标识（存 DeviceOAuthAccount.VendorKey）。</summary>
    string VendorKey { get; }

    /// <summary>展示名（如「MiniMax Code」）。</summary>
    string Label { get; }

    /// <summary>发起登录：返回用户打开的授权地址与轮询会话状态（不透明 JSON，由 PollSignInAsync 消费）。</summary>
    Task<DeviceOAuthStartResult> StartSignInAsync(CancellationToken cancellationToken);

    /// <summary>轮询登录结果：未完成返回 <see cref="DeviceOAuthPollResult.IsPending"/>=true。</summary>
    Task<DeviceOAuthPollResult> PollSignInAsync(string sessionJson, CancellationToken cancellationToken);

    /// <summary>
    /// 粘贴回调完成登录（浏览器授权码流厂商用）：用户把浏览器回跳地址整体粘回，
    /// 由厂商解析 code 并换 token。默认不支持（轮询式厂商无需实现）。
    /// </summary>
    Task<DeviceOAuthPollResult> CompleteWithCallbackAsync(string sessionJson, string callbackUrl, CancellationToken cancellationToken)
        => throw new NotSupportedException("该厂商不支持回调地址粘贴，请使用轮询登录");

    /// <summary>
    /// 用 refresh_token 换新 token。上游明确拒绝（invalid_grant 等）抛
    /// <see cref="DeviceOAuthRefreshRefusedException"/>；网络类错误抛普通异常（可重试）。
    /// </summary>
    Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken);

    /// <summary>
    /// 带 ExtraJson 的刷新（后台/凭证刷新服务统一走这一入口）：
    /// 需要附加数据的厂商（如 Kiro 的 AWS 客户端 id）覆写本方法，默认回落到无附加形态。
    /// </summary>
    Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, string? extraJson, CancellationToken cancellationToken)
        => RefreshTokenAsync(refreshToken, cancellationToken);

    /// <summary>该厂商是否支持拉取模型目录（默认不支持；支持的厂商覆写 FetchModelsAsync）。</summary>
    bool SupportsModelFetch => false;

    /// <summary>
    /// 拉取该账号可用的上游模型目录（供选择导入到关联站点）。
    /// 仅 <see cref="SupportsModelFetch"/>=true 的厂商需要实现；默认返回空列表。
    /// </summary>
    Task<IReadOnlyList<DeviceOAuthRemoteModel>> FetchModelsAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DeviceOAuthRemoteModel>>([]);

    /// <summary>
    /// 如 WorkBuddy 的 X-Refresh-Token 流要求 Authorization 仍携带旧 access token）。
    /// 默认回落到 refresh_token + ExtraJson 形态，既有厂商零改动。
    /// </summary>
    Task<DeviceOAuthTokenSet> RefreshTokenAsync(DeviceOAuthTokenSet current, string? extraJson, CancellationToken cancellationToken)
        => RefreshTokenAsync(current.RefreshToken ?? string.Empty, extraJson, cancellationToken);

    /// <summary>实时查询额度。凭证失效返回 <see cref="DeviceOAuthQuotaResult.CredentialInvalid"/>=true。</summary>
    Task<DeviceOAuthQuotaResult> QueryQuotaAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken cancellationToken);

    /// <summary>解析落库的原始额度响应（巡检缓存路径）；不可解析返回 null。</summary>
    DeviceOAuthQuotaResult? ParseCachedQuota(string rawJson);

    /// <summary>聊天转发站点配置；null 表示该厂商暂不支持聊天转发（仅账号托管与额度巡检）。</summary>
    DeviceOAuthChatSiteConfig? ChatSiteConfig { get; }

    /// <summary>默认模型清单（Slug=对外名，DisplayName=展示名）。</summary>
    IReadOnlyList<(string Slug, string DisplayName)> DefaultModels { get; }

    /// <summary>
    /// token 刷新提前量（默认 30 分钟）。短寿 token 厂商（如 Copilot 会话令牌约 30 分钟）
    /// 覆写为更小值，避免每次扫描都触发刷新。
    /// </summary>
    TimeSpan RefreshLead => TimeSpan.FromMinutes(30);
}

/// <summary>发起登录结果：授权地址 + 轮询会话状态（不透明）。</summary>
public sealed record DeviceOAuthStartResult(
    string AuthorizeUrl,
    string? UserCode,
    string Instructions,
    string SessionJson);

/// <summary>轮询登录结果。</summary>
public sealed record DeviceOAuthPollResult
{
    public bool IsPending { get; init; }
    public bool IsDenied { get; init; }
    public bool IsExpired { get; init; }
    public string? Error { get; init; }
    public DeviceOAuthTokenSet? Tokens { get; init; }
    public string? Email { get; init; }
    public string? ExternalUserId { get; init; }
    public string? DisplayName { get; init; }
    /// <summary>厂商附加数据（存 DeviceOAuthAccount.ExtraJson，如 MiniMax realUserId）。</summary>
    public string? ExtraJson { get; init; }
    /// <summary>
    /// 多段登录的续跳地址（如 Kiro 的 AWS Builder ID 第二段）：返回 pending + 本字段时，
    /// 前端应引导用户继续打开该地址，并配合 <see cref="UpdatedSessionJson"/> 更新会话状态。
    /// </summary>
    public string? NextAuthorizeUrl { get; init; }
    /// <summary>会话状态更新（多段登录中间态；控制器以此替换存储的 SessionJson）。</summary>
    public string? UpdatedSessionJson { get; init; }
}

/// <summary>token 集合。</summary>
public sealed record DeviceOAuthTokenSet
{
    public string AccessToken { get; init; } = string.Empty;
    public string? RefreshToken { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>上游明确拒绝刷新（需重新登录）。</summary>
public sealed class DeviceOAuthRefreshRefusedException(string message) : Exception(message);

/// <summary>额度查询结果（窗口型，复用通用 AccountQuotaWindow）。</summary>
public sealed record DeviceOAuthQuotaResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public bool CredentialInvalid { get; init; }
    public string? PlanType { get; init; }
    public string RawJson { get; init; } = string.Empty;
    public IReadOnlyList<AccountQuotaWindow> Windows { get; init; } = [];
}

/// <summary>聊天转发站点配置：隐藏 Site 的创建参数。</summary>
public sealed record DeviceOAuthChatSiteConfig(
    string BaseUrl,
    string ProtocolType,
    bool SupportsOpenAi,
    bool SupportsAnthropic,
    bool SupportsResponses,
    IReadOnlyDictionary<string, string> ExtraHeaders);

/// <summary>上游模型目录条目（Slug=上游模型 ID，DisplayName=展示名，缺失时回退 ID）。</summary>
public sealed record DeviceOAuthRemoteModel(string Slug, string DisplayName);
