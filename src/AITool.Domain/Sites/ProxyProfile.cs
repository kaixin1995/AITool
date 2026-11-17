using System.Text.Json.Serialization;
using SqlSugar;

namespace AITool.Domain.Sites;

/// <summary>
/// 网络出口代理配置方案。
/// 支持 HTTP、HTTPS、SOCKS5、SOCKS4 等协议，供站点和站点模型映射快捷下拉引用。
/// </summary>
[SugarTable("ProxyProfiles")]
[SugarIndex("UX_ProxyProfiles_Key", nameof(Key), OrderByType.Asc, true)]
public class ProxyProfile
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 唯一标识 Key，例如 "clash-main", "hk-socks5", "us-node-1"
    /// </summary>
    [SugarColumn(Length = 64)]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// 显示名称，例如 "本地 Clash (7890)", "香港高速专线 SOCKS5"
    /// </summary>
    [SugarColumn(Length = 128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 不含 userinfo 的代理端点，例如 "http://127.0.0.1:7890"。
    /// </summary>
    [SugarColumn(Length = 512)]
    public string ProxyUrl { get; set; } = string.Empty;

    /// <summary>每次配置修改递增，运行时可与稳定 Id 一起标识配置版本。</summary>
    [SugarColumn(DefaultValue = "1")]
    public long Revision { get; set; } = 1;

    /// <summary>经 IDataProtection 保护的用户名，绝不能通过管理 API 返回。</summary>
    [JsonIgnore]
    [SugarColumn(ColumnDataType = "text", IsNullable = true)]
    public string? ProtectedUsername { get; set; }

    /// <summary>经 IDataProtection 保护的密码，绝不能通过管理 API 返回。</summary>
    [JsonIgnore]
    [SugarColumn(ColumnDataType = "text", IsNullable = true)]
    public string? ProtectedPassword { get; set; }

    /// <summary>迁移后不可用配置的固定错误代码；不得包含原始输入。</summary>
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? ConfigurationError { get; set; }

    /// <summary>迁移隔离的原始配置，仅加密保存以供修复，绝不通过 API 返回。</summary>
    [JsonIgnore]
    [SugarColumn(ColumnDataType = "text", IsNullable = true)]
    public string? ProtectedLegacyConfiguration { get; set; }

    /// <summary>
    /// 描述说明
    /// </summary>
    [SugarColumn(Length = 512, IsNullable = true)]
    public string? Description { get; set; }

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 排序序号
    /// </summary>
    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [SugarColumn(IsNullable = true)]
    public DateTimeOffset? UpdatedAt { get; set; }
}
