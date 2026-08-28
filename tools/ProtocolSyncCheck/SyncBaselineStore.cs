using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProtocolSyncCheck;

/// <summary>
/// 运行间基线快照：每次运行把三方的路由全集与参考项目字段基线存为 JSON，
/// 下次运行对比后产出「自上次运行以来的协议变更」——参考项目更新后跑一次即可看出新增/移除的端点与字段。
/// 基线文件与报告一样属于本地生成物（gitignore），不区分 git 历史。
/// </summary>
internal static class SyncBaselineStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string BaselinePath(string repositoryRoot) =>
        Path.Combine(repositoryRoot, "docs", "protocol-sync-baseline.json");

    /// <summary>
    /// 读取上次运行的基线；文件不存在（首次运行）返回 null。
    /// </summary>
    public static SyncBaseline? Load(string repositoryRoot)
    {
        var path = BaselinePath(repositoryRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SyncBaseline>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            // 基线损坏按首次运行处理，不中断扫描。
            return null;
        }
    }

    public static void Save(string repositoryRoot, SyncBaseline baseline)
    {
        File.WriteAllText(BaselinePath(repositoryRoot), JsonSerializer.Serialize(baseline, JsonOptions));
    }

    /// <summary>
    /// 构建当前运行的基线快照。
    /// </summary>
    public static SyncBaseline Capture(
        IReadOnlyList<ProjectScanResult> results,
        List<FieldDiffResult> cpaFieldDiffs,
        List<FieldDiffResult> ccFieldDiffs,
        string cpaCommit,
        string ccCommit)
    {
        var baseline = new SyncBaseline
        {
            GeneratedAt = DateTime.Now,
            References =
            {
                ["CLIProxyAPI"] = cpaCommit,
                ["cc-switch"] = ccCommit
            }
        };

        foreach (var result in results)
        {
            var routeKeys = result.Routes
                .Select(route => route.Protocol + ":" + route.Method + " " + route.Path)
                .Concat(result.UnclassifiedRoutes.Select(route => route.Method + " " + route.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            baseline.Routes[result.ProjectName] = routeKeys;
        }

        baseline.Fields["CLIProxyAPI"] = ExtractFieldIndex(cpaFieldDiffs);
        baseline.Fields["cc-switch"] = ExtractFieldIndex(ccFieldDiffs);
        return baseline;
    }

    private static Dictionary<string, List<string>> ExtractFieldIndex(List<FieldDiffResult> diffs)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var diff in diffs)
        {
            index[diff.Group.Label] = diff.Rows
                .Select(row => row.FieldName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(field => field, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return index;
    }

    /// <summary>
    /// 计算两次基线之间的协议变更。
    /// </summary>
    public static List<BaselineChange> ComputeChanges(SyncBaseline previous, SyncBaseline current)
    {
        var changes = new List<BaselineChange>();

        foreach (var project in current.Routes.Keys.Concat(previous.Routes.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var had = previous.Routes.TryGetValue(project, out var oldRoutes) ? oldRoutes : [];
            var has = current.Routes.TryGetValue(project, out var newRoutes) ? newRoutes : [];
            AddSetChanges(changes, project, "路由", had, has);
        }

        foreach (var referenceName in current.Fields.Keys)
        {
            if (!previous.Fields.TryGetValue(referenceName, out var oldGroups))
            {
                continue;
            }

            var newGroups = current.Fields[referenceName];
            foreach (var groupLabel in newGroups.Keys.Concat(oldGroups.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var had = oldGroups.TryGetValue(groupLabel, out var oldFields) ? oldFields : [];
                var has = newGroups.TryGetValue(groupLabel, out var newFields) ? newFields : [];
                AddSetChanges(changes, referenceName + " · " + groupLabel, "字段", had, has);
            }
        }

        return changes;
    }

    private static void AddSetChanges(List<BaselineChange> changes, string scope, string kind, IReadOnlyList<string> oldValues, IReadOnlyList<string> newValues)
    {
        var oldSet = oldValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newSet = newValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var added in newSet.Except(oldSet).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            changes.Add(new BaselineChange(scope, kind, BaselineChangeKind.Added, added));
        }

        foreach (var removed in oldSet.Except(newSet).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            changes.Add(new BaselineChange(scope, kind, BaselineChangeKind.Removed, removed));
        }
    }
}

/// <summary>
/// 一次运行的快照。
/// </summary>
internal sealed class SyncBaseline
{
    public DateTime GeneratedAt { get; set; }

    /// <summary>参考项目基准 commit（项目名 → 短哈希）。</summary>
    public Dictionary<string, string> References { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>三方路由全集（项目名 → 路由键列表，含未跟踪路由）。</summary>
    public Dictionary<string, List<string>> Routes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>参考项目字段基线（项目名 → 分组标签 → 字段名列表）。</summary>
    public Dictionary<string, Dictionary<string, List<string>>> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 单条基线变更。
/// </summary>
internal sealed record BaselineChange(string Scope, string Kind, BaselineChangeKind ChangeKind, string Value);

internal enum BaselineChangeKind
{
    Added,
    Removed
}
