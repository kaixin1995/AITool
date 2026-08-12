using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

using ProtocolSyncCheck;

var repositoryRoot = ResolveRepositoryRoot(args);
var outputPath = Path.Combine(repositoryRoot, "docs", "protocol-sync-report.md");

// 只更新 CLIProxyAPI 参考代码，避免扫描或拉取无关项目。
GitPullHelper.PullCliProxyApi(repositoryRoot);

var catalog = ProtocolCatalog.CreateDefault();
var projects = new[]
{
    ProjectScanDefinition.CurrentProject(repositoryRoot),
    ProjectScanDefinition.CliProxyApi(repositoryRoot)
};

var results = projects
    .Select(project => ProtocolScanner.Scan(project, catalog))
    .ToArray();

var currentProjectFiles = Directory
    .GetFiles(Path.Combine(repositoryRoot, "src", "AITool.Web", "Services", "ProxyProtocol"), "*.cs")
    .Concat(Directory.GetFiles(Path.Combine(repositoryRoot, "src", "AITool.Web", "Controllers", "Proxy"), "*.cs"))
    .Append(Path.Combine(repositoryRoot, "src", "AITool.Web", "Controllers", "Admin", "ChatApiController.cs"))
    .ToArray();

var currentFields = CSharpFieldScanner.ScanFiles(currentProjectFiles);
var cpaFieldGroups = CpaFieldGroupBuilder.BuildGroups(repositoryRoot);
var cpaFieldDiffs = FieldDiffEngine.ComputeDiffs(cpaFieldGroups, currentFields);

var report = ProtocolReportBuilder.Build(results, catalog, cpaFieldDiffs);
File.WriteAllText(outputPath, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

Console.WriteLine($"协议同步报告已生成：{Path.GetRelativePath(repositoryRoot, outputPath)}");
foreach (var result in results)
{
    Console.WriteLine($"{result.ProjectName}: {result.Routes.Count} routes");
}
Console.WriteLine($"字段级对比：CLIProxyAPI {cpaFieldDiffs.Count} 个分组；AITool {currentFields.Count} 个字段");

static string ResolveRepositoryRoot(string[] args)
{
    if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
    {
        return Path.GetFullPath(args[0]);
    }

    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, "src", "AITool.Web")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    return Directory.GetCurrentDirectory();
}

internal sealed class ProjectScanDefinition
{
    public required string Name { get; init; }
    public required IReadOnlyList<RouteSourceFile> Files { get; init; }

    public static ProjectScanDefinition CurrentProject(string root) => new()
    {
        Name = "AITool",
        Files = new[]
        {
            RouteSourceFile.CSharpController(root, "src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs"),
            RouteSourceFile.CSharpController(root, "src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs"),
            RouteSourceFile.CSharpController(root, "src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs")
        }
    };

    public static ProjectScanDefinition CliProxyApi(string root) => new()
    {
        Name = "CLIProxyAPI",
        Files = new[]
        {
            // 不同版本可能将路由拆分到 server_routes.go 或保留在 server.go，两个文件都扫描。
            RouteSourceFile.GinRouter(root, "reference-projects/CLIProxyAPI/internal/api/server_routes.go"),
            RouteSourceFile.GinRouter(root, "reference-projects/CLIProxyAPI/internal/api/server.go")
        }
    };
}

internal sealed class RouteSourceFile
{
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public required RouteSourceKind Kind { get; init; }

    public static RouteSourceFile CSharpController(string root, string relativePath) =>
        Create(root, relativePath, RouteSourceKind.CSharpController);

    public static RouteSourceFile GinRouter(string root, string relativePath) =>
        Create(root, relativePath, RouteSourceKind.GinRouter);

    private static RouteSourceFile Create(string root, string relativePath, RouteSourceKind kind) => new()
    {
        RelativePath = relativePath,
        FullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)),
        Kind = kind
    };
}

internal enum RouteSourceKind
{
    CSharpController,
    GinRouter
}

internal static class ProtocolScanner
{
    private static readonly Regex CSharpRouteRegex = new(
        "\\[Http(?<method>Get|Post|Delete|Put|Patch)\\(\\\"(?<path>[^\\\"#]+)\\\"\\)\\]",
        RegexOptions.Compiled);

    private static readonly Regex GinRouteRegex = new(
        "(?<receiver>\\w+)\\.(?<method>GET|POST|DELETE|PUT|PATCH)\\(\\\"(?<path>[^\\\"#]+)\\\"",
        RegexOptions.Compiled);

    private static readonly Regex GinGroupRegex = new(
        "(?<name>\\w+)\\s*:=\\s*(?:(?<parent>[\\w.]+)\\.)?Group\\(\\\"(?<prefix>[^\\\"]*)\\\"\\)",
        RegexOptions.Compiled);

    public static ProjectScanResult Scan(ProjectScanDefinition project, ProtocolCatalog catalog)
    {
        var routes = new List<ProtocolRoute>();
        var missingFiles = new List<string>();

        foreach (var file in project.Files)
        {
            if (!File.Exists(file.FullPath))
            {
                missingFiles.Add(file.RelativePath);
                continue;
            }

            routes.AddRange(file.Kind switch
            {
                RouteSourceKind.CSharpController => ScanCSharpController(file, catalog),
                RouteSourceKind.GinRouter => ScanGinRouter(file, catalog),
                _ => []
            });
        }

        var distinctRoutes = routes
            .GroupBy(route => route.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(route => route.SourcePath, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(route => route.Protocol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(route => route.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(route => route.Method, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProjectScanResult(project.Name, distinctRoutes, missingFiles);
    }

    private static List<ProtocolRoute> ScanCSharpController(RouteSourceFile file, ProtocolCatalog catalog)
    {
        var routes = new List<ProtocolRoute>();
        var lines = File.ReadAllLines(file.FullPath);
        for (var index = 0; index < lines.Length; index++)
        {
            var match = CSharpRouteRegex.Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            AddClassifiedRoutes(
                routes,
                catalog,
                match.Groups["method"].Value.ToUpperInvariant(),
                NormalizeRoutePath(match.Groups["path"].Value),
                file.RelativePath,
                index + 1,
                false);
        }

        return routes;
    }

    private static List<ProtocolRoute> ScanGinRouter(RouteSourceFile file, ProtocolCatalog catalog)
    {
        var routes = new List<ProtocolRoute>();
        var groupPrefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = File.ReadAllLines(file.FullPath);

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var groupMatch = GinGroupRegex.Match(line);
            if (groupMatch.Success)
            {
                var name = groupMatch.Groups["name"].Value;
                var parent = groupMatch.Groups["parent"].Value;
                var prefix = NormalizeRoutePath(groupMatch.Groups["prefix"].Value);
                if (!string.IsNullOrWhiteSpace(parent) && groupPrefixes.TryGetValue(parent, out var parentPrefix))
                {
                    prefix = CombinePaths(parentPrefix, prefix);
                }

                groupPrefixes[name] = prefix;
            }

            var routeMatch = GinRouteRegex.Match(line);
            if (!routeMatch.Success)
            {
                continue;
            }

            var receiver = routeMatch.Groups["receiver"].Value;
            var path = NormalizeRoutePath(routeMatch.Groups["path"].Value);
            if (groupPrefixes.TryGetValue(receiver, out var groupPrefix))
            {
                path = CombinePaths(groupPrefix, path);
            }

            AddClassifiedRoutes(
                routes,
                catalog,
                routeMatch.Groups["method"].Value.ToUpperInvariant(),
                path,
                file.RelativePath,
                index + 1,
                false);
        }

        return routes;
    }

    private static void AddClassifiedRoutes(
        List<ProtocolRoute> routes,
        ProtocolCatalog catalog,
        string method,
        string path,
        string sourcePath,
        int lineNumber,
        bool isNotImplemented)
    {
        if (!catalog.TryClassifyAll(method, path, out var classifications))
        {
            return;
        }

        foreach (var classification in classifications)
        {
            routes.Add(new ProtocolRoute(
                method,
                path,
                classification.Protocol,
                classification.Category,
                classification.Description,
                isNotImplemented || classification.IsKnownStub,
                sourcePath,
                lineNumber));
        }
    }

    private static string NormalizeRoutePath(string path)
    {
        var normalized = path.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "/";
        }

        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        normalized = Regex.Replace(normalized, "\\{([^}/]+)\\}", ":$1");
        normalized = Regex.Replace(normalized, @"/:[^/]+$", match => match.Value switch
        {
            "/:modelId" => "/:model",
            "/:request_id" => "/:id",
            "/:task_id" => "/:id",
            _ => match.Value
        });
        return normalized.Replace("//", "/", StringComparison.Ordinal);
    }

    private static string CombinePaths(string prefix, string path) =>
        prefix == "/"
            ? NormalizeRoutePath(path)
            : NormalizeRoutePath(prefix.TrimEnd('/') + "/" + path.TrimStart('/'));
}

internal sealed record ProjectScanResult(
    string ProjectName,
    IReadOnlyList<ProtocolRoute> Routes,
    IReadOnlyList<string> MissingFiles);

internal sealed record ProtocolRoute(
    string Method,
    string Path,
    string Protocol,
    string Category,
    string Description,
    bool IsNotImplemented,
    string SourcePath,
    int LineNumber)
{
    public string Key => $"{Protocol}:{Method} {Path}";
}

internal sealed class ProtocolCatalog
{
    private readonly Dictionary<string, RouteClassification> _knownRoutes;

    private ProtocolCatalog(IEnumerable<RouteClassification> routes)
    {
        _knownRoutes = routes.ToDictionary(route => route.Key, StringComparer.OrdinalIgnoreCase);
    }

    public static ProtocolCatalog CreateDefault() => new(new[]
    {
        RouteClassification.Primary("OpenAI", "GET", "/v1/models", "模型列表"),
        RouteClassification.Primary("OpenAI", "GET", "/v1/models/:model", "单模型查询"),
        RouteClassification.Primary("OpenAI", "POST", "/v1/chat/completions", "Chat Completions"),
        RouteClassification.Legacy("OpenAI", "POST", "/v1/completions", "Legacy Completions"),
        RouteClassification.Primary("OpenAI", "POST", "/v1/responses", "Responses API"),
        RouteClassification.Extension("OpenAI", "POST", "/v1/responses/compact", "Responses compact 扩展"),
        RouteClassification.Extension("OpenAI", "GET", "/v1/responses", "Responses WebSocket 扩展"),
        RouteClassification.Primary("OpenAI", "POST", "/v1/embeddings", "Embeddings"),
        RouteClassification.Primary("OpenAI", "POST", "/v1/images/generations", "图像生成"),
        RouteClassification.Primary("OpenAI", "POST", "/v1/images/edits", "图像编辑"),
        RouteClassification.Legacy("OpenAI", "POST", "/v1/edits", "Legacy edits"),
        RouteClassification.Primary("OpenAI", "POST", "/v1/audio/transcriptions", "音频转录"),
        RouteClassification.Primary("OpenAI", "POST", "/v1/audio/translations", "音频翻译"),
        RouteClassification.Primary("OpenAI", "POST", "/v1/audio/speech", "语音合成"),
        RouteClassification.Primary("OpenAI", "POST", "/v1/moderations", "Moderations"),
        RouteClassification.Primary("OpenAI", "POST", "/v1/videos", "视频创建"),
        RouteClassification.Primary("OpenAI", "GET", "/v1/videos/:id", "视频查询"),
        RouteClassification.Extension("OpenAI", "GET", "/v1/realtime", "Realtime WebSocket"),
        RouteClassification.Extension("OpenAI", "POST", "/v1/videos/generations", "视频生成扩展"),
        RouteClassification.Extension("OpenAI", "GET", "/v1/videos/generations/:id", "视频任务查询扩展"),
        RouteClassification.Extension("OpenAI", "POST", "/v1/videos/:video_id/remix", "视频 remix 扩展"),
        RouteClassification.Extension("OpenAI", "POST", "/v1/videos/edits", "视频编辑扩展"),
        RouteClassification.Extension("OpenAI", "POST", "/v1/videos/extensions", "视频扩展"),
        RouteClassification.Extension("OpenAI", "GET", "/v1/videos/:id/content", "视频内容代理"),
        RouteClassification.Extension("OpenAI", "POST", "/v1/rerank", "Rerank 扩展"),
        RouteClassification.Extension("OpenAI", "POST", "/v1/engines/:model/embeddings", "旧式 embeddings 兼容路径"),
        RouteClassification.Extension("OpenAI", "POST", "/v1/models/*path", "Gemini 兼容路径"),
        RouteClassification.Primary("Anthropic", "GET", "/v1/models", "Anthropic 模型列表", matchPath: false),
        RouteClassification.Primary("Anthropic", "GET", "/v1/models/:model", "Anthropic 单模型查询", matchPath: false),
        RouteClassification.Primary("Anthropic", "POST", "/v1/messages", "Anthropic Messages"),
        RouteClassification.Primary("Anthropic", "POST", "/v1/messages/count_tokens", "Anthropic Count Tokens")
    });

    public bool TryClassifyAll(string method, string path, out IReadOnlyList<RouteClassification> classifications)
    {
        var matches = new List<RouteClassification>();
        if (_knownRoutes.TryGetValue(RouteClassification.BuildLookupKey(method, path), out var directMatch))
        {
            matches.Add(directMatch);
        }

        if (_knownRoutes.TryGetValue(RouteClassification.BuildLookupKey(method, path, "Anthropic"), out var protocolMatch))
        {
            matches.Add(protocolMatch);
        }

        classifications = matches;
        return matches.Count > 0;
    }

    public IReadOnlyList<RouteClassification> SyncTargets => _knownRoutes.Values
        .Where(route => route.Category is "主协议" or "legacy")
        .OrderBy(route => route.Protocol, StringComparer.OrdinalIgnoreCase)
        .ThenBy(route => route.Path, StringComparer.OrdinalIgnoreCase)
        .ThenBy(route => route.Method, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

internal sealed record RouteClassification(
    string Protocol,
    string Method,
    string Path,
    string Category,
    string Description,
    bool IsKnownStub = false,
    bool MatchPath = true)
{
    public string Key => BuildLookupKey(Method, Path, MatchPath ? string.Empty : Protocol);
    public string ComparisonKey => $"{Protocol}:{Method.ToUpperInvariant()} {Path}";

    public static string BuildLookupKey(string method, string path, string protocol = "") =>
        string.IsNullOrWhiteSpace(protocol)
            ? $"{method.ToUpperInvariant()} {path}"
            : $"{protocol}:{method.ToUpperInvariant()} {path}";

    public static RouteClassification Primary(string protocol, string method, string path, string description, bool matchPath = true) =>
        new(protocol, method, path, "主协议", description, MatchPath: matchPath);

    public static RouteClassification Legacy(string protocol, string method, string path, string description) =>
        new(protocol, method, path, "legacy", description);

    public static RouteClassification Extension(string protocol, string method, string path, string description) =>
        new(protocol, method, path, "扩展", description);
}

internal static class ProtocolReportBuilder
{
    public static string Build(
        IReadOnlyList<ProjectScanResult> results,
        ProtocolCatalog catalog,
        List<FieldDiffResult> cpaFieldDiffs)
    {
        var current = results.First(result => result.ProjectName == "AITool");
        var cpa = results.First(result => result.ProjectName == "CLIProxyAPI");
        var builder = new StringBuilder();

        builder.AppendLine("# AITool 与 CLIProxyAPI 协议同步检查报告");
        builder.AppendLine();
        AppendScanPrerequisites(builder, current, cpa);
        AppendOverview(builder, current, cpa, catalog, cpaFieldDiffs);
        AppendRouteComparison(builder, current, cpa, catalog);
        AppendFieldAlignmentReport(builder, cpaFieldDiffs);
        AppendDiagnosticConclusion(builder, current, cpa, cpaFieldDiffs);
        return builder.ToString();
    }

    private static void AppendScanPrerequisites(StringBuilder builder, ProjectScanResult current, ProjectScanResult cpa)
    {
        var missing = current.MissingFiles
            .Select(file => $"AITool：`{file}`")
            .Concat(cpa.MissingFiles.Select(file => $"CLIProxyAPI：`{file}`"))
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }

        builder.AppendLine("## 扫描前提异常");
        builder.AppendLine();
        builder.AppendLine("> 参考文件缺失时，相关差异结论可能不完整。");
        foreach (var item in missing)
        {
            builder.AppendLine($"- 未找到 {item}");
        }
        builder.AppendLine();
    }

    private static void AppendOverview(
        StringBuilder builder,
        ProjectScanResult current,
        ProjectScanResult cpa,
        ProtocolCatalog catalog,
        List<FieldDiffResult> cpaFieldDiffs)
    {
        var missingRoutes = CollectReferenceOnlyRoutes(current, cpa, catalog);
        var mismatchedGroups = cpaFieldDiffs.Count(diff => diff.HasMismatch);
        var alignedGroups = cpaFieldDiffs.Count - mismatchedGroups;

        builder.AppendLine("## 总览");
        builder.AppendLine();
        builder.AppendLine($"- CLIProxyAPI 已支持但 AITool 未实现的主协议接口：**{missingRoutes.Count}** 个");
        builder.AppendLine($"- CLIProxyAPI 字段未对齐分组：**{mismatchedGroups}** 个");
        builder.AppendLine($"- CLIProxyAPI 字段完全对齐分组：**{alignedGroups}** 个");
        builder.AppendLine();
    }

    private static void AppendRouteComparison(
        StringBuilder builder,
        ProjectScanResult current,
        ProjectScanResult cpa,
        ProtocolCatalog catalog)
    {
        var missingRoutes = CollectReferenceOnlyRoutes(current, cpa, catalog);

        builder.AppendLine("## CLIProxyAPI 已支持但 AITool 未实现的接口");
        builder.AppendLine();
        if (missingRoutes.Count == 0)
        {
            builder.AppendLine("✅ 未发现 CLIProxyAPI 已支持而 AITool 缺失的 OpenAI / Anthropic 主协议接口。");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("| 协议 | 分类 | Method | URL | 说明 |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var item in missingRoutes)
        {
            builder.AppendLine($"| {EscapeMarkdown(item.Target.Protocol)} | {EscapeMarkdown(item.Target.Category)} | {item.Target.Method} | `{item.Target.Path}` | {EscapeMarkdown(item.Target.Description)} |");
        }
        builder.AppendLine();
    }

    private static void AppendFieldAlignmentReport(StringBuilder builder, List<FieldDiffResult> fieldDiffs)
    {
        builder.AppendLine("## AITool 与 CLIProxyAPI 字段对比");
        builder.AppendLine();
        builder.AppendLine("> 字段基线来自 CLIProxyAPI 的请求/响应处理函数；AITool 侧同时扫描协议桥接代码、代理控制器和 Responses 流式状态代码。字段出现但语义未必完全等价，需结合状态和来源位置判断。");
        builder.AppendLine();

        foreach (var diff in fieldDiffs.OrderBy(diff => diff.Group.Label, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"### {EscapeMarkdown(diff.Group.Label)}");
            builder.AppendLine();
            builder.AppendLine($"- 对齐情况：{diff.AlignedRows.Count}/{diff.Rows.Count}");
            builder.AppendLine($"- 需要关注：{diff.MisalignedRows.Count}");
            builder.AppendLine();

            if (diff.MisalignedRows.Count == 0)
            {
                builder.AppendLine("✅ CLIProxyAPI 参考字段均已在 AITool 中检测到处理逻辑。");
                builder.AppendLine();
                continue;
            }

            builder.AppendLine("| 字段 | CLIProxyAPI 类型 | 可选 | AITool 状态 | AITool 类型线索 | AITool 位置 |");
            builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var row in diff.MisalignedRows)
            {
                var locations = GetLocations(row);
                builder.AppendLine($"| `{row.FieldName}` | `{EscapeMarkdown(row.ReferenceType)}` | {FormatOptional(row.Optional)} | {FormatFieldStatus(row.TypeMatchStatus)} | {EscapeMarkdown(row.CurrentTypeHint)} | {EscapeMarkdown(locations)} |");
            }
            builder.AppendLine();
        }
    }

    private static string GetLocations(FieldAlignmentRow row)
    {
        if (row.CurrentLocations.Count == 0)
        {
            return "—";
        }

        return string.Join("<br>", row.CurrentLocations
            .Take(3)
            .Select(location => $"{Path.GetFileName(location.FilePath)}:{location.LineNumber}"));
    }

    private static void AppendDiagnosticConclusion(
        StringBuilder builder,
        ProjectScanResult current,
        ProjectScanResult cpa,
        List<FieldDiffResult> fieldDiffs)
    {
        var missingRoutes = cpa.Routes
            .Where(route => !route.IsNotImplemented)
            .Select(route => route.Key)
            .Except(current.Routes.Where(route => !route.IsNotImplemented).Select(route => route.Key), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mismatches = fieldDiffs.SelectMany(diff => diff.MisalignedRows).ToList();

        builder.AppendLine("## 本次运行的排查结论");
        builder.AppendLine();
        if (missingRoutes.Count == 0 && mismatches.Count == 0 && current.MissingFiles.Count == 0 && cpa.MissingFiles.Count == 0)
        {
            builder.AppendLine("✅ 静态路由和字段扫描没有发现 CLIProxyAPI 与 AITool 的明显协议缺口。");
            builder.AppendLine("如果运行时仍出现空响应或流式内容缺失，应继续核对实际上游 SSE / JSON 原文及转换日志。");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("### 优先级判断");
        builder.AppendLine();
        if (missingRoutes.Count > 0)
        {
            builder.AppendLine("- **高优先级：**先补齐上方列出的 CLIProxyAPI 主协议路由缺口。");
        }
        if (fieldDiffs.Any(diff => diff.MisalignedRows.Any(row => row.TypeMatchStatus == FieldTypeMatchStatus.Missing)))
        {
            builder.AppendLine("- **高优先级：**处理字段状态为“未检测到”的请求、响应和流式事件字段。");
        }
        if (fieldDiffs.Any(diff => diff.MisalignedRows.Any(row => row.TypeMatchStatus == FieldTypeMatchStatus.TypeMismatch)))
        {
            builder.AppendLine("- **高优先级：**核对类型线索不一致的字段，重点关注数组、对象、标量以及 JsonNode 动态转换。");
        }
        builder.AppendLine("- **流式重点：**核对 `type`、`delta`、`text`、`index`、`output_index`、工具调用参数和终止事件的顺序。");
        builder.AppendLine("- **非流式重点：**核对 message/content/output、tool calls、finish reason 和 usage 的最终结构。");
        builder.AppendLine("- **转换重点：**如果字段通过语义映射、透传或辅助方法处理，应检查对应代码位置，而不能只按同名字段判断。");
        builder.AppendLine();
    }

    private static List<ReferenceOnlyRoute> CollectReferenceOnlyRoutes(
        ProjectScanResult current,
        ProjectScanResult reference,
        ProtocolCatalog catalog)
    {
        var currentKeys = current.Routes
            .Where(route => !route.IsNotImplemented)
            .Select(route => route.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referenceKeys = reference.Routes
            .Where(route => !route.IsNotImplemented)
            .Select(route => route.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return catalog.SyncTargets
            .Where(target => referenceKeys.Contains(target.ComparisonKey) && !currentKeys.Contains(target.ComparisonKey))
            .Select(target => new ReferenceOnlyRoute(target))
            .OrderBy(item => item.Target.Protocol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Target.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Target.Method, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FormatFieldStatus(FieldTypeMatchStatus status) => status switch
    {
        FieldTypeMatchStatus.PassThrough => "已透传",
        FieldTypeMatchStatus.BridgeHandled => "已兼容中转",
        FieldTypeMatchStatus.SemanticHandled => "已语义映射",
        FieldTypeMatchStatus.DynamicHandled => "动态处理，无法确认",
        FieldTypeMatchStatus.Missing => "未检测到",
        FieldTypeMatchStatus.TypeMismatch => "类型线索不一致",
        _ => "已对齐"
    };

    private static string FormatOptional(bool optional) => optional ? "是" : "否";

    private static string EscapeMarkdown(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);

    private sealed record ReferenceOnlyRoute(RouteClassification Target);
}

internal static class GitPullHelper
{
    public static void PullCliProxyApi(string repositoryRoot)
    {
        var projectDir = Path.Combine(repositoryRoot, "reference-projects", "CLIProxyAPI");
        if (!Directory.Exists(Path.Combine(projectDir, ".git")))
        {
            Console.WriteLine("⚠️ 未找到 reference-projects/CLIProxyAPI，跳过拉取。");
            return;
        }

        Console.Write("正在拉取 CLIProxyAPI 最新代码...");
        var (success, output) = RunGitPull(projectDir);
        Console.WriteLine(success
            ? $" ✅ {ExtractPullSummary(output)}"
            : $" ⚠️ 拉取失败：{output.Split('\n').FirstOrDefault()}");
    }

    private static (bool Success, string Output) RunGitPull(string workingDirectory)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "pull --ff-only",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return (false, "无法启动 git 进程");
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0
                ? (true, string.IsNullOrEmpty(output) ? error : output)
                : (false, string.IsNullOrEmpty(error) ? output : error);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string ExtractPullSummary(string output)
    {
        var firstLine = output.Split('\n').FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(firstLine)
            || firstLine.Equals("Already up to date.", StringComparison.OrdinalIgnoreCase)
            ? "已是最新"
            : firstLine;
    }
}
