using SqlSugar;

namespace AITool.Domain.Proxy;

/// <summary>
/// 表示一条代理访问密钥配置，用于识别和校验进入代理入口的调用方身份。
/// </summary>
[SugarTable("ProxyAccessKeys")]
[SugarIndex("IX_ProxyAccessKeys_AccessKeyHash_IsEnabled", nameof(AccessKeyHash), OrderByType.Asc, nameof(IsEnabled), OrderByType.Asc)]
public sealed class ProxyAccessKey
{
    /// <summary>
    /// 密钥唯一标识，用于在日志、配置管理和鉴权逻辑中引用该密钥。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 密钥名称，用于后台管理时区分不同用途或不同调用方的密钥。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = false)]
    public string KeyName { get; set; } = string.Empty;

    /// <summary>
    /// 明文密钥值，用于系统保存或展示当前可直接使用的访问密钥内容。
    /// </summary>
    [SugarColumn(Length = 500, IsNullable = false)]
    public string PlainKey { get; set; } = string.Empty;

    /// <summary>
    /// 密钥哈希值，用于兼容历史数据或在不直接比较明文的场景下完成校验。
    /// </summary>
    [SugarColumn(Length = 500, IsNullable = false)]
    public string AccessKeyHash { get; set; } = string.Empty;

    /// <summary>
    /// 掩码后的显示值，用于在界面上安全展示密钥的大致信息并兼容历史存储结构。
    /// </summary>
    [SugarColumn(Length = 100, IsNullable = false)]
    public string MaskedValue { get; set; } = string.Empty;

    /// <summary>
    /// 标记该密钥当前是否可用，禁用后不应再通过该密钥访问代理能力。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 允许访问的路由入口名称（对外模型名）的 JSON 数组，如 ["gpt-4","claude-sonnet-4-5"]。
    /// 空串表示允许全部路由（包括后续新增的），非空表示只能访问列表中的路由入口。
    /// </summary>
    [SugarColumn(Length = -1, IsNullable = false)]
    public string AllowedRouteNames { get; set; } = string.Empty;
}
