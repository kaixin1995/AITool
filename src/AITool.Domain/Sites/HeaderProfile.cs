using SqlSugar;

namespace AITool.Domain.Sites;

/// <summary>
/// 请求头模板与客户端仿真配置方案。
/// 支持系统内置与用户自定义扩展，供模型库和站点映射快捷下拉引用。
/// </summary>
[SugarTable("HeaderProfiles")]
[SugarIndex("UX_HeaderProfiles_Key", nameof(Key), OrderByType.Asc, true)]
public class HeaderProfile
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 唯一标识 Key，例如 "OpenCode", "ClaudeCode", "CodexCli", "Antigravity", "GeminiCli", "my-custom-1"
    /// </summary>
    [SugarColumn(Length = 64)]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// 显示名称，例如 "OpenCode CLI 终端", "Claude Code 官方命令行"
    /// </summary>
    [SugarColumn(Length = 128)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 描述说明，例如 "模拟 OpenCode 终端，自动注入动态 Session 和 Request ID"
    /// </summary>
    [SugarColumn(Length = 512, IsNullable = true)]
    public string? Description { get; set; }

    /// <summary>
    /// 模板请求头字典 JSON，例如 {"User-Agent": "opencode/1.15.0", "x-opencode-client": "cli"}
    /// </summary>
    [SugarColumn(ColumnDataType = "TEXT", IsNullable = true)]
    public string? HeadersJson { get; set; }

    /// <summary>
    /// 是否为系统内置预设（内置预设不可删除，但可克隆）
    /// </summary>
    public bool IsBuiltIn { get; set; }

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
