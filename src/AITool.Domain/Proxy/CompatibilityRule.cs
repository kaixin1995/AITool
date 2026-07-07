using System.Text.Json.Serialization;

namespace AITool.Domain.Proxy;

/// <summary>
/// 兼容规则集里的一条规则。对应 CompatibilityProfile.RulesJson 数组中的一项。
/// </summary>
public sealed class CompatibilityRule
{
    /// <summary>
    /// 操作类型：strip（剔除字段）/ rename（重命名顶层字段）/ default（为缺失字段补默认值）。
    /// </summary>
    [JsonPropertyName("op")]
    public string Op { get; set; } = "strip";

    /// <summary>
    /// strip 的目标字段路径。沿用路径语法：顶层字段直接写名字（metadata）；
    /// 裸字段名自动当作 messages[].字段名（reasoning_content）；也可写精确路径 a.b 或 a[].b。
    /// </summary>
    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    /// <summary>
    /// rename 的原字段名（仅顶层）。
    /// </summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>
    /// rename 的新字段名。
    /// </summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    /// <summary>
    /// default 的字段名（仅顶层）。
    /// </summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// default 的字段值（字符串形式，应用时按 true/false/数字/字符串推断类型）。
    /// </summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// 生效路径：passthrough（仅透传）/ bridge（仅兼容中转）/ all（两者，默认）。
    /// </summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "all";
}
