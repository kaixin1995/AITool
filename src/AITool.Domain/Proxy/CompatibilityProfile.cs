using SqlSugar;

namespace AITool.Domain.Proxy;

/// <summary>
/// 兼容规则集：描述转发某上游前对请求体做的字段级变换（剔除/重命名/补默认值）。
/// <para>
/// 一个规则集可被多个模型引用（通过 ModelLibraryItem.CompatibilityProfileId），
/// 避免在每台模型上重复配相同的兼容规则。例如「GPT-5 兼容」规则集可被所有 GPT-5.x 模型勾选。
/// </para>
/// <para>
/// 规则集合在启动期加载到内存缓存，转发时按当前路径（透传/兼容中转）筛选规则应用。
/// </para>
/// </summary>
[SugarTable("CompatibilityProfiles")]
public sealed class CompatibilityProfile
{
    /// <summary>
    /// 规则集唯一标识。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 规则集名称，用于页面展示和模型引用选择，如「GPT-5 兼容」「z.ai 兼容」。
    /// </summary>
    [SugarColumn(Length = 100, IsNullable = false)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 规则集说明，描述适用场景，如「剥离 reasoning_content，兼容 GPT-5 chat completions」。
    /// </summary>
    [SugarColumn(Length = 500, IsNullable = false)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 规则数组 JSON，每条规则形如：
    /// {"op":"strip","target":"reasoning_content","scope":"all"}
    /// {"op":"rename","from":"reasoning_effort","to":"effort","scope":"all"}
    /// {"op":"default","key":"store","value":"false","scope":"bridge"}
    /// op 取值 strip/rename/default；scope 取值 passthrough（仅透传）/bridge（仅兼容中转）/all（两者，默认）。
    /// strip 的 target 沿用路径语法（裸字段名自动当作 messages[].字段名）。
    /// </summary>
    [SugarColumn(ColumnDataType = "Text", IsNullable = false)]
    public string RulesJson { get; set; } = "[]";

    /// <summary>
    /// 是否启用。禁用的规则集不会出现在模型引用下拉中，也不会被转发逻辑应用。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 创建时间。
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 最后更新时间。
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
