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
    /// 标记该模型当前是否启用，禁用后不再参与代理路由和检测任务。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 模型库项配置创建时间，用于记录该模型定义何时被加入系统。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
