namespace ProtocolSyncCheck;

/// <summary>
/// 将 CLIProxyAPI 的动态 JSON 字段基线与当前项目实际处理的字段做对比。
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
                    .Select(field => field.TypeHint)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(type => type, StringComparer.OrdinalIgnoreCase));

                var typeMatchStatus = EvaluateTypeMatch(referenceFields, currentUsage);
                rows.Add(new FieldAlignmentRow(
                    fieldGroup.Key,
                    referenceTypes,
                    referenceFields.All(field => field.Optional),
                    currentUsage is not null,
                    currentUsage?.DisplayTypeHints ?? "—",
                    currentUsage?.Locations ?? [],
                    typeMatchStatus));
            }

            results.Add(new FieldDiffResult(group, rows));
        }

        return results;
    }

    /// <summary>
    /// 评估字段在类型层面是否存在明显不一致。
    /// </summary>
    private static FieldTypeMatchStatus EvaluateTypeMatch(List<ProtocolField> referenceFields, CurrentFieldUsage? currentUsage)
    {
        if (currentUsage is null)
        {
            return FieldTypeMatchStatus.Missing;
        }

        if (currentUsage.TypeHints.Contains("pass-through"))
        {
            return FieldTypeMatchStatus.PassThrough;
        }

        if (currentUsage.TypeHints.Contains("conversion"))
        {
            return FieldTypeMatchStatus.BridgeHandled;
        }

        if (currentUsage.TypeHints.Contains("semantic-target") || currentUsage.TypeHints.Contains("semantic-source"))
        {
            return FieldTypeMatchStatus.SemanticHandled;
        }

        // 动态 JSON 只能证明字段被访问，不能证明结构与参考类型一致。
        if (currentUsage.TypeHints.Contains("json"))
        {
            return FieldTypeMatchStatus.DynamicHandled;
        }

        var referenceKinds = referenceFields
            .Select(field => NormalizeReferenceTypeKind(field.TypeHint))
            .Where(kind => kind != "unknown")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (referenceKinds.Count == 0 || referenceKinds.Contains("json"))
        {
            return FieldTypeMatchStatus.DynamicHandled;
        }

        var currentKinds = currentUsage.TypeHints
            .Select(NormalizeCurrentTypeKind)
            .Where(kind => kind != "unknown" && kind != "json")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (currentKinds.Count == 0)
        {
            return FieldTypeMatchStatus.DynamicHandled;
        }

        return currentKinds.Overlaps(referenceKinds)
            ? FieldTypeMatchStatus.Matched
            : FieldTypeMatchStatus.TypeMismatch;
    }

    /// <summary>
    /// 将参考代码中的类型线索归一化为 JSON 类型类别。
    /// </summary>
    private static string NormalizeReferenceTypeKind(string type)
    {
        var normalized = type.Trim();
        return normalized switch
        {
            "string" => "string",
            "bool" => "bool",
            "number" => "number",
            "array" => "array",
            "object" => "object",
            "json" => "json",
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
            "scalar" => "scalar",
            "null" => "null",
            "json" => "json",
            _ => "unknown"
        };
    }
}

/// <summary>
/// 参考项目动态 JSON 处理逻辑中检测到的字段。
/// </summary>
internal sealed record ProtocolField(
    string SourceName,
    string TypeHint,
    string JsonName,
    bool Optional);

/// <summary>
/// 一个协议接口对应的动态字段分组。
/// </summary>
internal sealed class ProtocolStructGroup(
    string label,
    string description,
    string[] routeKeys,
    List<string> structNames,
    List<ProtocolField> fields)
{
    public string Label { get; } = label;
    public string Description { get; } = description;

    /// <summary>
    /// 该分组关联的协议接口键（Protocol:Method Path），用于接口状态表。
    /// </summary>
    public IReadOnlyList<string> RouteKeys { get; } = routeKeys;

    public List<string> StructNames { get; } = structNames;
    public List<ProtocolField> Fields { get; } = fields;
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
    List<CurrentFieldLocation> currentLocations,
    FieldTypeMatchStatus typeMatchStatus)
{
    public string FieldName { get; } = fieldName;
    public string ReferenceType { get; } = referenceType;
    public bool Optional { get; } = optional;
    public bool IsDetected { get; } = isDetected;
    public string CurrentTypeHint { get; } = currentTypeHint;
    public IReadOnlyList<CurrentFieldLocation> CurrentLocations { get; } = currentLocations;
    public FieldTypeMatchStatus TypeMatchStatus { get; } = typeMatchStatus;
    public bool IsAligned => TypeMatchStatus is not FieldTypeMatchStatus.Missing
        and not FieldTypeMatchStatus.TypeMismatch;
    public bool IsPassThrough => TypeMatchStatus == FieldTypeMatchStatus.PassThrough;
    public bool IsBridgeHandled => TypeMatchStatus is FieldTypeMatchStatus.BridgeHandled
        or FieldTypeMatchStatus.SemanticHandled;
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
    DynamicHandled,
    PassThrough,
    BridgeHandled,
    SemanticHandled,
    Missing,
    TypeMismatch
}
