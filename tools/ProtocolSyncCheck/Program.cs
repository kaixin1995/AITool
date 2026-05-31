using System.Text;
using System.Text.RegularExpressions;

var repositoryRoot = ResolveRepositoryRoot(args);
var outputPath = Path.Combine(repositoryRoot, "protocol-sync-report.md");

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

var report = ProtocolReportBuilder.Build(results, catalog);
File.WriteAllText(outputPath, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

Console.WriteLine($"协议同步报告已生成：{Path.GetRelativePath(repositoryRoot, outputPath)}");
foreach (var result in results)
{
    Console.WriteLine($"{result.ProjectName}: {result.Routes.Count} routes");
}

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
    /// 根据三方扫描结果生成完整 Markdown 报告。
    /// </summary>
    public static string Build(IReadOnlyList<ProjectScanResult> results, ProtocolCatalog catalog)
    {
        var current = results.First(result => result.ProjectName == "当前项目 AITool");
        var references = results.Where(result => result.ProjectName != current.ProjectName).ToArray();
        var builder = new StringBuilder();

        builder.AppendLine("# 协议同步检查报告");
        builder.AppendLine();
        builder.AppendLine("> 本报告由 `tools/ProtocolSyncCheck` 自动生成，用于检查当前项目已有接口在参考项目（new-api、CPA）中是否仍然被支持。");
        builder.AppendLine();
        builder.AppendLine("## 扫描摘要");
        builder.AppendLine();
        builder.AppendLine("| 项目 | 已识别路由数 | 缺失扫描文件 |");
        builder.AppendLine("| --- | ---: | --- |");
        foreach (var result in results)
        {
            var missing = result.MissingFiles.Count == 0
                ? "—"
                : string.Join("<br>", result.MissingFiles.Select(EscapeMarkdown));
            builder.AppendLine($"| {EscapeMarkdown(result.ProjectName)} | {result.Routes.Count} | {missing} |");
        }

        // 核心章节：当前项目已有路由在参考项目中的同步状态
        AppendCurrentRouteSyncStatus(builder, current, references);
        // 参考信息：参考项目有但当前项目暂未实现的接口
        AppendReferenceOnlyRoutes(builder, current, references, catalog);
        // 全量路由支持矩阵
        AppendRouteMatrix(builder, results);
        // 501/stub 路由
        AppendStubRoutes(builder, results);
        AppendIgnoredNote(builder);

        return builder.ToString();
    }

    /// <summary>
    /// 核心章节：逐条列出当前项目已有路由，并标注各参考项目是否仍支持。
    /// 如果某参考项目不再包含该路由，标记为 ⚠️ 告警。
    /// </summary>
    private static void AppendCurrentRouteSyncStatus(StringBuilder builder, ProjectScanResult current, IReadOnlyList<ProjectScanResult> references)
    {
        var currentRoutes = current.Routes.Where(route => !route.IsNotImplemented).ToArray();

        // 按参考项目构建已实现路由索引
        var referenceIndex = references.ToDictionary(
            r => r.ProjectName,
            r => r.Routes.Where(route => !route.IsNotImplemented)
                .Select(route => route.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        builder.AppendLine();
        builder.AppendLine("## 当前项目已有路由同步状态");
        builder.AppendLine();
        builder.AppendLine("> 以下为当前项目已实现的路由在各参考项目中的支持情况。⚠️ 表示该参考项目中未检测到此路由，可能意味着协议已变更或移除。");
        builder.AppendLine();

        if (currentRoutes.Length == 0)
        {
            builder.AppendLine("当前项目未扫描到已实现的路由。");
            return;
        }

        // 表头
        builder.Append("| 协议 | 分类 | Method | URL | 说明 | 代码位置 |");
        foreach (var reference in references)
        {
            builder.Append(' ').Append(EscapeMarkdown(reference.ProjectName)).Append(" |");
        }
        builder.AppendLine();

        builder.Append("| --- | --- | --- | --- | --- | --- |");
        foreach (var _ in references)
        {
            builder.Append(" --- |");
        }
        builder.AppendLine();

        var hasWarning = false;
        foreach (var route in currentRoutes)
        {
            builder.Append($"| {route.Protocol} | {route.Category} | {route.Method} | `{route.Path}` | {EscapeMarkdown(route.Description)} | {FormatSource(route)} |");
            foreach (var reference in references)
            {
                var supported = referenceIndex[reference.ProjectName].Contains(route.Key);
                if (!supported)
                {
                    hasWarning = true;
                    builder.Append(" ⚠️ 未检测到 |");
                }
                else
                {
                    builder.Append(" ✅ |");
                }
            }
            builder.AppendLine();
        }

        if (!hasWarning)
        {
            builder.AppendLine();
            builder.AppendLine("> ✅ 所有当前项目已有路由在各参考项目中均仍被检测到，无同步风险。");
        }
        else
        {
            builder.AppendLine();
            builder.AppendLine("> ⚠️ 存在参考项目未检测到的路由，建议核实参考项目是否移除或变更了该接口。");
        }
    }

    /// <summary>
    /// 参考信息：列出参考项目支持但当前项目暂未实现的路由，仅作参考提示。
    /// </summary>
    private static void AppendReferenceOnlyRoutes(StringBuilder builder, ProjectScanResult current, IReadOnlyList<ProjectScanResult> references, ProtocolCatalog catalog)
    {
        var currentKeys = current.Routes.Where(route => !route.IsNotImplemented).Select(route => route.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referenceRoutes = references
            .SelectMany(result => result.Routes.Where(route => !route.IsNotImplemented).Select(route => new { Project = result.ProjectName, Route = route }))
            .GroupBy(item => item.Route.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        // 只列出 catalog 中主协议/legacy 分类的路由（排除扩展和 stub）
        var refOnly = catalog.SyncTargets
            .Where(target => referenceRoutes.ContainsKey(target.Key) && !currentKeys.Contains(target.Key))
            .ToArray();

        builder.AppendLine();
        builder.AppendLine("## 参考信息：参考项目已支持但当前项目暂未实现的路由");
        builder.AppendLine();
        builder.AppendLine("> 以下路由仅作参考，不视为同步缺口。后续如需新增接口可优先考虑这些路由。");
        builder.AppendLine();

        if (refOnly.Length == 0)
        {
            builder.AppendLine("当前没有发现参考项目支持但当前项目未实现的主协议或 legacy 路由。");
            return;
        }

        builder.AppendLine("| 协议 | 分类 | Method | URL | 参考项目 | 说明 |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var target in refOnly)
        {
            var projects = string.Join("、", referenceRoutes[target.Key].Select(item => item.Project).Distinct(StringComparer.OrdinalIgnoreCase));
            builder.AppendLine($"| {target.Protocol} | {target.Category} | {target.Method} | `{target.Path}` | {EscapeMarkdown(projects)} | {EscapeMarkdown(target.Description)} |");
        }
    }

    /// <summary>
    /// 追加三方路由支持矩阵。
    /// </summary>
    private static void AppendRouteMatrix(StringBuilder builder, IReadOnlyList<ProjectScanResult> results)
    {
        var allRoutes = results
            .SelectMany(result => result.Routes.Where(route => !route.IsNotImplemented))
            .GroupBy(route => route.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(route => route.Protocol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(route => route.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(route => route.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(route => route.Method, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        builder.AppendLine();
        builder.AppendLine("## 已识别路由支持矩阵");
        builder.AppendLine();
        builder.Append("| 协议 | 分类 | Method | URL | 说明 |");
        foreach (var result in results)
        {
            builder.Append(' ').Append(EscapeMarkdown(result.ProjectName)).Append(" |");
        }
        builder.AppendLine();
        builder.Append("| --- | --- | --- | --- | --- |");
        foreach (var _ in results)
        {
            builder.Append(" --- |");
        }
        builder.AppendLine();

        foreach (var route in allRoutes)
        {
            builder.Append($"| {route.Protocol} | {route.Category} | {route.Method} | `{route.Path}` | {EscapeMarkdown(route.Description)} |");
            foreach (var result in results)
            {
                var matched = result.Routes.FirstOrDefault(item => string.Equals(item.Key, route.Key, StringComparison.OrdinalIgnoreCase) && !item.IsNotImplemented);
                builder.Append(' ').Append(matched is null ? "—" : $"✅ {FormatSource(matched)}").Append(" |");
            }
            builder.AppendLine();
        }
    }

    /// <summary>
    /// 追加已识别但属于 501 或 stub 的路由，避免误当作同步缺口。
    /// </summary>
    private static void AppendStubRoutes(StringBuilder builder, IReadOnlyList<ProjectScanResult> results)
    {
        var stubs = results
            .SelectMany(result => result.Routes.Where(route => route.IsNotImplemented).Select(route => new { Project = result.ProjectName, Route = route }))
            .OrderBy(item => item.Route.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Route.Method, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        builder.AppendLine();
        builder.AppendLine("## 已识别但不作为同步缺口的 501 / stub 路由");
        builder.AppendLine();
        if (stubs.Length == 0)
        {
            builder.AppendLine("本次没有识别到 501 / stub 路由。");
            return;
        }

        builder.AppendLine("| 项目 | Method | URL | 说明 | 代码位置 |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var item in stubs)
        {
            builder.AppendLine($"| {EscapeMarkdown(item.Project)} | {item.Route.Method} | `{item.Route.Path}` | {EscapeMarkdown(item.Route.Description)} | {FormatSource(item.Route)} |");
        }
    }

    /// <summary>
    /// 追加当前第一版工具的覆盖范围说明。
    /// </summary>
    private static void AppendIgnoredNote(StringBuilder builder)
    {
        builder.AppendLine();
        builder.AppendLine("## 覆盖范围说明");
        builder.AppendLine();
        builder.AppendLine("本工具第一版只扫描当前项目关心的 OpenAI / Anthropic 主协议、legacy 接口和已知扩展路径。Gemini、MJ、Suno、Jimeng、管理后台、OAuth、健康检查和 provider alias 暂不作为主协议同步缺口。后续如果需要跟踪新的协议族，应先把路径加入工具中的 `ProtocolCatalog`。 ");
    }

    /// <summary>
    /// 格式化路由源码位置为 Markdown 链接。
    /// </summary>
    private static string FormatSource(ProtocolRoute route)
    {
        return $"[{route.SourcePath}:{route.LineNumber}]({route.SourcePath}#L{route.LineNumber})";
    }

    /// <summary>
    /// 转义 Markdown 表格中容易破坏结构的字符。
    /// </summary>
    private static string EscapeMarkdown(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal);
    }
}
