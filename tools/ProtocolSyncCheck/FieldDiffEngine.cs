namespace ProtocolSyncCheck;

/// <summary>
/// 将 new-api 的 Go struct 按协议接口分组，并与当前项目实际处理的字段做对比。
/// </summary>
internal static class FieldDiffEngine
{

    /// <summary>
    /// 计算每个分组中的字段对齐情况。
    /// </summary>
    public static List<FieldDiffResult> ComputeDiffs(
        List<ProtocolStructGroup> groups,
        Dictionary<string, CurrentFieldUsage> currentProjectFields)
    {
        var results = new List<FieldDiffResult>();

        foreach (var group in groups)
        {
            var fieldMap = group.Fields
                .GroupBy(field => field.JsonName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(grouping => grouping.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rows = new List<FieldAlignmentRow>();
            foreach (var fieldGroup in fieldMap)
            {
                currentProjectFields.TryGetValue(fieldGroup.Key, out var currentUsage);

                var referenceFields = fieldGroup.ToList();
                var referenceTypes = string.Join(" / ", referenceFields
                    .Select(field => field.GoType)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(type => type, StringComparer.OrdinalIgnoreCase));

                var typeMatchStatus = EvaluateTypeMatch(referenceFields, currentUsage);
                rows.Add(new FieldAlignmentRow(
                    fieldGroup.Key,
                    referenceTypes,
                    referenceFields.All(field => field.OmitEmpty),
                    currentUsage is not null,
                    currentUsage?.DisplayTypeHints ?? "—",
                    typeMatchStatus));
            }

            results.Add(new FieldDiffResult(group, rows));
        }

        return results;
    }

    /// <summary>
    /// 评估字段在类型层面是否存在明显不一致。
    /// </summary>
    private static FieldTypeMatchStatus EvaluateTypeMatch(List<GoStructField> referenceFields, CurrentFieldUsage? currentUsage)
    {
        if (currentUsage is null)
        {
            return FieldTypeMatchStatus.Missing;
        }

        // 当前项目大量通过 JsonNode / JsonObject 动态透传或组装字段。
        // 只在存在“强类型且明显不一致”的证据时才判为类型不一致，避免把动态处理误报为缺口。
        if (currentUsage.TypeHints.Contains("json") || currentUsage.TypeHints.Contains("scalar"))
        {
            return FieldTypeMatchStatus.Matched;
        }

        var referenceKinds = referenceFields
            .Select(field => NormalizeGoTypeKind(field.GoType))
            .Where(kind => kind != "unknown")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (referenceKinds.Count == 0 || referenceKinds.Contains("json"))
        {
            return FieldTypeMatchStatus.Matched;
        }

        var currentKinds = currentUsage.TypeHints
            .Select(NormalizeCurrentTypeKind)
            .Where(kind => kind != "unknown" && kind != "json" && kind != "scalar")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (currentKinds.Count == 0)
        {
            return FieldTypeMatchStatus.Matched;
        }

        return currentKinds.Overlaps(referenceKinds)
            ? FieldTypeMatchStatus.Matched
            : FieldTypeMatchStatus.TypeMismatch;
    }

    /// <summary>
    /// 将 Go 类型归一化为更适合比较的 JSON 类型类别。
    /// </summary>
    private static string NormalizeGoTypeKind(string goType)
    {
        var type = goType.Trim().TrimStart('*');
        if (type.StartsWith("[]", StringComparison.Ordinal))
        {
            return "array";
        }

        if (type.StartsWith("map[", StringComparison.Ordinal))
        {
            return "object";
        }

        return type switch
        {
            "string" => "string",
            "bool" => "bool",
            "int" or "int32" or "int64" or "uint" or "uint32" or "uint64" or "float32" or "float64" => "number",
            "interface{}" or "any" => "json",
            _ when type.Contains("Response", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Request", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Message", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Content", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Tool", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Usage", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Thinking", StringComparison.OrdinalIgnoreCase) => "object",
            _ => "unknown"
        };
    }

    /// <summary>
    /// 将当前项目扫描到的类型线索归一化为可对比的类别。
    /// </summary>
    private static string NormalizeCurrentTypeKind(string currentType)
    {
        return currentType switch
        {
            "string" => "string",
            "bool" => "bool",
            "number" => "number",
            "array" => "array",
            "object" => "object",
            "null" => "null",
            "json" or "scalar" => currentType,
            _ => "unknown"
        };
    }

}

/// <summary>
/// 一个协议接口对应的 struct 字段分组。
/// </summary>
internal sealed class ProtocolStructGroup(
    string label,
    string description,
    List<string> structNames,
    List<GoStructField> fields)
{
    public string Label { get; } = label;
    public string Description { get; } = description;
    public List<string> StructNames { get; } = structNames;
    public List<GoStructField> Fields { get; } = fields;
}

/// <summary>
/// 单个字段的对齐行。
/// </summary>
internal sealed class FieldAlignmentRow(
    string fieldName,
    string referenceType,
    bool optional,
    bool isDetected,
    string currentTypeHint,
    FieldTypeMatchStatus typeMatchStatus)
{
    public string FieldName { get; } = fieldName;
    public string ReferenceType { get; } = referenceType;
    public bool Optional { get; } = optional;
    public bool IsDetected { get; } = isDetected;
    public string CurrentTypeHint { get; } = currentTypeHint;
    public FieldTypeMatchStatus TypeMatchStatus { get; } = typeMatchStatus;
    public bool IsAligned => TypeMatchStatus == FieldTypeMatchStatus.Matched;
}

/// <summary>
/// 字段对齐结果。
/// </summary>
internal sealed class FieldDiffResult(ProtocolStructGroup group, List<FieldAlignmentRow> rows)
{
    public ProtocolStructGroup Group { get; } = group;
    public List<FieldAlignmentRow> Rows { get; } = rows;
    public List<FieldAlignmentRow> AlignedRows => Rows.Where(row => row.IsAligned).ToList();
    public List<FieldAlignmentRow> MisalignedRows => Rows.Where(row => !row.IsAligned).ToList();
    public bool HasMismatch => MisalignedRows.Count > 0;
}

/// <summary>
/// 字段类型对齐状态。
/// </summary>
internal enum FieldTypeMatchStatus
{
    Matched,
    Missing,
    TypeMismatch
}
