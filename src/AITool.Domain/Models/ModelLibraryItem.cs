using SqlSugar;

namespace AITool.Domain.Models;

/// <summary>
/// 表示模型库中的一项定义，用于集中维护可供代理和检测使用的模型名称及其显示信息。
/// </summary>
[SugarTable("ModelLibraryItems")]
[SugarIndex("UX_ModelLibraryItems_ModelName", nameof(ModelName), OrderByType.Asc, true)]
public sealed class ModelLibraryItem
{
    /// <summary>
    /// 模型库项唯一标识，用于在映射、健康监控等关联关系中引用该模型。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 模型名称，作为对外暴露和路由匹配的唯一标识，需保证全局唯一。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = false)]
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// 显示名称，用于在界面上呈现更友好的模型标识，便于用户识别。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = false)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 模型类型（兼容旧版 EF 阴影属性 ModelType，迁移到 SqlSugar 后改为实体真实属性，固定为 chat）。
    /// </summary>
    [SugarColumn(Length = 50, IsNullable = false, ColumnName = "ModelType")]
    public string ModelType { get; set; } = "chat";

    /// <summary>
    /// 强制覆盖的思考等级。留空表示不干预（透传客户端原始值）；
    /// 非空时无论客户端传什么，转发给上游时都强制覆盖成这个值。
    /// 支持标准值（low/medium/high/xhigh/max）和自定义值。
    /// </summary>
    [SugarColumn(Length = 50, IsNullable = false)]
    public string OverrideReasoningEffort { get; set; } = string.Empty;

    /// <summary>
    /// 关联的兼容规则集 Id。转发上游前按该规则集对请求体做字段级变换（剔除/重命名/补默认值）。
    /// 为空表示不应用任何规则集。规则集独立维护，可被多个模型引用。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public Guid? CompatibilityProfileId { get; set; }

    /// <summary>
    /// 模型维度的默认客户端特征模拟预设（None | OpenCode | ClaudeCode | CodexCli | Antigravity | Custom）。
    /// </summary>
    [SugarColumn(Length = 50, IsNullable = false)]
    public string ClientEmulation { get; set; } = "None";

    /// <summary>
    /// 模型维度的默认自定义转发请求头（JSON 格式）。
    /// </summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "text")]
    public string? ExtraHeadersJson { get; set; }

    /// <summary>
    /// 标记该模型当前是否启用，禁用后不再参与代理路由和检测任务。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 模型库项配置创建时间，用于记录该模型定义何时被加入系统。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
