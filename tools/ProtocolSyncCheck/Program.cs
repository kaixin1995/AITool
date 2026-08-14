using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

using ProtocolSyncCheck;

var repositoryRoot = ResolveRepositoryRoot(args);
var outputPath = Path.Combine(repositoryRoot, "docs", "protocol-sync-report.md");
var skipPull = args.Any(arg => arg.Equals("--skip-pull", StringComparison.OrdinalIgnoreCase));

// 只更新 CLIProxyAPI 参考代码，避免扫描或拉取无关项目。
var pullResult = skipPull
    ? GitPullHelper.DescribeCurrentHead(repositoryRoot)
    : GitPullHelper.PullCliProxyApi(repositoryRoot);

var catalog = ProtocolCatalog.CreateDefault();
var projects = new[]
{
    ProjectScanDefinition.CurrentProject(repositoryRoot),
    ProjectScanDefinition.CliProxyApi(repositoryRoot)
};

var results = projects
    .Select(project => ProtocolScanner.Scan(project, catalog))
    .ToArray();

// 协议桥接已独立为 AITool.Protocol 项目（原 AITool.Web/Services/ProxyProtocol 目录已移除）。
// 递归扫描源码目录，但排除 bin/obj 中的自动生成代码。
var protocolSourceDir = Path.Combine(repositoryRoot, "src", "AITool.Protocol");
var currentProjectFiles = Directory
    .EnumerateFiles(protocolSourceDir, "*.cs", SearchOption.AllDirectories)
    .Where(file => !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    .Concat(Directory.GetFiles(Path.Combine(repositoryRoot, "src", "AITool.Web", "Controllers", "Proxy"), "*.cs"))
    .Append(Path.Combine(repositoryRoot, "src", "AITool.Web", "Controllers", "Admin", "ChatApiController.cs"))
    .ToArray();

var currentFields = CSharpFieldScanner.ScanFiles(currentProjectFiles);
var cpaFieldGroups = CpaFieldGroupBuilder.BuildGroups(repositoryRoot);
var cpaFieldDiffs = FieldDiffEngine.ComputeDiffs(cpaFieldGroups, currentFields);

var report = ProtocolReportBuilder.Build(results, catalog, cpaFieldDiffs, pullResult);
File.WriteAllText(outputPath, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

Console.WriteLine("协议同步报告已生成：" + Path.GetRelativePath(repositoryRoot, outputPath));
Console.WriteLine("CLIProxyAPI 基准版本：" + pullResult.CommitHash + "（" + pullResult.CommitDate + "）");
if (!pullResult.Success)
{
    Console.WriteLine("⚠️ 拉取未成功：" + pullResult.Message);
}

foreach (var result in results)
{
    Console.WriteLine(result.ProjectName + ": " + result.Routes.Count + " routes"
        + (result.UnclassifiedRoutes.Count > 0 ? "（另有 " + result.UnclassifiedRoutes.Count + " 条未跟踪路由）" : string.Empty));
}

Console.WriteLine("字段级对比：CLIProxyAPI " + cpaFieldDiffs.Count + " 个分组；AITool " + currentFields.Count + " 个字段");

static string ResolveRepositoryRoot(string[] args)
{
    if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) && !args[0].StartsWith("--", StringComparison.Ordinal))
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

/// <summary>
/// 扫描结果中的一条原始路由（无论是否被协议目录识别）。
/// </summary>
internal sealed record RawRoute(string Method, string Path, string SourcePath, int LineNumber);

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
        var unclassified = new List<RawRoute>();
        var missingFiles = new List<string>();

        foreach (var file in project.Files)
        {
            if (!File.Exists(file.FullPath))
            {
                missingFiles.Add(file.RelativePath);
                continue;
            }

            switch (file.Kind)
            {
                case RouteSourceKind.CSharpController:
                    ScanCSharpController(file, catalog, routes, unclassified);
                    break;
                case RouteSourceKind.GinRouter:
                    ScanGinRouter(file, catalog, routes, unclassified);
                    break;
            }
        }

        var distinctRoutes = routes
            .GroupBy(route => route.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(route => route.SourcePath, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(route => route.Protocol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(route => route.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(route => route.Method, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var distinctUnclassified = unclassified
            .GroupBy(route => route.Method + " " + route.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(route => route.SourcePath, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(route => route.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(route => route.Method, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProjectScanResult(project.Name, distinctRoutes, distinctUnclassified, missingFiles);
    }

    private static void ScanCSharpController(
        RouteSourceFile file,
        ProtocolCatalog catalog,
        List<ProtocolRoute> routes,
        List<RawRoute> unclassified)
    {
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
                unclassified,
                catalog,
                match.Groups["method"].Value.ToUpperInvariant(),
                NormalizeRoutePath(match.Groups["path"].Value),
                file.RelativePath,
                index + 1);
        }
    }

    private static void ScanGinRouter(
        RouteSourceFile file,
        ProtocolCatalog catalog,
        List<ProtocolRoute> routes,
        List<RawRoute> unclassified)
    {
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
                unclassified,
                catalog,
                routeMatch.Groups["method"].Value.ToUpperInvariant(),
                path,
                file.RelativePath,
                index + 1);
        }
    }

    private static void AddClassifiedRoutes(
        List<ProtocolRoute> routes,
        List<RawRoute> unclassified,
        ProtocolCatalog catalog,
        string method,
        string path,
        string sourcePath,
        int lineNumber)
    {
        if (!catalog.TryClassifyAll(method, path, out var classifications))
        {
            unclassified.Add(new RawRoute(method, path, sourcePath, lineNumber));
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
                classification.IsKnownStub,
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
    IReadOnlyList<RawRoute> UnclassifiedRoutes,
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
    public string Key => Protocol + ":" + Method + " " + Path;
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

    /// <summary>
    /// 需要与参考项目对齐的接口（主协议 + legacy）。
    /// </summary>
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
    public string ComparisonKey => Protocol + ":" + Method.ToUpperInvariant() + " " + Path;

    public static string BuildLookupKey(string method, string path, string protocol = "") =>
        string.IsNullOrWhiteSpace(protocol)
            ? method.ToUpperInvariant() + " " + path
            : protocol + ":" + method.ToUpperInvariant() + " " + path;

    public static RouteClassification Primary(string protocol, string method, string path, string description, bool matchPath = true) =>
        new(protocol, method, path, "主协议", description, MatchPath: matchPath);

    public static RouteClassification Legacy(string protocol, string method, string path, string description) =>
        new(protocol, method, path, "legacy", description);

    public static RouteClassification Extension(string protocol, string method, string path, string description) =>
        new(protocol, method, path, "扩展", description);
}

/// <summary>
/// 单个接口的对齐状态。
/// </summary>
internal enum RouteSyncStatus
{
    FullyAligned,      // 路由双方都有，且关联字段基线全部对齐
    IncompleteFields,  // 路由双方都有，但部分字段未在 AITool 中检测到或类型不一致
    NotImplemented,    // CLIProxyAPI 有、AITool 没有
    AIToolOnly,        // AITool 有、CLIProxyAPI 没有（已移除或未提供）
    RouteOnlyAligned,  // 路由双方都有，但没有字段基线分组（仅路由级一致）
    NeitherImplemented // 双方都没有实现（目录内但参考项目未提供）
}

internal sealed class RouteSyncEntry
{
    public required RouteClassification Target { get; init; }
    public required RouteSyncStatus Status { get; init; }
    public int MisalignedFieldCount { get; init; }
    public int TotalFieldCount { get; init; }
    public List<string> MissingFields { get; } = [];
    public required bool CpaHasRoute { get; init; }
    public required bool AitoolHasRoute { get; init; }
}

internal static class ProtocolReportBuilder
{
    public static string Build(
        IReadOnlyList<ProjectScanResult> results,
        ProtocolCatalog catalog,
        List<FieldDiffResult> cpaFieldDiffs,
        GitPullResult pullResult)
    {
        var current = results.First(result => result.ProjectName == "AITool");
        var cpa = results.First(result => result.ProjectName == "CLIProxyAPI");
        var builder = new StringBuilder();

        builder.AppendLine("# AITool 与 CLIProxyAPI 协议同步检查报告");
        builder.AppendLine();
        AppendRunMetadata(builder, pullResult);
        AppendScanPrerequisites(builder, current, cpa);
        AppendOverview(builder, current, cpa, catalog, cpaFieldDiffs);
        AppendRouteStatusTable(builder, current, cpa, catalog, cpaFieldDiffs);
        AppendUntrackedRoutes(builder, current, cpa);
        AppendFieldAlignmentReport(builder, cpaFieldDiffs);
        AppendDiagnosticConclusion(builder, current, cpa, cpaFieldDiffs);
        return builder.ToString();
    }

    private static void AppendRunMetadata(StringBuilder builder, GitPullResult pullResult)
    {
        builder.AppendLine("## 本次运行信息");
        builder.AppendLine();
        builder.AppendLine("- 生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine("- CLIProxyAPI 基准版本：`" + pullResult.CommitHash + "`（" + pullResult.CommitDate + "）");
        builder.AppendLine(pullResult.Success
            ? "- 拉取结果：✅ " + EscapeMarkdown(pullResult.Message)
            : "- 拉取结果：⚠️ 未更新（" + EscapeMarkdown(pullResult.Message) + "）");
        builder.AppendLine();
    }

    private static void AppendScanPrerequisites(StringBuilder builder, ProjectScanResult current, ProjectScanResult cpa)
    {
        var missing = current.MissingFiles
            .Select(file => "AITool：`" + file + "`")
            .Concat(cpa.MissingFiles.Select(file => "CLIProxyAPI：`" + file + "`"))
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
            builder.AppendLine("- 未找到 " + item);
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
        var entries = BuildRouteEntries(current, cpa, catalog, cpaFieldDiffs);
        var fullyAligned = entries.Count(e => e.Status == RouteSyncStatus.FullyAligned);
        var incomplete = entries.Count(e => e.Status == RouteSyncStatus.IncompleteFields);
        var notImplemented = entries.Count(e => e.Status == RouteSyncStatus.NotImplemented);
        var aitoolOnly = entries.Count(e => e.Status == RouteSyncStatus.AIToolOnly);
        var routeOnly = entries.Count(e => e.Status == RouteSyncStatus.RouteOnlyAligned);
        var neither = entries.Count(e => e.Status == RouteSyncStatus.NeitherImplemented);

        builder.AppendLine("## 总览");
        builder.AppendLine();
        builder.AppendLine("| 状态 | 数量 | 说明 |");
        builder.AppendLine("| --- | --- | --- |");
        builder.AppendLine("| ✅ 完全一致 | **" + fullyAligned + "** | 路由与字段均与 CLIProxyAPI 对齐 |");
        builder.AppendLine("| ⚠️ 已实现但字段不全 | **" + incomplete + "** | 路由已实现，但部分字段未检测到或类型不一致 |");
        builder.AppendLine("| ❌ 未实现 | **" + notImplemented + "** | CLIProxyAPI 已支持，AITool 缺少路由 |");
        builder.AppendLine("| ➕ AITool 独有 | **" + aitoolOnly + "** | AITool 有路由，CLIProxyAPI 未提供（可能已移除） |");
        builder.AppendLine("| 🔵 路由一致（无字段基线） | **" + routeOnly + "** | 双方都有路由，但参考代码未提供字段基线 |");
        builder.AppendLine("| ⚪ 双方均未实现 | **" + neither + "** | 协议目录内，但 CLIProxyAPI 与 AITool 均未提供 |");
        builder.AppendLine();
    }

    private static void AppendRouteStatusTable(
        StringBuilder builder,
        ProjectScanResult current,
        ProjectScanResult cpa,
        ProtocolCatalog catalog,
        List<FieldDiffResult> cpaFieldDiffs)
    {
        var entries = BuildRouteEntries(current, cpa, catalog, cpaFieldDiffs);

        builder.AppendLine("## 协议接口状态");
        builder.AppendLine();
        builder.AppendLine("| 状态 | 协议 | Method | URL | 字段 | 说明 |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var entry in entries)
        {
            var statusText = entry.Status switch
            {
                RouteSyncStatus.FullyAligned => "✅ 完全一致",
                RouteSyncStatus.IncompleteFields => "⚠️ 已实现但字段不全",
                RouteSyncStatus.NotImplemented => "❌ 未实现",
                RouteSyncStatus.AIToolOnly => "➕ AITool 独有",
                RouteSyncStatus.NeitherImplemented => "⚪ 双方均未实现",
                _ => "🔵 路由一致"
            };

            var fieldText = entry.Status switch
            {
                RouteSyncStatus.IncompleteFields => (entry.TotalFieldCount - entry.MisalignedFieldCount) + "/" + entry.TotalFieldCount + " 对齐，" + entry.MisalignedFieldCount + " 个待处理",
                RouteSyncStatus.FullyAligned when entry.TotalFieldCount > 0 => entry.TotalFieldCount + "/" + entry.TotalFieldCount,
                RouteSyncStatus.FullyAligned => "—",
                RouteSyncStatus.NotImplemented => "—",
                RouteSyncStatus.AIToolOnly => "—",
                RouteSyncStatus.NeitherImplemented => "—",
                _ => "无字段基线"
            };

            builder.AppendLine("| " + statusText + " | " + EscapeMarkdown(entry.Target.Protocol) + " | " + entry.Target.Method + " | `" + entry.Target.Path + "` | " + fieldText + " | " + EscapeMarkdown(entry.Target.Description) + " |");
        }
        builder.AppendLine();
    }

    /// <summary>
    /// 构建每个协议接口的路由 + 字段对齐状态。
    /// </summary>
    private static List<RouteSyncEntry> BuildRouteEntries(
        ProjectScanResult current,
        ProjectScanResult cpa,
        ProtocolCatalog catalog,
        List<FieldDiffResult> cpaFieldDiffs)
    {
        var currentKeys = current.Routes
            .Where(route => !route.IsNotImplemented)
            .Select(route => route.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cpaKeys = cpa.Routes
            .Where(route => !route.IsNotImplemented)
            .Select(route => route.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = new List<RouteSyncEntry>();
        foreach (var target in catalog.SyncTargets)
        {
            var cpaHas = cpaKeys.Contains(target.ComparisonKey);
            var aitoolHas = currentKeys.Contains(target.ComparisonKey);

            var status = !cpaHas
                ? (aitoolHas ? RouteSyncStatus.AIToolOnly : RouteSyncStatus.NeitherImplemented)
                : !aitoolHas
                    ? RouteSyncStatus.NotImplemented
                    : RouteSyncStatus.RouteOnlyAligned;

            // 关联字段分组
            var boundGroups = cpaFieldDiffs
                .Where(diff => diff.Group.RouteKeys.Any(key => string.Equals(key, target.ComparisonKey, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var misaligned = boundGroups.SelectMany(diff => diff.MisalignedRows).ToList();
            var total = boundGroups.Sum(diff => diff.Rows.Count);

            if (status == RouteSyncStatus.RouteOnlyAligned && total > 0)
            {
                status = misaligned.Count > 0 ? RouteSyncStatus.IncompleteFields : RouteSyncStatus.FullyAligned;
            }

            var entry = new RouteSyncEntry
            {
                Target = target,
                Status = status,
                TotalFieldCount = total,
                MisalignedFieldCount = misaligned.Count,
                CpaHasRoute = cpaHas,
                AitoolHasRoute = aitoolHas
            };
            foreach (var row in misaligned.Where(row => row.TypeMatchStatus == FieldTypeMatchStatus.Missing))
            {
                entry.MissingFields.Add(row.FieldName);
            }

            entries.Add(entry);
        }

        return entries;
    }

    private static void AppendUntrackedRoutes(StringBuilder builder, ProjectScanResult current, ProjectScanResult cpa)
    {
        if (cpa.UnclassifiedRoutes.Count > 0)
        {
            builder.AppendLine("## CLIProxyAPI 新增但未跟踪的路由");
            builder.AppendLine();
            builder.AppendLine("> 这些路由不在协议目录中，可能是参考项目新增的协议、管理接口或非 OpenAI/Anthropic 协议。请人工确认是否需要纳入同步范围。");
            builder.AppendLine();
            builder.AppendLine("| Method | URL | 位置 |");
            builder.AppendLine("| --- | --- | --- |");
            foreach (var route in cpa.UnclassifiedRoutes)
            {
                builder.AppendLine("| " + route.Method + " | `" + route.Path + "` | `" + Path.GetFileName(route.SourcePath) + ":" + route.LineNumber + "` |");
            }
            builder.AppendLine();
        }

        if (current.UnclassifiedRoutes.Count > 0)
        {
            builder.AppendLine("## AITool 独有但未跟踪的路由");
            builder.AppendLine();
            builder.AppendLine("| Method | URL | 位置 |");
            builder.AppendLine("| --- | --- | --- |");
            foreach (var route in current.UnclassifiedRoutes)
            {
                builder.AppendLine("| " + route.Method + " | `" + route.Path + "` | `" + Path.GetFileName(route.SourcePath) + ":" + route.LineNumber + "` |");
            }
            builder.AppendLine();
        }
    }

    private static void AppendFieldAlignmentReport(StringBuilder builder, List<FieldDiffResult> fieldDiffs)
    {
        builder.AppendLine("## AITool 与 CLIProxyAPI 字段对比");
        builder.AppendLine();
        builder.AppendLine("> 字段基线来自 CLIProxyAPI 的请求/响应处理函数与协议转换函数；AITool 侧同时扫描协议桥接代码、代理控制器和 Responses 流式状态代码。字段出现但语义未必完全等价，需结合状态和来源位置判断。");
        builder.AppendLine();

        foreach (var diff in fieldDiffs.OrderBy(diff => diff.Group.Label, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine("### " + EscapeMarkdown(diff.Group.Label));
            builder.AppendLine();
            builder.AppendLine("- 对齐情况：" + diff.AlignedRows.Count + "/" + diff.Rows.Count);
            builder.AppendLine("- 需要关注：" + diff.MisalignedRows.Count);
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
                builder.AppendLine("| `" + row.FieldName + "` | `" + EscapeMarkdown(row.ReferenceType) + "` | " + FormatOptional(row.Optional) + " | " + FormatFieldStatus(row.TypeMatchStatus) + " | " + EscapeMarkdown(row.CurrentTypeHint) + " | " + EscapeMarkdown(locations) + " |");
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
            .Select(location => Path.GetFileName(location.FilePath) + ":" + location.LineNumber));
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
        if (cpa.UnclassifiedRoutes.Count > 0)
        {
            builder.AppendLine("- **待确认：**人工核对 CLIProxyAPI 未跟踪路由，判断是否为需要同步的新协议。");
        }
        builder.AppendLine("- **流式重点：**核对 `type`、`delta`、`text`、`index`、`output_index`、工具调用参数和终止事件的顺序。");
        builder.AppendLine("- **非流式重点：**核对 message/content/output、tool calls、finish reason 和 usage 的最终结构。");
        builder.AppendLine("- **转换重点：**如果字段通过语义映射、透传或辅助方法处理，应检查对应代码位置，而不能只按同名字段判断。");
        builder.AppendLine();
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
}

internal static class GitPullHelper
{
    // 如果 origin（gh-proxy 镜像）不可用，回退到官方仓库地址。
    private const string FallbackUrl = "https://github.com/router-for-me/CLIProxyAPI.git";

    public static GitPullResult DescribeCurrentHead(string repositoryRoot)
    {
        var projectDir = Path.Combine(repositoryRoot, "reference-projects", "CLIProxyAPI");
        return new GitPullResult(
            true,
            "已跳过拉取（--skip-pull）",
            GetHeadInfo(projectDir, out var commitDate) ?? "未知",
            commitDate ?? "未知");
    }

    public static GitPullResult PullCliProxyApi(string repositoryRoot)
    {
        var projectDir = Path.Combine(repositoryRoot, "reference-projects", "CLIProxyAPI");
        if (!Directory.Exists(Path.Combine(projectDir, ".git")))
        {
            return new GitPullResult(false, "未找到 reference-projects/CLIProxyAPI/.git，跳过拉取。", "未知", "未知");
        }

        Console.Write("正在拉取 CLIProxyAPI 最新代码...");
        var (success, output) = RunGitPull(projectDir);
        if (!success)
        {
            // 镜像源可能失效，回退到官方 GitHub 地址再试一次。
            Console.WriteLine(" ⚠️ origin 拉取失败：" + output.Split('\n').FirstOrDefault()?.Trim() + "，尝试官方仓库...");
            var (fallbackSuccess, fallbackOutput) = RunGitPull(projectDir, FallbackUrl);
            if (fallbackSuccess)
            {
                success = true;
                output = fallbackOutput;
            }
            else
            {
                Console.WriteLine(" ⚠️ 官方仓库拉取也失败：" + fallbackOutput.Split('\n').FirstOrDefault()?.Trim());
            }
        }

        if (success)
        {
            Console.WriteLine(" ✅ " + ExtractPullSummary(output));
        }

        var commitHash = GetHeadInfo(projectDir, out var commitDate) ?? "未知";
        Console.WriteLine("   当前基准：" + commitHash + "（" + (commitDate ?? "未知") + "）");
        return new GitPullResult(success, output.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty, commitHash, commitDate ?? "未知");
    }

    private static string? GetHeadInfo(string workingDirectory, out string? commitDate)
    {
        commitDate = null;
        var hash = RunGit(workingDirectory, "log -1 --format=%h");
        if (hash is null)
        {
            return null;
        }

        commitDate = RunGit(workingDirectory, "log -1 --format=%ci")?.Trim();
        return hash.Trim();
    }

    private static string? RunGit(string workingDirectory, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static (bool Success, string Output) RunGitPull(string workingDirectory, string? fallbackUrl = null)
    {
        try
        {
            var arguments = fallbackUrl is null
                ? "pull --ff-only"
                : "pull --ff-only \"" + fallbackUrl + "\" main";

            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
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

internal sealed record GitPullResult(bool Success, string Message, string CommitHash, string CommitDate);
