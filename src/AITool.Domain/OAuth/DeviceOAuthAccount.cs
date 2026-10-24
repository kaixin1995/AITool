using SqlSugar;

namespace AITool.Domain.OAuth;

/// <summary>
/// 通用轮询式 OAuth 账号（MiniMax Code / ZCode / Qoder 等设备码或授权链接+轮询流厂商共用）。
/// <para>
/// 与 Codex / Google / Kimi / xAI 垂直一致的「隐藏 Site 复用」方案：每账号自动创建一个
/// ManagedSource="device_oauth" 的隐藏 Site，聊天 token 同步写回 Site.ApiKey；
/// <see cref="VendorKey"/> 区分厂商，具体端点/协议/头由厂商定义（IDeviceOAuthVendor）提供。
/// 禁用状态矩阵与 KimiAccount 相同：ManuallyDisabled / DisabledByFeatureToggle 独立记账。
/// </para>
/// </summary>
[SugarTable("DeviceOAuthAccounts")]
[SugarIndex("IX_DeviceOAuthAccounts_Vendor", nameof(VendorKey), OrderByType.Asc, false)]
[SugarIndex("IX_DeviceOAuthAccounts_LinkedSiteId", nameof(LinkedSiteId), OrderByType.Asc, false)]
[SugarIndex("IX_DeviceOAuthAccounts_TokenExpiresAt", nameof(TokenExpiresAt), OrderByType.Asc, false)]
public sealed class DeviceOAuthAccount
{
    /// <summary>浅拷贝（缓存副本防污染）。</summary>
    public DeviceOAuthAccount Clone() => (DeviceOAuthAccount)MemberwiseClone();

    /// <summary>账号唯一标识。</summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>厂商标识（如 minimax-cn / minimax-global / zcode / qoder）。</summary>
    [SugarColumn(Length = 50, IsNullable = false)]
    public string VendorKey { get; set; } = string.Empty;

    /// <summary>用户自定义名称。</summary>
    [SugarColumn(Length = 200, IsNullable = false)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>授权账号邮箱（若有）。</summary>
    [SugarColumn(Length = 200, IsNullable = true)]
    public string? Email { get; set; }

    /// <summary>上游用户标识（若有）。</summary>
    [SugarColumn(Length = 200, IsNullable = true)]
    public string? ExternalUserId { get; set; }

    /// <summary>聊天/账号 API 所用 token，同步写回隐藏 Site 的 ApiKey。</summary>
    [SugarColumn(Length = 4000, IsNullable = true)]
    public string? AccessToken { get; set; }

    /// <summary>refresh_token（后台刷新用）。</summary>
    [SugarColumn(Length = 4000, IsNullable = true)]
    public string? RefreshToken { get; set; }

    /// <summary>token 过期时间（UTC）。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? TokenExpiresAt { get; set; }

    /// <summary>最近一次成功刷新时间。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? LastRefreshAt { get; set; }

    /// <summary>上游明确拒绝刷新（invalid_grant 等）后置位，等待用户重新登录。</summary>
    [SugarColumn(IsNullable = false)]
    public bool RequiresReauth { get; set; } = false;

    /// <summary>厂商特定附加数据（JSON，如 MiniMax 的 realUserId / Qoder 的 deviceToken）。</summary>
    [SugarColumn(ColumnDataType = "text", IsNullable = true)]
    public string? ExtraJson { get; set; }

    /// <summary>关联的隐藏 Site。</summary>
    [SugarColumn(IsNullable = false)]
    public Guid LinkedSiteId { get; set; }

    /// <summary>是否启用（禁用同步禁用隐藏 Site）。</summary>
    [SugarColumn(IsNullable = false)]
    public bool IsEnabled { get; set; } = true;

    /// <summary>用户手动禁用（巡检不自动恢复）。</summary>
    [SugarColumn(IsNullable = false)]
    public bool ManuallyDisabled { get; set; } = false;

    /// <summary>因 OAuth 总开关禁用（重开后自动恢复）。</summary>
    [SugarColumn(IsNullable = false)]
    public bool DisabledByFeatureToggle { get; set; } = false;

    /// <summary>最近一次额度查询原始响应（缓存展示与巡检解析）。</summary>
    [SugarColumn(ColumnDataType = "text", IsNullable = true)]
    public string? LastQuotaRawJson { get; set; }

    /// <summary>最近一次额度查询时间。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? LastQuotaCheckedAt { get; set; }

    /// <summary>逻辑删除标记。</summary>
    [SugarColumn(IsNullable = false)]
    public bool IsDeleted { get; set; } = false;

    /// <summary>创建时间（UTC）。</summary>
    [SugarColumn(IsNullable = false)]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>更新时间（UTC）。</summary>
    [SugarColumn(IsNullable = false)]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
