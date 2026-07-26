using SqlSugar;

namespace AITool.Domain.Models;

/// <summary>
/// 表示模型库中的一条统一模型定义，用于在不同站点、路由和检测场景中复用同一套模型标识。
/// </summary>
[SugarTable("ModelLibraryItems")]
[SugarIndex("UX_ModelLibraryItems_ModelName", nameof(ModelName), OrderByType.Asc, true)]
public sealed class ModelLibraryItem
{
    /// <summary>
    /// 模型唯一标识，用于在系统内部稳定引用该模型定义。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 统一模型名称，作为系统内部识别模型的标准名称。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = false)]
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// 页面展示名称，用于界面显示或对外说明时提供更友好的可读文本。
    /// </summary>
    [SugarColumn(Length = 200, IsNullable = true)]
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
    /// 标记该模型当前是否启用，禁用后不再参与代理路由和检测任务。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 模型定义创建时间，用于保留基础数据的建立时间信息。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
