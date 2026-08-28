using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

using ProtocolSyncCheck;

var repositoryRoot = ResolveRepositoryRoot(args);
var outputPath = Path.Combine(repositoryRoot, "docs", "protocol-sync-report.md");
var skipPull = args.Any(arg => arg.Equals("--skip-pull", StringComparison.OrdinalIgnoreCase));

// 拉取两个参考项目（CLIProxyAPI + cc-switch），避免扫描过期基准。
var cpaPullResult = skipPull
    ? GitPullHelper.DescribeCurrentHead(repositoryRoot, "CLIProxyAPI")
    : GitPullHelper.PullReferenceProject(
        repositoryRoot,
        "CLIProxyAPI",
        fallbackUrl: "https://github.com/router-for-me/CLIProxyAPI.git",
        fallbackBranch: "main");
var ccPullResult = skipPull
    ? GitPullHelper.DescribeCurrentHead(repositoryRoot, "cc-switch")
    : GitPullHelper.PullReferenceProject(
        repositoryRoot,
        "cc-switch",
        fallbackUrl: "https://github.com/farion1231/cc-switch.git",
        fallbackBranch: "main");

var catalog = ProtocolCatalog.CreateDefault();
var projects = new[]
{
    ProjectScanDefinition.CurrentProject(repositoryRoot),
    ProjectScanDefinition.CliProxyApi(repositoryRoot),
    ProjectScanDefinition.CcSwitch(repositoryRoot)
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
var ccFieldGroups = CcSwitchFieldGroupBuilder.BuildGroups(repositoryRoot);
var ccFieldDiffs = FieldDiffEngine.ComputeDiffs(ccFieldGroups, currentFields);

// 反向推导协议向量：从 cc-switch 的 Rust 测试提取（输入→断言），在 AITool.Protocol 真实转换上执行。
var vectorSourceDir = Path.Combine(repositoryRoot, "reference-projects", "cc-switch", "src-tauri", "src", "proxy", "providers");
var vectorFiles = new[]
{
    "transform.rs", "transform_responses.rs", "transform_codex_chat.rs", "transform_codex_anthropic.rs"
};
var testVectors = vectorFiles
    .SelectMany(file => RustTestVectorExtractor.ExtractFile(Path.Combine(vectorSourceDir, file)))
    .ToList();
var vectorResults = ProtocolVectorRunner.RunAll(testVectors);

// 运行间基线：读取上次快照计算「参考项目协议变更」，扫描完成后写入新快照。
var previousBaseline = SyncBaselineStore.Load(repositoryRoot);
var currentBaseline = SyncBaselineStore.Capture(results, cpaFieldDiffs, ccFieldDiffs, cpaPullResult.CommitHash, ccPullResult.CommitHash);
var baselineChanges = previousBaseline is null
    ? []
    : SyncBaselineStore.ComputeChanges(previousBaseline, currentBaseline);

var report = ProtocolReportBuilder.Build(results, catalog, cpaFieldDiffs, ccFieldDiffs, cpaPullResult, ccPullResult, previousBaseline, baselineChanges, vectorResults);
File.WriteAllText(outputPath, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
SyncBaselineStore.Save(repositoryRoot, currentBaseline);

Console.WriteLine("协议同步报告已生成：" + Path.GetRelativePath(repositoryRoot, outputPath));
Console.WriteLine("CLIProxyAPI 基准版本：" + cpaPullResult.CommitHash + "（" + cpaPullResult.CommitDate + "）");
if (!cpaPullResult.Success)
{
    Console.WriteLine("⚠️ CLIProxyAPI 拉取未成功：" + cpaPullResult.Message);
}
Console.WriteLine("cc-switch 基准版本：" + ccPullResult.CommitHash + "（" + ccPullResult.CommitDate + "）");
if (!ccPullResult.Success)
{
    Console.WriteLine("⚠️ cc-switch 拉取未成功：" + ccPullResult.Message);
}

foreach (var result in results)
{
    Console.WriteLine(result.ProjectName + ": " + result.Routes.Count + " routes"
        + (result.UnclassifiedRoutes.Count > 0 ? "（另有 " + result.UnclassifiedRoutes.Count + " 条未跟踪路由）" : string.Empty));
}

Console.WriteLine("字段级对比：CLIProxyAPI " + cpaFieldDiffs.Count + " 个分组；cc-switch " + ccFieldDiffs.Count + " 个分组；AITool " + currentFields.Count + " 个字段");

// 反向推导向量测试结果摘要。
var vectorPassed = vectorResults.Count(result => result.Status == VectorRunStatus.Passed);
var vectorFailed = vectorResults.Count(result => result.Status == VectorRunStatus.Failed);
var vectorSkipped = vectorResults.Count(result => result.Status == VectorRunStatus.Skipped);
Console.WriteLine("反向推导向量：提取 " + testVectors.Count + " 个（来自 cc-switch 测试），执行通过 " + vectorPassed
    + "，失败 " + vectorFailed + "，跳过 " + vectorSkipped);
if (vectorFailed > 0)
{
    Console.WriteLine("❌ 向量测试失败 " + vectorFailed + " 个（详见报告「反向推导协议向量测试」，逐路径定位差异）");
}

if (previousBaseline is null)
{
    Console.WriteLine("首次运行：已建立协议基线快照（下次运行起将对比参考项目变更）");
}
else if (baselineChanges.Count > 0)
{
    Console.WriteLine("⚠️ 自上次运行以来检测到 " + baselineChanges.Count + " 处协议变更（详见报告「自上次运行以来的协议变更」）");
}
else
{
    Console.WriteLine("✅ 自上次运行以来参考项目协议无变化");
}

// 高优先级缺口时以非零退出码结束，便于脚本/CI 判断。
// 判定口径：CLIProxyAPI 是 AITool 的直接协议对标（同形态网关），其缺口立即失败；
// cc-switch 包含 AITool 有意不做的能力（Gemini、thinking 签名桥接、alpha-search 等），
// 其未覆盖字段仅提示不失败，由报告的快速结论与明细供人工甄别。
var missingRoutes = results
    .First(result => result.ProjectName == "CLIProxyAPI")
    .Routes.Select(route => route.Key)
    .Except(results.First(result => result.ProjectName == "AITool").Routes.Select(route => route.Key), StringComparer.OrdinalIgnoreCase)
    .Count();
var cpaMissingFields = cpaFieldDiffs.Sum(diff => diff.Rows.Count(row => row.TypeMatchStatus == FieldTypeMatchStatus.Missing));
var ccMissingFields = ccFieldDiffs.Sum(diff => diff.Rows.Count(row => row.TypeMatchStatus == FieldTypeMatchStatus.Missing));
if (ccMissingFields > 0)
{
    Console.WriteLine("ℹ️ cc-switch 有 " + ccMissingFields + " 个字段 AITool 未覆盖（含 Gemini/签名桥接等专属能力，供人工甄别，不影响退出码）");
}

if (missingRoutes > 0 || cpaMissingFields > 0 || vectorFailed > 0)
{
    Console.WriteLine("❌ 发现高优先级缺口：CLIProxyAPI 未实现路由 " + missingRoutes + " 条、未检测到字段 " + cpaMissingFields
        + " 个、向量测试失败 " + vectorFailed + " 个（退出码 1）");
    return 1;
}

return 0;

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

    public static ProjectScanDefinition CcSwitch(string root) => new()
    {
        Name = "cc-switch",
        Files = new[]
        {
            // Axum 路由集中在 proxy/server.rs 的 build_router()（含 compact / alpha-search / gemini 等全部本地端点）。
            RouteSourceFile.AxumRouter(root, "reference-projects/cc-switch/src-tauri/src/proxy/server.rs")
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

    public static RouteSourceFile AxumRouter(string root, string relativePath) =>
        Create(root, relativePath, RouteSourceKind.AxumRouter);

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
    GinRouter,
    AxumRouter
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
        "(?<name>\\w+)\\s*:=\\s*(?:(?<parent>[\\w.]+)\\.)?Group\\(\\\"(?<prefix>[^\"]*)\\\"",
        RegexOptions.Compiled);

    /// <summary>Axum 单行路由：.route("/path", post(handler))。</summary>
    private static readonly Regex AxumInlineRouteRegex = new(
        "\\.route\\(\\s*\\\"(?<path>[^\\\"]+)\\\"\\s*,\\s*(?<method>get|post|put|delete|patch|any)\\s*\\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Axum 多行路由：路径字面量独占一行（.route( 换行 "/path" 换行 post(...) )）。</summary>
    private static readonly Regex AxumPathLiteralRegex = new(
        "^\\s*\\\"(?<path>[^\\\"]+)\\\"\\s*,?\\s*$",
        RegexOptions.Compiled);

    /// <summary>Axum 方法行：post(handlers::xxx) / get(health_check) 等。</summary>
    private static readonly Regex AxumMethodRegex = new(
        "(?<method>get|post|put|delete|patch|any)\\s*\\(\\s*[\\w:]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
                case RouteSourceKind.AxumRouter:
                    ScanAxumRouter(file, catalog, routes, unclassified);
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

    /// <summary>
    /// 扫描 Axum 路由（cc-switch，Rust）。兼容两种书写形式：
    /// 单行 .route("/path", post(handler)) 与多行 .route( 换行 "/path", 换行 post(handler), )。
    /// any(..) 方法归一化为 ANY（Gemini 通配路由），无法匹配目录时进入未跟踪列表。
    /// </summary>
    private static void ScanAxumRouter(
        RouteSourceFile file,
        ProtocolCatalog catalog,
        List<ProtocolRoute> routes,
        List<RawRoute> unclassified)
    {
        var lines = File.ReadAllLines(file.FullPath);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var inlineMatch = AxumInlineRouteRegex.Match(line);
            if (inlineMatch.Success)
            {
                AddClassifiedRoutes(
                    routes,
                    unclassified,
                    catalog,
                    inlineMatch.Groups["method"].Value.ToUpperInvariant(),
                    NormalizeRoutePath(inlineMatch.Groups["path"].Value),
                    file.RelativePath,
                    index + 1);
                continue;
            }

            // 多行形式：当前行以 .route( 结尾，向后找路径字面量行与方法行（限制窗口避免误扫跨路由内容）。
            if (!line.Contains(".route(", StringComparison.Ordinal)
                || line.Contains('"'))
            {
                continue;
            }

            string? path = null;
            var pathLine = -1;
            for (var next = index + 1; next < Math.Min(index + 4, lines.Length); next++)
            {
                var pathMatch = AxumPathLiteralRegex.Match(lines[next]);
                if (!pathMatch.Success)
                {
                    continue;
                }

                path = NormalizeRoutePath(pathMatch.Groups["path"].Value);
                pathLine = next;
                break;
            }

            if (path is null)
            {
                continue;
            }

            for (var next = pathLine; next < Math.Min(pathLine + 3, lines.Length); next++)
            {
                var methodMatch = AxumMethodRegex.Match(lines[next]);
                if (!methodMatch.Success)
                {
                    continue;
                }

                AddClassifiedRoutes(
                    routes,
                    unclassified,
                    catalog,
                    methodMatch.Groups["method"].Value.ToUpperInvariant(),
                    path,
                    file.RelativePath,
                    index + 1);
                break;
            }
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
        List<FieldDiffResult> ccFieldDiffs,
        GitPullResult cpaPullResult,
        GitPullResult ccPullResult,
        SyncBaseline? previousBaseline,
        List<BaselineChange> baselineChanges,
        List<VectorRunResult> vectorResults)
    {
        var current = results.First(result => result.ProjectName == "AITool");
        var cpa = results.First(result => result.ProjectName == "CLIProxyAPI");
        var ccSwitch = results.FirstOrDefault(result => result.ProjectName == "cc-switch");
        var builder = new StringBuilder();

        builder.AppendLine("# AITool 与参考项目协议同步检查报告");
        builder.AppendLine();
        AppendRunMetadata(builder, cpaPullResult, ccPullResult);
        AppendQuickSummary(builder, current, cpa, ccSwitch, catalog, cpaFieldDiffs, ccFieldDiffs, previousBaseline, baselineChanges, vectorResults);
        if (previousBaseline is not null)
        {
            AppendBaselineChanges(builder, previousBaseline, baselineChanges, cpaPullResult, ccPullResult);
        }

        AppendVectorReport(builder, vectorResults);

        AppendScanPrerequisites(builder, results);
        AppendOverview(builder, current, cpa, catalog, cpaFieldDiffs);
        AppendRouteStatusTable(builder, current, cpa, catalog, cpaFieldDiffs);
        if (ccSwitch is not null)
        {
            AppendCcSwitchMatrix(builder, current, cpa, ccSwitch, catalog);
            AppendCcSwitchOnlyRoutes(builder, ccSwitch);
        }

        AppendUntrackedRoutes(builder, current, cpa);
        AppendFieldAlignmentReport(builder, cpaFieldDiffs, "CLIProxyAPI");
        AppendFieldAlignmentReport(builder, ccFieldDiffs, "cc-switch");
        AppendDiagnosticConclusion(builder, current, cpa, cpaFieldDiffs, ccFieldDiffs);
        return builder.ToString();
    }

    /// <summary>
    /// 顶部快速结论：一眼看清本次运行的不一致数量与优先级。
    /// </summary>
    private static void AppendQuickSummary(
        StringBuilder builder,
        ProjectScanResult current,
        ProjectScanResult cpa,
        ProjectScanResult? ccSwitch,
        ProtocolCatalog catalog,
        List<FieldDiffResult> cpaFieldDiffs,
        List<FieldDiffResult> ccFieldDiffs,
        SyncBaseline? previousBaseline,
        List<BaselineChange> baselineChanges,
        List<VectorRunResult> vectorResults)
    {
        var entries = BuildRouteEntries(current, cpa, catalog, cpaFieldDiffs);
        var notImplemented = entries.Count(e => e.Status == RouteSyncStatus.NotImplemented);
        var incomplete = entries.Count(e => e.Status == RouteSyncStatus.IncompleteFields);
        var cpaMissingFields = cpaFieldDiffs.Sum(diff => diff.Rows.Count(row => row.TypeMatchStatus == FieldTypeMatchStatus.Missing));
        var cpaTypeMismatches = cpaFieldDiffs.Sum(diff => diff.Rows.Count(row => row.TypeMatchStatus == FieldTypeMatchStatus.TypeMismatch));
        var ccMissingFields = ccFieldDiffs.Sum(diff => diff.Rows.Count(row => row.TypeMatchStatus == FieldTypeMatchStatus.Missing));
        var ccTypeMismatches = ccFieldDiffs.Sum(diff => diff.Rows.Count(row => row.TypeMatchStatus == FieldTypeMatchStatus.TypeMismatch));

        builder.AppendLine("## 快速结论");
        builder.AppendLine();
        builder.AppendLine("| 维度 | 数量 | 含义 |");
        builder.AppendLine("| --- | --- | --- |");
        builder.AppendLine("| ❌ CLIProxyAPI 有而 AITool 未实现的路由 | **" + notImplemented + "** | 高优先级：补齐路由 |");
        builder.AppendLine("| ⚠️ 已实现但字段不全的路由 | **" + incomplete + "** | 结合字段明细核对 |");
        builder.AppendLine("| ❌ CLIProxyAPI 字段未在 AITool 检测到 | **" + cpaMissingFields + "** | 高优先级：核对字段处理 |");
        builder.AppendLine("| ⚠️ CLIProxyAPI 字段类型线索不一致 | **" + cpaTypeMismatches + "** | 核对类型 |");
        builder.AppendLine("| ❌ cc-switch 字段未在 AITool 检测到 | **" + ccMissingFields + "** | cc-switch 处理了而 AITool 未覆盖（含 Gemini/签名桥接等专属能力，需人工甄别） |");
        builder.AppendLine("| ⚠️ cc-switch 字段类型线索不一致 | **" + ccTypeMismatches + "** | 核对类型 |");
        if (previousBaseline is null)
        {
            builder.AppendLine("| 🆕 基线快照 | 首次建立 | 下次运行起自动对比参考项目协议变更 |");
        }
        else
        {
            builder.AppendLine("| " + (baselineChanges.Count == 0 ? "✅" : "🔔") + " 自上次运行的协议变更 | **" + baselineChanges.Count + "** 处 | 详见下一节 |");
        }

        var vectorPassed = vectorResults.Count(result => result.Status == VectorRunStatus.Passed);
        var vectorFailed = vectorResults.Count(result => result.Status == VectorRunStatus.Failed);
        var vectorSkipped = vectorResults.Count(result => result.Status == VectorRunStatus.Skipped);
        builder.AppendLine(vectorFailed == 0
            ? "| ✅ 反向推导向量测试（cc-switch 基准） | **" + vectorPassed + "** 通过 / " + vectorSkipped + " 跳过 | cc-switch 测试断言在 AITool 转换上全部复现 |"
            : "| ❌ 反向推导向量测试失败 | **" + vectorFailed + "** / " + vectorResults.Count + " | 高优先级：逐路径定位与 cc-switch 的转换分歧 |");

        builder.AppendLine();
    }

    /// <summary>
    /// 反向推导协议向量测试报告：从 cc-switch 测试提取的（输入→断言）在 AITool.Protocol 真实转换上执行的结果。
    /// 每条失败精确到：测试名 + 来源位置 + 断言路径 + cc-switch 期望值 + AITool 实际值。
    /// </summary>
    private static void AppendVectorReport(StringBuilder builder, List<VectorRunResult> vectorResults)
    {
        builder.AppendLine("## 反向推导协议向量测试（以 cc-switch 测试为基准）");
        builder.AppendLine();
        builder.AppendLine("> 从 cc-switch 的 Rust 测试（`transform.rs` / `transform_responses.rs` / `transform_codex_chat.rs` / `transform_codex_anthropic.rs`）反向提取「输入 JSON → 断言路径/期望值」，在 AITool.Protocol 的真实转换代码上执行（与生产链路同一公开 API）。cc-switch 怎么转，AITool 就怎么跑——失败即分歧，明细定位到具体路径。");
        builder.AppendLine();

        var passed = vectorResults.Count(result => result.Status == VectorRunStatus.Passed);
        var failed = vectorResults.Count(result => result.Status == VectorRunStatus.Failed);
        var skipped = vectorResults.Count(result => result.Status == VectorRunStatus.Skipped);
        builder.AppendLine("- 执行结果：✅ 通过 **" + passed + "** / ❌ 失败 **" + failed + "** / ⏭ 跳过 **" + skipped + "**（共 " + vectorResults.Count + "）");
        var failedAssertions = vectorResults.SelectMany(result => result.Failures).Count();
        if (failed > 0)
        {
            builder.AppendLine("- 失败断言总数：**" + failedAssertions + "**（下表逐条列出）");
        }

        builder.AppendLine();
        if (vectorResults.Count == 0)
        {
            builder.AppendLine("⚠️ 未提取到任何测试向量——检查 cc-switch 参考代码是否就位。");
            builder.AppendLine();
            return;
        }

        if (failed == 0)
        {
            builder.AppendLine("✅ 全部向量通过：cc-switch 测试所断言的协议行为，AITool 协议桥全部复现。");
            builder.AppendLine();
            return;
        }

        // 按方向分组输出失败明细。
        foreach (var directionGroup in vectorResults
                     .Where(result => result.Status == VectorRunStatus.Failed)
                     .GroupBy(result => result.Vector.Direction)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine("### " + EscapeMarkdown(directionGroup.Key));
            builder.AppendLine();
            builder.AppendLine("| 测试 | 位置 | 断言路径 | cc-switch 期望 | AITool 实际 | 原因 |");
            builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var result in directionGroup)
            {
                foreach (var failure in result.Failures.Take(6))
                {
                    builder.AppendLine(
                        "| `" + EscapeMarkdown(result.Vector.TestName) + "` | `" + result.Vector.SourceFile + ":" + result.Vector.Line + "` | `"
                        + EscapeMarkdown(failure.Path) + "` | " + EscapeMarkdown(failure.Expected) + " | "
                        + EscapeMarkdown(failure.Actual) + " | " + EscapeMarkdown(failure.Reason) + " |");
                }

                if (result.Failures.Count > 6)
                {
                    builder.AppendLine("| `" + EscapeMarkdown(result.Vector.TestName) + "` | … | … | … | … | 另有 " + (result.Failures.Count - 6) + " 条断言失败 |");
                }
            }

            builder.AppendLine();
        }

        if (skipped > 0)
        {
            builder.AppendLine("<details><summary>⏭ 跳过的向量（" + skipped + " 个，多为宏内含 Rust 表达式无法静态解析）</summary>");
            builder.AppendLine();
            foreach (var result in vectorResults.Where(result => result.Status == VectorRunStatus.Skipped).Take(20))
            {
                builder.AppendLine("- `" + result.Vector.TestName + "`（" + result.Vector.SourceFile + ":" + result.Vector.Line + "）：" + EscapeMarkdown(result.SkipReason ?? string.Empty));
            }

            builder.AppendLine();
            builder.AppendLine("</details>");
            builder.AppendLine();
        }
    }

    /// <summary>
    /// 自上次运行以来的协议变更：参考项目 commit 变化 + 三方路由增删 + 参考字段基线增删。
    /// </summary>
    private static void AppendBaselineChanges(
        StringBuilder builder,
        SyncBaseline previousBaseline,
        List<BaselineChange> changes,
        GitPullResult cpaPullResult,
        GitPullResult ccPullResult)
    {
        builder.AppendLine("## 自上次运行以来的协议变更");
        builder.AppendLine();
        builder.AppendLine("- 上次基线：" + previousBaseline.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss")
            + "（CLIProxyAPI `" + (previousBaseline.References.TryGetValue("CLIProxyAPI", out var cpaHash) ? cpaHash : "未知")
            + "`，cc-switch `" + (previousBaseline.References.TryGetValue("cc-switch", out var ccHash) ? ccHash : "未知") + "`）");
        builder.AppendLine("- 本次基准：CLIProxyAPI `" + cpaPullResult.CommitHash + "`，cc-switch `" + ccPullResult.CommitHash + "`");
        builder.AppendLine();

        if (changes.Count == 0)
        {
            builder.AppendLine("✅ 两次运行之间三个项目的路由与参考字段基线无变化。");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("> 下表是两次运行快照的差集：**参考项目新增的字段/路由意味着上游协议演进，AITool 侧若未同步跟进会出现行为差异**；AITool 自身路由增删则反映本地开发进度。");
        builder.AppendLine();
        builder.AppendLine("| 变更 | 范围 | 类型 | 值 |");
        builder.AppendLine("| --- | --- | --- | --- |");
        foreach (var change in changes.OrderBy(change => change.Scope, StringComparer.OrdinalIgnoreCase).ThenBy(change => change.Value, StringComparer.OrdinalIgnoreCase))
        {
            var marker = change.ChangeKind == BaselineChangeKind.Added ? "➕ 新增" : "➖ 移除";
            builder.AppendLine("| " + marker + " | " + EscapeMarkdown(change.Scope) + " | " + change.Kind + " | `" + EscapeMarkdown(change.Value) + "` |");
        }

        builder.AppendLine();
    }

    private static void AppendRunMetadata(StringBuilder builder, GitPullResult cpaPullResult, GitPullResult ccPullResult)
    {
        builder.AppendLine("## 本次运行信息");
        builder.AppendLine();
        builder.AppendLine("- 生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine("- CLIProxyAPI 基准版本：`" + cpaPullResult.CommitHash + "`（" + cpaPullResult.CommitDate + "）");
        builder.AppendLine(cpaPullResult.Success
            ? "- CLIProxyAPI 拉取结果：✅ " + EscapeMarkdown(cpaPullResult.Message)
            : "- CLIProxyAPI 拉取结果：⚠️ 未更新（" + EscapeMarkdown(cpaPullResult.Message) + "）");
        builder.AppendLine("- cc-switch 基准版本：`" + ccPullResult.CommitHash + "`（" + ccPullResult.CommitDate + "）");
        builder.AppendLine(ccPullResult.Success
            ? "- cc-switch 拉取结果：✅ " + EscapeMarkdown(ccPullResult.Message)
            : "- cc-switch 拉取结果：⚠️ 未更新（" + EscapeMarkdown(ccPullResult.Message) + "）");
        builder.AppendLine();
    }

    private static void AppendScanPrerequisites(StringBuilder builder, IReadOnlyList<ProjectScanResult> results)
    {
        var missing = results
            .SelectMany(result => result.MissingFiles.Select(file => result.ProjectName + "：`" + file + "`"))
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

    /// <summary>
    /// 三方路由矩阵：协议目录内每条目标路由在 AITool / CLIProxyAPI / cc-switch 的覆盖情况。
    /// cc-switch 侧额外把等价别名（如 /v1/v1/*、/codex/*、不带 /v1 前缀）折叠进主路由判断。
    /// </summary>
    private static void AppendCcSwitchMatrix(
        StringBuilder builder,
        ProjectScanResult current,
        ProjectScanResult cpa,
        ProjectScanResult ccSwitch,
        ProtocolCatalog catalog)
    {
        builder.AppendLine("## 三方路由覆盖矩阵（AITool / CLIProxyAPI / cc-switch）");
        builder.AppendLine();
        builder.AppendLine("> cc-switch 为 Rust/Axum 本地网关，列出现的是协议目录内主路由与 legacy 路由的覆盖情况；其特有别名前缀（`/v1/v1/*`、`/codex/*`、`/grokbuild/*`、`/claude/*`）与 Gemini 通配路由见下方专属清单。");
        builder.AppendLine();
        builder.AppendLine("| 协议 | Method | URL | AITool | CLIProxyAPI | cc-switch | 说明 |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");

        var currentKeys = current.Routes.Select(route => route.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cpaKeys = cpa.Routes.Select(route => route.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ccKeys = ccSwitch.Routes.Select(route => route.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ccAliasKeys = BuildCcSwitchAliasKeys(ccSwitch);

        foreach (var target in catalog.SyncTargets)
        {
            var comparisonKey = target.ComparisonKey;
            var aitoolHas = currentKeys.Contains(comparisonKey);
            var cpaHas = cpaKeys.Contains(comparisonKey);
            var ccHas = ccKeys.Contains(comparisonKey) || ccAliasKeys.Contains(comparisonKey);
            builder.AppendLine(
                "| " + EscapeMarkdown(target.Protocol) + " | " + target.Method + " | `" + target.Path + "` | "
                + FormatPresence(aitoolHas) + " | " + FormatPresence(cpaHas) + " | " + FormatPresence(ccHas) + " | "
                + EscapeMarkdown(target.Description) + " |");
        }

        builder.AppendLine();
    }

    /// <summary>
    /// cc-switch 等价别名集合：本地端点支持 /v1/v1/*（客户端双重前缀）、/codex/*（Codex CLI 前缀）、
    /// 以及不带 /v1 的裸路径（/responses、/chat/completions），这些与主路由语义等价，折叠后不判缺失。
    /// </summary>
    private static HashSet<string> BuildCcSwitchAliasKeys(ProjectScanResult ccSwitch)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in ccSwitch.Routes.Concat(ccSwitch.UnclassifiedRoutes.Select(ToProtocolRoute)))
        {
            var path = route.Path;
            string? stripped = null;
            if (path.StartsWith("/v1/v1/", StringComparison.Ordinal))
            {
                stripped = path["/v1".Length..];
            }
            else if (path.StartsWith("/codex/v1/", StringComparison.Ordinal))
            {
                stripped = path["/codex".Length..];
            }
            else if (path.StartsWith("/responses", StringComparison.Ordinal)
                || path.StartsWith("/chat/completions", StringComparison.Ordinal)
                || path.StartsWith("/models", StringComparison.Ordinal))
            {
                stripped = "/v1" + path;
            }

            if (stripped is not null)
            {
                aliases.Add(route.Protocol + ":" + route.Method + " " + stripped);
            }
        }

        return aliases;
    }

    private static ProtocolRoute ToProtocolRoute(RawRoute route) => new(
        route.Method,
        route.Path,
        string.Empty,
        "未跟踪",
        string.Empty,
        IsNotImplemented: false,
        route.SourcePath,
        route.LineNumber);

    private static string FormatPresence(bool present) => present ? "✅" : "—";

    /// <summary>
    /// cc-switch 特有路由清单（不在协议目录内）：Gemini 通配、别名前缀、alpha-search 等，供人工确认。
    /// </summary>
    private static void AppendCcSwitchOnlyRoutes(StringBuilder builder, ProjectScanResult ccSwitch)
    {
        var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allCcRoutes = ccSwitch.Routes.Concat(ccSwitch.UnclassifiedRoutes.Select(ToProtocolRoute))
            .GroupBy(route => route.Method + " " + route.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(route => route.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(route => route.Method, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (allCcRoutes.Count == 0)
        {
            return;
        }

        builder.AppendLine("## cc-switch 本地端点全量清单");
        builder.AppendLine();
        builder.AppendLine("> cc-switch 的 Axum 路由全集（含协议目录内路由与本地别名/Gemini 通配/健康检查）。与 AITool 对照用于发现可借鉴的端点（如 compact 多前缀、alpha-search）。");
        builder.AppendLine();
        builder.AppendLine("| Method | URL | 位置 |");
        builder.AppendLine("| --- | --- | --- |");
        foreach (var route in allCcRoutes)
        {
            builder.AppendLine("| " + route.Method + " | `" + route.Path + "` | `" + Path.GetFileName(route.SourcePath) + ":" + route.LineNumber + "` |");
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

    private static void AppendFieldAlignmentReport(StringBuilder builder, List<FieldDiffResult> fieldDiffs, string referenceName)
    {
        builder.AppendLine("## AITool 与 " + referenceName + " 字段对比");
        builder.AppendLine();
        builder.AppendLine("> 字段基线来自 " + referenceName + " 的协议转换/流式处理代码；AITool 侧同时扫描协议桥接代码、代理控制器和 Responses 流式状态代码。字段出现但语义未必完全等价，需结合状态和来源位置判断。");
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
                builder.AppendLine("✅ " + referenceName + " 参考字段均已在 AITool 中检测到处理逻辑。");
                builder.AppendLine();
                continue;
            }

            builder.AppendLine("| 字段 | " + referenceName + " 类型 | 可选 | AITool 状态 | AITool 类型线索 | AITool 位置 |");
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
        List<FieldDiffResult> fieldDiffs,
        List<FieldDiffResult> ccFieldDiffs)
    {
        var missingRoutes = cpa.Routes
            .Where(route => !route.IsNotImplemented)
            .Select(route => route.Key)
            .Except(current.Routes.Where(route => !route.IsNotImplemented).Select(route => route.Key), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mismatches = fieldDiffs.SelectMany(diff => diff.MisalignedRows)
            .Concat(ccFieldDiffs.SelectMany(diff => diff.MisalignedRows))
            .ToList();

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
    public static GitPullResult DescribeCurrentHead(string repositoryRoot, string projectDirName)
    {
        var projectDir = Path.Combine(repositoryRoot, "reference-projects", projectDirName);
        return new GitPullResult(
            true,
            "已跳过拉取（--skip-pull）",
            GetHeadInfo(projectDir, out var commitDate) ?? "未知",
            commitDate ?? "未知");
    }

    /// <summary>
    /// 拉取 reference-projects 下的参考项目：先走 origin（可能是 gh-proxy 镜像），
    /// 失败时回退官方仓库地址再试一次。
    /// </summary>
    public static GitPullResult PullReferenceProject(
        string repositoryRoot,
        string projectDirName,
        string fallbackUrl,
        string fallbackBranch)
    {
        var projectDir = Path.Combine(repositoryRoot, "reference-projects", projectDirName);
        if (!Directory.Exists(Path.Combine(projectDir, ".git")))
        {
            return new GitPullResult(false, $"未找到 reference-projects/{projectDirName}/.git，跳过拉取。", "未知", "未知");
        }

        Console.Write("正在拉取 " + projectDirName + " 最新代码...");
        var (success, output) = RunGitPull(projectDir);
        if (!success)
        {
            // 镜像源可能失效，回退到官方 GitHub 地址再试一次。
            Console.WriteLine(" ⚠️ origin 拉取失败：" + output.Split('\n').FirstOrDefault()?.Trim() + "，尝试官方仓库...");
            var (fallbackSuccess, fallbackOutput) = RunGitPull(projectDir, fallbackUrl, fallbackBranch);
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

    private static (bool Success, string Output) RunGitPull(string workingDirectory, string? fallbackUrl = null, string? fallbackBranch = null)
    {
        try
        {
            var arguments = fallbackUrl is null
                ? "pull --ff-only"
                : "pull --ff-only \"" + fallbackUrl + "\" " + (fallbackBranch ?? "main");

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
