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
    /// 代理完整 URL，例如 "http://127.0.0.1:7890", "socks5://127.0.0.1:10808"
    /// </summary>
    [SugarColumn(Length = 512)]
    public string ProxyUrl { get; set; } = string.Empty;

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
