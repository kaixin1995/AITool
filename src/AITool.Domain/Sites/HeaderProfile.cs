namespace AITool.Domain.Sites;

/// <summary>
/// 请求头模板与客户端仿真配置方案。
/// 支持系统内置与用户自定义扩展，保存在本地 client-header-profiles.json 中，脱离数据库存储。
/// </summary>
public class HeaderProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 唯一标识 Key，例如 "OpenCode", "ClaudeCode", "CodexCli", "CodexVsCode", "ZCode", "Antigravity", "my-custom-1"
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// 显示名称，例如 "OpenCode CLI 终端", "Claude Code 官方命令行"
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 描述说明，例如 "模拟 OpenCode 终端，自动注入动态 Session 和 Request ID"
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// 模板请求头字典 JSON，例如 {"User-Agent": "opencode/1.18.18", "x-opencode-client": "cli"}
    /// </summary>
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
    public DateTimeOffset? UpdatedAt { get; set; }
}
