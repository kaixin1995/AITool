using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// 使用项目中定义的类型
using ProtocolSyncCheck;

var repositoryRoot = ResolveRepositoryRoot(args);
var outputPath = Path.Combine(repositoryRoot, "docs", "protocol-sync-report.md");

// 拉取参考项目最新代码
GitPullHelper.PullReferenceProjects(repositoryRoot);

// 路由级扫描
var catalog = ProtocolCatalog.CreateDefault();
var projects = new[]
{
    ProjectScanDefinition.CurrentProject(repositoryRoot),
    ProjectScanDefinition.NewApi(repositoryRoot),
    ProjectScanDefinition.CliProxyApi(repositoryRoot)
};

var results = projects
    .Select(project => ProtocolScanner.Scan(project, catalog))
    .ToArray();

// 字段级扫描
var goStructs = GoStructScanner.ScanDirectory(
    Path.Combine(repositoryRoot, "reference-projects", "new-api", "dto"));

var currentProjectFiles = Directory
    .GetFiles(Path.Combine(repositoryRoot, "src", "AITool.Web", "Services", "ProxyProtocol"), "*.cs")
    .Concat(Directory.GetFiles(Path.Combine(repositoryRoot, "src", "AITool.Web", "Controllers", "Proxy"), "*.cs"))
    .ToArray();

var currentFields = CSharpFieldScanner.ScanFiles(currentProjectFiles);
var newApiStructGroups = NewApiFieldGroupBuilder.BuildGroups(repositoryRoot, goStructs);
var newApiFieldDiffs = FieldDiffEngine.ComputeDiffs(newApiStructGroups, currentFields);
var cpaFieldGroups = CpaFieldGroupBuilder.BuildGroups(repositoryRoot);
var cpaFieldDiffs = FieldDiffEngine.ComputeDiffs(cpaFieldGroups, currentFields);

// 生成报告
var report = ProtocolReportBuilder.Build(results, catalog, newApiFieldDiffs, cpaFieldDiffs);
File.WriteAllText(outputPath, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

Console.WriteLine($"协议同步报告已生成：{Path.GetRelativePath(repositoryRoot, outputPath)}");
foreach (var result in results)
{
    Console.WriteLine($"{result.ProjectName}: {result.Routes.Count} routes");
}
Console.WriteLine($"字段级对比：new-api {goStructs.Count} 个 Go struct、{newApiFieldDiffs.Count} 个分组；CPA {cpaFieldDiffs.Count} 个分组；当前项目 {currentFields.Count} 个字段");

/// <summary>
/// 解析当前仓库根目录，默认从工具运行目录向上查找项目标记文件。
/// </summary>
static string ResolveRepositoryRoot(string[] args)
{
    if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
    {
        return Path.GetFullPath(args[0]);
    }

    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "protocol-url-reference.md"))
            || Directory.Exists(Path.Combine(current.FullName, "src", "AITool.Web")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    return Directory.GetCurrentDirectory();
}

/// <summary>
/// 描述一个项目中需要扫描的协议路由文件和路由写法。
/// </summary>
internal sealed class ProjectScanDefinition
{
    /// <summary>
    /// 当前项目在报告中的显示名称。
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// 需要参与扫描的源码文件列表。
    /// </summary>
    public required IReadOnlyList<RouteSourceFile> Files { get; init; }

    /// <summary>
    /// 创建当前 AITool 项目的扫描定义。
    /// </summary>
    public static ProjectScanDefinition CurrentProject(string root)
    {
        return new ProjectScanDefinition
        {
            Name = "当前项目 AITool",
            Files = new[]
            {
                RouteSourceFile.CSharpController(root, "src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs"),
                RouteSourceFile.CSharpController(root, "src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs"),
                RouteSourceFile.CSharpController(root, "src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs")
            }
        };
    }

    /// <summary>
    /// 创建 new-api 参考项目的扫描定义。
    /// </summary>
    public static ProjectScanDefinition NewApi(string root)
    {
        return new ProjectScanDefinition
        {
            Name = "new-api",
            Files = new[]
            {
                RouteSourceFile.GinRouter(root, "reference-projects/new-api/router/relay-router.go"),
                RouteSourceFile.GinRouter(root, "reference-projects/new-api/router/video-router.go")
            }
        };
    }

    /// <summary>
    /// 创建 CLIProxyAPI 参考项目的扫描定义。
    /// </summary>
    public static ProjectScanDefinition CliProxyApi(string root)
    {
        return new ProjectScanDefinition
        {
            Name = "CPA / CLIProxyAPI",
            Files = new[]
            {
                RouteSourceFile.GinRouter(root, "reference-projects/CLIProxyAPI/internal/api/server.go")
            }
        };
    }
}

/// <summary>
/// 描述单个源码文件的相对路径和路由提取模式。
/// </summary>
internal sealed class RouteSourceFile
{
    /// <summary>
    /// 仓库根目录到源码文件的相对路径。
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>
    /// 源码文件绝对路径。
    /// </summary>
    public required string FullPath { get; init; }

    /// <summary>
    /// 路由提取模式。
    /// </summary>
    public required RouteSourceKind Kind { get; init; }

    /// <summary>
    /// 创建 ASP.NET Core Controller 文件的扫描配置。
    /// </summary>
    public static RouteSourceFile CSharpController(string root, string relativePath)
    {
        return Create(root, relativePath, RouteSourceKind.CSharpController);
    }

    /// <summary>
    /// 创建 Gin Router 文件的扫描配置。
    /// </summary>
    public static RouteSourceFile GinRouter(string root, string relativePath)
    {
        return Create(root, relativePath, RouteSourceKind.GinRouter);
    }

    /// <summary>
    /// 根据相对路径和扫描模式创建源码文件描述。
    /// </summary>
    private static RouteSourceFile Create(string root, string relativePath, RouteSourceKind kind)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return new RouteSourceFile
        {
            RelativePath = relativePath,
            FullPath = Path.Combine(root, normalized),
            Kind = kind
        };
    }
}

/// <summary>
/// 表示支持的源码路由写法类型。
/// </summary>
internal enum RouteSourceKind
{
    /// <summary>
    /// ASP.NET Core 控制器特性路由。
    /// </summary>
    CSharpController,

    /// <summary>
    /// Gin Router 方法调用路由。
    /// </summary>
    GinRouter
}

/// <summary>
/// 从源码文件提取协议路由。
/// </summary>
internal static class ProtocolScanner
{
    private static readonly Regex CSharpRouteRegex = new(@"\[Http(?<method>Get|Post|Delete|Put|Patch)\(""(?<path>[^""#]+)""\)\]", RegexOptions.Compiled);
    private static readonly Regex GinRouteRegex = new(@"(?<receiver>\w+)\.(?<method>GET|POST|DELETE|PUT|PATCH)\(""(?<path>[^""#]+)""", RegexOptions.Compiled);
    private static readonly Regex GinGroupRegex = new(@"(?<name>\w+)\s*:=\s*(?:(?<parent>[\w.]+)\.)?Group\(""(?<prefix>[^""]*)""\)", RegexOptions.Compiled);

    /// <summary>
    /// 扫描一个项目定义中的全部源码文件。
    /// </summary>
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

            var fileRoutes = file.Kind switch
            {
                RouteSourceKind.CSharpController => ScanCSharpController(file, catalog),
                RouteSourceKind.GinRouter => ScanGinRouter(file, catalog),
                _ => []
            };
            routes.AddRange(fileRoutes);
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

    /// <summary>
    /// 扫描 ASP.NET Core 控制器特性路由。
    /// </summary>
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

            var method = match.Groups["method"].Value.ToUpperInvariant();
            var rawPath = NormalizeRoutePath(match.Groups["path"].Value);
            if (catalog.TryClassify(method, rawPath, out var primaryClassification))
            {
                AddRoute(routes, method, rawPath, file.RelativePath, index + 1, primaryClassification, false);
            }

            if (catalog.TryClassifyAll(method, rawPath, out var classifications))
            {
                foreach (var classification in classifications.Where(item => !item.MatchPath))
                {
                    AddRoute(routes, method, rawPath, file.RelativePath, index + 1, classification, false);
                }
            }
        }

        return routes;
    }

    /// <summary>
    /// 扫描 Gin Router 路由，并尝试拼接同文件内的 Group 前缀。
    /// </summary>
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
            var method = routeMatch.Groups["method"].Value.ToUpperInvariant();
            var path = NormalizeRoutePath(routeMatch.Groups["path"].Value);
            if (groupPrefixes.TryGetValue(receiver, out var groupPrefix))
            {
                path = CombinePaths(groupPrefix, path);
            }

            var isNotImplemented = line.Contains("RelayNotImplemented", StringComparison.Ordinal);
            if (catalog.TryClassify(method, path, out var primaryClassification))
            {
                AddRoute(routes, method, path, file.RelativePath, index + 1, primaryClassification, isNotImplemented);
            }

            if (catalog.TryClassifyAll(method, path, out var classifications))
            {
                foreach (var classification in classifications.Where(item => !item.MatchPath))
                {
                    AddRoute(routes, method, path, file.RelativePath, index + 1, classification, isNotImplemented);
                }
            }
        }

        return routes;
    }

    /// <summary>
    /// 根据目录分类和忽略规则追加一条路由。
    /// </summary>
    private static void AddRoute(
        List<ProtocolRoute> routes,
        string method,
        string path,
        string sourcePath,
        int lineNumber,
        RouteClassification classification,
        bool isNotImplemented)
    {
        var normalizedPath = NormalizeRoutePath(path);
        routes.Add(new ProtocolRoute(
            method,
            normalizedPath,
            classification.Protocol,
            classification.Category,
            classification.Description,
            isNotImplemented || classification.IsKnownStub,
            sourcePath,
            lineNumber));
    }

    /// <summary>
    /// 规范化不同框架中的路径占位符写法。
    /// </summary>
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

    /// <summary>
    /// 拼接 Gin Group 前缀和相对路由路径。
    /// </summary>
    private static string CombinePaths(string prefix, string path)
    {
        if (prefix == "/")
        {
            return NormalizeRoutePath(path);
        }

        return NormalizeRoutePath(prefix.TrimEnd('/') + "/" + path.TrimStart('/'));
    }
}

/// <summary>
/// 保存一个项目的扫描结果。
/// </summary>
internal sealed record ProjectScanResult(
    string ProjectName,
    IReadOnlyList<ProtocolRoute> Routes,
    IReadOnlyList<string> MissingFiles);

/// <summary>
/// 表示扫描得到的一条协议路由。
/// </summary>
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
    /// <summary>
    /// 用于三方对比的规范化键。
    /// </summary>
    public string Key => $"{Method} {Path}";
}

/// <summary>
/// 保存已知协议路径分类规则。
/// </summary>
internal sealed class ProtocolCatalog
{
    private readonly Dictionary<string, RouteClassification> _knownRoutes;

    private ProtocolCatalog(IEnumerable<RouteClassification> routes)
    {
        _knownRoutes = routes.ToDictionary(route => route.Key, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 创建当前项目关心的 OpenAI 与 Anthropic 协议目录。
    /// </summary>
    public static ProtocolCatalog CreateDefault()
    {
        return new ProtocolCatalog(new[]
        {
            RouteClassification.Primary("OpenAI", "GET", "/v1/models", "模型列表"),
            RouteClassification.Primary("OpenAI", "GET", "/v1/models/:model", "单模型查询"),
            RouteClassification.Primary("OpenAI", "POST", "/v1/chat/completions", "Chat Completions"),
            RouteClassification.Legacy("OpenAI", "POST", "/v1/completions", "legacy Completions"),
            RouteClassification.Primary("OpenAI", "POST", "/v1/responses", "Responses API"),
            RouteClassification.Extension("OpenAI", "POST", "/v1/responses/compact", "Responses compact 扩展"),
            RouteClassification.Extension("OpenAI", "GET", "/v1/responses", "Responses WebSocket 扩展"),
            RouteClassification.Primary("OpenAI", "POST", "/v1/embeddings", "Embeddings"),
            RouteClassification.Primary("OpenAI", "POST", "/v1/images/generations", "图像生成"),
            RouteClassification.Primary("OpenAI", "POST", "/v1/images/edits", "图像编辑"),
            RouteClassification.Legacy("OpenAI", "POST", "/v1/edits", "旧式 edits"),
            RouteClassification.Primary("OpenAI", "POST", "/v1/audio/transcriptions", "音频转录"),
            RouteClassification.Primary("OpenAI", "POST", "/v1/audio/translations", "音频翻译"),
            RouteClassification.Primary("OpenAI", "POST", "/v1/audio/speech", "语音合成"),
            RouteClassification.Primary("OpenAI", "POST", "/v1/moderations", "Moderations"),
            RouteClassification.Primary("OpenAI", "POST", "/v1/videos", "视频创建"),
            RouteClassification.Primary("OpenAI", "GET", "/v1/videos/:id", "视频查询"),
            RouteClassification.Extension("OpenAI", "GET", "/v1/realtime", "Realtime WebSocket"),
            RouteClassification.Extension("OpenAI", "GET", "/v1/videos/:id/content", "视频内容代理"),
            RouteClassification.Extension("OpenAI", "POST", "/v1/video/generations", "视频生成扩展"),
            RouteClassification.Extension("OpenAI", "GET", "/v1/video/generations/:id", "视频任务查询扩展"),
            RouteClassification.Extension("OpenAI", "POST", "/v1/videos/:video_id/remix", "视频 remix 扩展"),
            RouteClassification.Extension("OpenAI", "POST", "/v1/videos/generations", "xAI 视频生成扩展"),
            RouteClassification.Extension("OpenAI", "POST", "/v1/videos/edits", "xAI 视频编辑扩展"),
            RouteClassification.Extension("OpenAI", "POST", "/v1/videos/extensions", "xAI 视频扩展"),
            RouteClassification.Extension("OpenAI", "POST", "/v1/rerank", "Rerank 扩展"),
            RouteClassification.Extension("OpenAI", "POST", "/v1/engines/:model/embeddings", "旧式 embeddings 兼容路径"),
            RouteClassification.Extension("OpenAI", "POST", "/v1/models/*path", "Gemini 兼容路径"),
            RouteClassification.Primary("Anthropic", "GET", "/v1/models", "Anthropic 模型列表", matchPath: false),
            RouteClassification.Primary("Anthropic", "GET", "/v1/models/:model", "Anthropic 单模型查询", matchPath: false),
            RouteClassification.Primary("Anthropic", "POST", "/v1/messages", "Anthropic Messages"),
            RouteClassification.Primary("Anthropic", "POST", "/v1/messages/count_tokens", "Anthropic Count Tokens"),
            RouteClassification.Stub("OpenAI", "POST", "/v1/images/variations", "new-api 501 图像 variations"),
            RouteClassification.Stub("OpenAI", "GET", "/v1/files", "new-api 501 files"),
            RouteClassification.Stub("OpenAI", "POST", "/v1/files", "new-api 501 files"),
            RouteClassification.Stub("OpenAI", "DELETE", "/v1/files/:id", "new-api 501 files"),
            RouteClassification.Stub("OpenAI", "GET", "/v1/files/:id", "new-api 501 files"),
            RouteClassification.Stub("OpenAI", "GET", "/v1/files/:id/content", "new-api 501 files"),
            RouteClassification.Stub("OpenAI", "POST", "/v1/fine-tunes", "new-api 501 fine-tunes"),
            RouteClassification.Stub("OpenAI", "GET", "/v1/fine-tunes", "new-api 501 fine-tunes"),
            RouteClassification.Stub("OpenAI", "GET", "/v1/fine-tunes/:id", "new-api 501 fine-tunes"),
            RouteClassification.Stub("OpenAI", "POST", "/v1/fine-tunes/:id/cancel", "new-api 501 fine-tunes"),
            RouteClassification.Stub("OpenAI", "GET", "/v1/fine-tunes/:id/events", "new-api 501 fine-tunes"),
            RouteClassification.Stub("OpenAI", "DELETE", "/v1/models/:model", "new-api 501 model delete")
        });
    }

    /// <summary>
    /// 尝试根据方法和路径识别协议分类。
    /// </summary>
    public bool TryClassify(string method, string path, out RouteClassification classification)
    {
        return _knownRoutes.TryGetValue(RouteClassification.BuildKey(method, path), out classification!)
            || _knownRoutes.TryGetValue(RouteClassification.BuildKey(method, path, "Anthropic"), out classification!);
    }

    /// <summary>
    /// 返回路径匹配到的所有协议分类，主要用于 /v1/models 这类双协议复用路径。
    /// </summary>
    public bool TryClassifyAll(string method, string path, out IReadOnlyList<RouteClassification> classifications)
    {
        var matches = new List<RouteClassification>();
        if (_knownRoutes.TryGetValue(RouteClassification.BuildKey(method, path), out var directMatch))
        {
            matches.Add(directMatch);
        }

        if (_knownRoutes.TryGetValue(RouteClassification.BuildKey(method, path, "Anthropic"), out var protocolMatch))
        {
            matches.Add(protocolMatch);
        }

        classifications = matches;
        return matches.Count > 0;
    }

    /// <summary>
    /// 返回目录中所有主协议和 legacy 协议路由，用于生成缺口报告。
    /// </summary>
    public IReadOnlyList<RouteClassification> SyncTargets => _knownRoutes.Values
        .Where(route => route.Category is "主协议" or "legacy")
        .OrderBy(route => route.Protocol, StringComparer.OrdinalIgnoreCase)
        .ThenBy(route => route.Path, StringComparer.OrdinalIgnoreCase)
        .ThenBy(route => route.Method, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

/// <summary>
/// 表示一个已知路由的协议分类。
/// </summary>
internal sealed record RouteClassification(
    string Protocol,
    string Method,
    string Path,
    string Category,
    string Description,
    bool IsKnownStub = false,
    bool MatchPath = true)
{
    /// <summary>
    /// 用于字典查找的规范化键。
    /// </summary>
    public string Key => BuildKey(Method, Path, MatchPath ? string.Empty : Protocol);

    /// <summary>
    /// 构造规范化路由键。
    /// </summary>
    public static string BuildKey(string method, string path, string protocol = "") =>
        string.IsNullOrWhiteSpace(protocol)
            ? $"{method.ToUpperInvariant()} {path}"
            : $"{protocol}:{method.ToUpperInvariant()} {path}";

    /// <summary>
    /// 创建主协议路由分类。
    /// </summary>
    public static RouteClassification Primary(string protocol, string method, string path, string description, bool matchPath = true) =>
        new(protocol, method, path, "主协议", description, MatchPath: matchPath);

    /// <summary>
    /// 创建 legacy 协议路由分类。
    /// </summary>
    public static RouteClassification Legacy(string protocol, string method, string path, string description) =>
        new(protocol, method, path, "legacy", description);

    /// <summary>
    /// 创建协议扩展路由分类。
    /// </summary>
    public static RouteClassification Extension(string protocol, string method, string path, string description) =>
        new(protocol, method, path, "扩展", description);

    /// <summary>
    /// 创建已知未实现路由分类。
    /// </summary>
    public static RouteClassification Stub(string protocol, string method, string path, string description) =>
        new(protocol, method, path, "501/stub", description, IsKnownStub: true);
}

/// <summary>
/// 根据扫描结果生成 Markdown 协议同步报告。
/// </summary>
internal static class ProtocolReportBuilder
{
    /// <summary>
    /// 根据三方扫描结果和字段级对比生成完整 Markdown 报告。
    /// 报告主体只保留两类信息：参考项目已支持但当前项目未实现的接口，以及已实现接口的字段对齐情况。
    /// </summary>
    public static string Build(
        IReadOnlyList<ProjectScanResult> results,
        ProtocolCatalog catalog,
        List<FieldDiffResult> newApiFieldDiffs,
        List<FieldDiffResult> cpaFieldDiffs)
    {
        var current = results.First(result => result.ProjectName == "当前项目 AITool");
        var references = results.Where(result => result.ProjectName != current.ProjectName).ToArray();
        var newApi = references.First(result => result.ProjectName == "new-api");
        var cpa = references.First(result => result.ProjectName == "CPA / CLIProxyAPI");
        var builder = new StringBuilder();

        builder.AppendLine("# 协议同步检查报告");
        builder.AppendLine();
        AppendOverview(builder, current, newApi, cpa, catalog, newApiFieldDiffs, cpaFieldDiffs);
        AppendReferenceRouteComparison(builder, current, newApi, catalog);
        AppendFieldAlignmentReport(builder, "new-api", newApiFieldDiffs);
        AppendReferenceRouteComparison(builder, current, cpa, catalog);
        AppendFieldAlignmentReport(builder, "CPA / CLIProxyAPI", cpaFieldDiffs);
        AppendReferenceFieldComparison(builder, newApiFieldDiffs, cpaFieldDiffs);
        return builder.ToString();
    }

    /// <summary>
    /// 输出报告顶部总览，只汇总用户关心的两件事。
    /// </summary>
    private static void AppendOverview(
        StringBuilder builder,
        ProjectScanResult current,
        ProjectScanResult newApi,
        ProjectScanResult cpa,
        ProtocolCatalog catalog,
        List<FieldDiffResult> newApiFieldDiffs,
        List<FieldDiffResult> cpaFieldDiffs)
    {
        var newApiReferenceOnlyRoutes = CollectReferenceOnlyRoutes(current, newApi, catalog);
        var cpaReferenceOnlyRoutes = CollectReferenceOnlyRoutes(current, cpa, catalog);
        var newApiMismatchedGroups = newApiFieldDiffs.Where(diff => diff.HasMismatch).ToList();
        var newApiAlignedGroups = newApiFieldDiffs.Where(diff => !diff.HasMismatch).ToList();
        var cpaMismatchedGroups = cpaFieldDiffs.Where(diff => diff.HasMismatch).ToList();
        var cpaAlignedGroups = cpaFieldDiffs.Where(diff => !diff.HasMismatch).ToList();

        builder.AppendLine("## 总览");
        builder.AppendLine();
        builder.AppendLine($"- new-api 已支持但本项目未实现的接口：**{newApiReferenceOnlyRoutes.Count}** 个");
        builder.AppendLine($"- CPA / CLIProxyAPI 已支持但本项目未实现的接口：**{cpaReferenceOnlyRoutes.Count}** 个");
        builder.AppendLine($"- 基于 new-api 的未对齐分组：**{newApiMismatchedGroups.Count}** 个，完全对齐分组：**{newApiAlignedGroups.Count}** 个");
        builder.AppendLine($"- 基于 CPA / CLIProxyAPI 的未对齐分组：**{cpaMismatchedGroups.Count}** 个，完全对齐分组：**{cpaAlignedGroups.Count}** 个");
        builder.AppendLine();
    }

    /// <summary>
    /// 按单个参考项目展示该项目已支持但当前项目未实现的接口。
    /// </summary>
    private static void AppendReferenceRouteComparison(
        StringBuilder builder,
        ProjectScanResult current,
        ProjectScanResult reference,
        ProtocolCatalog catalog)
    {
        var routes = CollectReferenceOnlyRoutes(current, reference, catalog);

        builder.AppendLine($"## 与 {EscapeMarkdown(reference.ProjectName)} 对比");
        builder.AppendLine();
        builder.AppendLine($"### {EscapeMarkdown(reference.ProjectName)} 已支持但本项目未实现的接口");
        builder.AppendLine();

        if (routes.Count == 0)
        {
            builder.AppendLine($"✅ 当前没有发现 {EscapeMarkdown(reference.ProjectName)} 已支持但本项目尚未实现的 OpenAI / Anthropic 接口。");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("| 协议 | 分类 | Method | URL | 说明 |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var item in routes)
        {
            builder.AppendLine($"| {EscapeMarkdown(item.Target.Protocol)} | {EscapeMarkdown(item.Target.Category)} | {item.Target.Method} | `{item.Target.Path}` | {EscapeMarkdown(item.Target.Description)} |");
        }

        builder.AppendLine();
    }

    /// <summary>
    /// 输出已实现接口的字段对齐情况：明确标注当前字段对比基于哪个参考项目。
    /// </summary>
    private static void AppendFieldAlignmentReport(StringBuilder builder, string referenceProjectName, List<FieldDiffResult> fieldDiffs)
    {
        var mismatchedGroups = fieldDiffs
            .Where(diff => diff.HasMismatch)
            .OrderByDescending(diff => diff.MisalignedRows.Count)
            .ThenBy(diff => diff.Group.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var alignedGroups = fieldDiffs
            .Where(diff => !diff.HasMismatch)
            .OrderBy(diff => diff.Group.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        builder.AppendLine($"### 基于 {EscapeMarkdown(referenceProjectName)} 的字段对齐情况");
        builder.AppendLine();
        builder.AppendLine($"> 当前字段明细对比来源：**{EscapeMarkdown(referenceProjectName)}**。表格仅展示**未对齐字段**。`当前类型线索` 来自当前项目对 JsonNode / JsonObject 的实际读写代码。");
        builder.AppendLine();

        builder.AppendLine("#### 完全对齐（简要）");
        builder.AppendLine();
        if (alignedGroups.Count == 0)
        {
            builder.AppendLine("- 无");
        }
        else
        {
            foreach (var diff in alignedGroups)
            {
                builder.AppendLine($"- **{EscapeMarkdown(diff.Group.Label)}**：{diff.Rows.Count}/{diff.Rows.Count} 字段已对齐");
            }
        }

        builder.AppendLine();
        builder.AppendLine("#### 未对齐接口");
        builder.AppendLine();

        if (mismatchedGroups.Count == 0)
        {
            builder.AppendLine($"✅ 当前基于 {EscapeMarkdown(referenceProjectName)} 的字段扫描中，已实现接口字段均已对齐。");
            builder.AppendLine();
            return;
        }

        foreach (var diff in mismatchedGroups)
        {
            builder.AppendLine($"##### {EscapeMarkdown(diff.Group.Label)}");
            builder.AppendLine();
            builder.AppendLine($"> {EscapeMarkdown(diff.Group.Description)}");
            builder.AppendLine();
            builder.AppendLine($"- 对齐情况：{diff.AlignedRows.Count}/{diff.Rows.Count}");
            builder.AppendLine($"- 未对齐字段：{diff.MisalignedRows.Count}");
            builder.AppendLine();
            builder.AppendLine("| 字段 | 参考类型 | 可选 | 当前状态 | 当前类型线索 |");
            builder.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var row in diff.MisalignedRows)
            {
                builder.AppendLine($"| `{row.FieldName}` | `{EscapeMarkdown(row.ReferenceType)}` | {FormatOptional(row.Optional)} | {FormatFieldStatus(row.TypeMatchStatus)} | {EscapeMarkdown(row.CurrentTypeHint)} |");
            }
            builder.AppendLine();
        }
    }

    /// <summary>
    /// 追加 new-api 与 CPA / CLIProxyAPI 的字段基线互相比对。
    /// </summary>
    private static void AppendReferenceFieldComparison(
        StringBuilder builder,
        List<FieldDiffResult> newApiFieldDiffs,
        List<FieldDiffResult> cpaFieldDiffs)
    {
        var newApiIndex = newApiFieldDiffs.ToDictionary(diff => NormalizeReferenceGroupLabel(diff.Group.Label), StringComparer.OrdinalIgnoreCase);
        var cpaIndex = cpaFieldDiffs.ToDictionary(diff => NormalizeReferenceGroupLabel(diff.Group.Label), StringComparer.OrdinalIgnoreCase);
        var commonGroupKeys = newApiIndex.Keys
            .Intersect(cpaIndex.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        builder.AppendLine("## CPA / CLIProxyAPI 与 new-api 字段基线对比");
        builder.AppendLine();
        builder.AppendLine("> 对比基础：仅比较**当前项目已经实现的接口**中，new-api 与 CPA / CLIProxyAPI 都能建立字段基线的分组。这里比较的是两个参考项目之间的字段基线，不涉及当前项目字段是否已实现。");
        builder.AppendLine();

        if (commonGroupKeys.Count == 0)
        {
            builder.AppendLine("当前没有可同时在 new-api 与 CPA / CLIProxyAPI 中建立字段基线的共同接口分组。");
            builder.AppendLine();
            return;
        }

        foreach (var key in commonGroupKeys)
        {
            var newApiDiff = newApiIndex[key];
            var cpaDiff = cpaIndex[key];
            var rows = BuildReferenceComparisonRows(newApiDiff, cpaDiff);
            var alignedCount = rows.Count(row => row.Category == "两边都有");

            builder.AppendLine($"### {EscapeMarkdown(key)}");
            builder.AppendLine();
            builder.AppendLine($"- 字段基线对齐：{alignedCount}/{rows.Count}");
            builder.AppendLine($"- new-api 字段数：{newApiDiff.Rows.Count}");
            builder.AppendLine($"- CPA / CLIProxyAPI 字段数：{cpaDiff.Rows.Count}");
            builder.AppendLine();
            builder.AppendLine("| 字段 | 类别 | new-api 类型 | new-api 可选 | CPA 类型 | CPA 可选 |");
            builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var row in rows)
            {
                builder.AppendLine($"| `{row.FieldName}` | {row.Category} | {FormatReferenceType(row.NewApiType)} | {FormatNullableOptional(row.NewApiOptional)} | {FormatReferenceType(row.CpaType)} | {FormatNullableOptional(row.CpaOptional)} |");
            }
            builder.AppendLine();
        }
    }

    /// <summary>
    /// 构建两个参考项目字段基线的逐字段对比行。
    /// </summary>
    private static List<ReferenceFieldComparisonRow> BuildReferenceComparisonRows(FieldDiffResult newApiDiff, FieldDiffResult cpaDiff)
    {
        var newApiRows = newApiDiff.Rows.ToDictionary(row => row.FieldName, StringComparer.OrdinalIgnoreCase);
        var cpaRows = cpaDiff.Rows.ToDictionary(row => row.FieldName, StringComparer.OrdinalIgnoreCase);
        var allFields = newApiRows.Keys
            .Union(cpaRows.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(field => field, StringComparer.OrdinalIgnoreCase);

        var rows = new List<ReferenceFieldComparisonRow>();
        foreach (var field in allFields)
        {
            newApiRows.TryGetValue(field, out var newApiRow);
            cpaRows.TryGetValue(field, out var cpaRow);

            var category = (newApiRow, cpaRow) switch
            {
                ({ } left, { } right) when !string.Equals(left.ReferenceType, right.ReferenceType, StringComparison.OrdinalIgnoreCase) => "两边都有 / 类型不同",
                ({ }, { }) => "两边都有",
                ({ }, null) => "仅 new-api",
                (null, { }) => "仅 CPA",
                _ => "未知"
            };

            rows.Add(new ReferenceFieldComparisonRow(
                field,
                category,
                newApiRow?.ReferenceType,
                newApiRow?.Optional,
                cpaRow?.ReferenceType,
                cpaRow?.Optional));
        }

        return rows;
    }

    /// <summary>
    /// 规范化参考项目字段分组名，方便 new-api 与 CPA 对齐。
    /// </summary>
    private static string NormalizeReferenceGroupLabel(string label)
    {
        return label.Replace("（CPA）", string.Empty, StringComparison.Ordinal).Trim();
    }

    /// <summary>
    /// 格式化参考类型展示。
    /// </summary>
    private static string FormatReferenceType(string? type)
    {
        return string.IsNullOrWhiteSpace(type) ? "—" : $"`{EscapeMarkdown(type)}`";
    }

    /// <summary>
    /// 格式化可选状态展示。
    /// </summary>
    private static string FormatNullableOptional(bool? optional)
    {
        return optional is null ? "—" : FormatOptional(optional.Value);
    }

    /// <summary>
    /// 收集单个参考项目已支持但当前项目未实现的接口。
    /// </summary>
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
            .Where(target => referenceKeys.Contains(target.Key) && !currentKeys.Contains(target.Key))
            .Select(target => new ReferenceOnlyRoute(target))
            .OrderBy(item => item.Target.Protocol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Target.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Target.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Target.Method, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 将字段对齐状态转换为直观文案。
    /// </summary>
    private static string FormatFieldStatus(FieldTypeMatchStatus status)
    {
        return status switch
        {
            FieldTypeMatchStatus.Missing => "未检测到",
            FieldTypeMatchStatus.TypeMismatch => "类型线索不一致",
            _ => "已对齐"
        };
    }

    /// <summary>
    /// 格式化字段可选性。
    /// </summary>
    private static string FormatOptional(bool optional)
    {
        return optional ? "是" : "否";
    }

    /// <summary>
    /// 转义 Markdown 表格中容易破坏结构的字符。
    /// </summary>
    private static string EscapeMarkdown(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal);
    }

    /// <summary>
    /// 参考项目字段基线对比行。
    /// </summary>
    private sealed record ReferenceFieldComparisonRow(
        string FieldName,
        string Category,
        string? NewApiType,
        bool? NewApiOptional,
        string? CpaType,
        bool? CpaOptional);

    /// <summary>
    /// 参考项目已支持但当前项目未实现的接口项。
    /// </summary>
    private sealed record ReferenceOnlyRoute(RouteClassification Target);
}
/// <summary>
/// 拉取参考项目（new-api、CLIProxyAPI）的最新代码。
/// </summary>
internal static class GitPullHelper
{
    /// <summary>
    /// 拉取参考项目的最新代码，拉取失败时输出警告但不中断主流程。
    /// </summary>
    public static void PullReferenceProjects(string repositoryRoot)
    {
        var referenceDir = Path.Combine(repositoryRoot, "reference-projects");
        if (!Directory.Exists(referenceDir))
        {
            Console.WriteLine("⚠️ 未找到 reference-projects 目录，跳过拉取。");
            return;
        }

        foreach (var projectDir in Directory.EnumerateDirectories(referenceDir))
        {
            if (!Directory.Exists(Path.Combine(projectDir, ".git")))
            {
                continue;
            }

            var projectName = Path.GetFileName(projectDir);
            Console.Write($"正在拉取 {projectName} 最新代码...");
            var (success, output) = RunGitPull(projectDir);
            if (success)
            {
                Console.WriteLine($" ✅ {ExtractPullSummary(output)}");
            }
            else
            {
                Console.WriteLine($" ⚠️ 拉取失败：{output.Split('\n').FirstOrDefault()}");
            }
        }
    }

    /// <summary>
    /// 在指定目录执行 git pull，返回是否成功及命令输出。
    /// </summary>
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

    /// <summary>
    /// 从 git pull 输出中提取简要摘要（如文件变更数）。
    /// </summary>
    private static string ExtractPullSummary(string output)
    {
        var firstLine = output.Split('\n').FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(firstLine) || firstLine.Equals("Already up to date.", StringComparison.OrdinalIgnoreCase))
        {
            return "已是最新";
        }

        return firstLine;
    }
}
