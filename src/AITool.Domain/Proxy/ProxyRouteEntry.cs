using SqlSugar;

namespace AITool.Domain.Proxy;

/// <summary>
/// 表示一条代理主入口配置，用于描述系统中可独立存在并对外暴露的逻辑入口。
/// </summary>
[SugarTable("ProxyRouteEntries")]
[SugarIndex("UX_ProxyRouteEntries_EntryName", nameof(EntryName), OrderByType.Asc, true)]
public sealed class ProxyRouteEntry
{
    /// <summary>
    /// 主入口唯一标识，用于在配置和关联关系中定位具体入口。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 主入口名称，用于对外展示或在内部区分不同的代理入口。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = false)]
    public string EntryName { get; set; } = string.Empty;

    /// <summary>
    /// 主入口创建时间，用于记录该入口配置的建立时间。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
