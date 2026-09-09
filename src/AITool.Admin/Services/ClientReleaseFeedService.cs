using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AITool.Admin.Services;

/// <summary>
/// 客户端官方发布源查询服务：按请求头模板档案的 Key 拉取官方发布页数据
/// （GitHub Releases / npm registry），输出事实清单供 AI 总结最新版本。
/// <para>
/// 确定性数据优先、AI 只做归纳，而不是让 AI 凭训练记忆猜版本号。
/// 没有公开发布源的档案（如 ZCode / Antigravity 闭源客户端）返回失败，调用方回落 AI 自身知识。
/// 查询结果按档案 Key 进程级缓存 30 分钟（GitHub 未认证配额 60 次/小时，手动低频按钮足够）。
/// </para>
/// </summary>
public sealed partial class ClientReleaseFeedService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>档案 Key → 官方发布源清单（大小写不敏感；有序，前者优先）。</summary>
    private static readonly Dictionary<string, IReadOnlyList<ReleaseSource>> KnownSources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["opencode"] =
        [
            new ReleaseSource("npm", "npm: opencode-ai", "https://registry.npmjs.org/opencode-ai/latest"),
            new ReleaseSource("github", "GitHub Releases: sst/opencode", "https://api.github.com/repos/sst/opencode/releases?per_page=5")
        ],
        ["claudecode"] =
        [
            new ReleaseSource("npm", "npm: @anthropic-ai/claude-code", "https://registry.npmjs.org/@anthropic-ai/claude-code/latest")
        ],
        ["codexcli"] =
        [
            new ReleaseSource("npm", "npm: @openai/codex", "https://registry.npmjs.org/@openai/codex/latest"),
            new ReleaseSource("github", "GitHub Releases: openai/codex", "https://api.github.com/repos/openai/codex/releases?per_page=5")
        ],
        ["codexvscode"] =
        [
            new ReleaseSource("npm", "npm: @openai/codex", "https://registry.npmjs.org/@openai/codex/latest"),
            new ReleaseSource("github", "GitHub Releases: openai/codex", "https://api.github.com/repos/openai/codex/releases?per_page=5")
        ],
        ["kimi"] =
        [
            new ReleaseSource("github", "GitHub Releases: MoonshotAI/kimi-cli", "https://api.github.com/repos/MoonshotAI/kimi-cli/releases?per_page=5")
        ],
        ["zcode"] =
        [
            new ReleaseSource("changelog", "官方更新页: zcode.z.ai/changelog", "https://zcode.z.ai/en/changelog")
        ],
        ["antigravity"] =
        [
            // antigravity.google/changelog 含多个产品板块（2.0/CLI/SDK/IDE），摘要整体交给 AI 按客户端身份识别 CLI 板块。
            new ReleaseSource("changelog", "官方更新页: antigravity.google/changelog", "https://antigravity.google/changelog")
        ]
    };

    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, ReleaseFeedResult Result)> Cache = new();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ClientReleaseFeedService> _logger;

    public ClientReleaseFeedService(IHttpClientFactory httpClientFactory, ILogger<ClientReleaseFeedService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>一个档案的发布源查询结果。Success=false 表示没有已知源或全部拉取失败。</summary>
    public sealed record ReleaseFeedResult(bool Success, string Facts, string SourceLabels);

    private sealed record ReleaseSource(string Kind, string Label, string Url);

    public Task<ReleaseFeedResult> LookupAsync(string profileKey, CancellationToken cancellationToken)
    {
        var key = profileKey?.Trim() ?? string.Empty;
        if (Cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheTtl)
        {
            return Task.FromResult(cached.Result);
        }

        return LookupCoreAsync(key, cancellationToken);
    }

    private async Task<ReleaseFeedResult> LookupCoreAsync(string key, CancellationToken cancellationToken)
    {
        var facts = new List<string>();
        var labels = new List<string>();

        if (KnownSources.TryGetValue(key, out var sources))
        {
            var client = _httpClientFactory.CreateClient("ReleaseFeed");
            foreach (var source in sources)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
                    if (source.Kind == "github")
                    {
                        // GitHub API 要求带 User-Agent，否则 403。
                        request.Headers.UserAgent.ParseAdd("AI-Tool-Admin");
                        request.Headers.Accept.ParseAdd("application/vnd.github+json");
                    }
                    else
                    {
                        // 官网更新页按浏览器 UA 访问，避免被基础反爬拦截。
                        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AI-Tool-Admin");
                    }
                    using var response = await client.SendAsync(request, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);

                    var lines = source.Kind switch
                    {
                        "npm" => ParseNpmLatest(body),
                        // changelog 网页只做降噪摘要，版本识别交给 AI（页面结构随时可能改版，不做硬编码解析）。
                        "changelog" => WrapDigestAsFacts(ExtractChangelogDigest(body)),
                        _ => ParseGitHubReleases(body)
                    };
                    if (lines.Count > 0)
                    {
                        facts.AddRange(lines);
                        labels.Add(source.Label);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fetch release feed failed: {Source} {Url}", source.Label, source.Url);
                }
            }
        }

        var result = facts.Count > 0
            ? new ReleaseFeedResult(true, string.Join("\n", facts), string.Join("；", labels))
            : new ReleaseFeedResult(false, string.Empty, string.Empty);
        Cache[key] = (DateTimeOffset.UtcNow, result);
        return result;
    }

    /// <summary>把摘要文本包装成事实清单（空摘要返回空清单表示该源无数据）。</summary>
    private static List<string> WrapDigestAsFacts(string digest)
        => string.IsNullOrWhiteSpace(digest) ? [] : [digest];

    /// <summary>解析 npm /latest 响应为事实行。解析失败返回空清单。</summary>
    public static List<string> ParseNpmLatest(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("version", out var versionEl)
                && versionEl.ValueKind == JsonValueKind.String)
            {
                return [$"- npm dist-tag latest：{versionEl.GetString()}（正式版）"];
            }
        }
        catch
        {
            // 交给调用方按「该源无数据」处理。
        }
        return [];
    }

    /// <summary>
    /// 从 changelog 网页提取「AI 可读摘要」：剥掉 script/style/标签得到文本行，
    /// 只保留短行（版本号、日期、板块标题、功能标题），丢弃长段落与 JS/CSS 残留，
    /// 并限制总字符预算（适配 AI 上下文）。
    /// <para>
    /// 刻意<strong>不硬编码页面结构</strong>（选择器/配对规则随时可能被官网改版破坏）：
    /// 这里只负责压噪降噪，具体哪一行是版本号、属于哪个产品板块，由 AI 结合客户端身份灵活判断。
    /// </para>
    /// </summary>
    public static string ExtractChangelogDigest(string html, int maxChars = 64000)
    {
        try
        {
            // script/style 块整体移除（内容对版本识别无意义且极占预算）。
            var cleaned = RegexScriptBlocks().Replace(html, " ");
            cleaned = RegexStyleBlocks().Replace(cleaned, " ");
            // 剥标签成文本行 + 解码常见实体。
            var stripped = RegexTags().Replace(cleaned, "\n");
            stripped = System.Net.WebUtility.HtmlDecode(stripped);

            var sb = new StringBuilder();
            var total = 0;
            foreach (var rawLine in stripped.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.Length > 140) continue;
                if (total + line.Length + 1 > maxChars) break;
                sb.Append(line).Append('\n');
                total += line.Length + 1;
            }
            return sb.ToString().TrimEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    [GeneratedRegex(@"<script\b[^>]*>[\s\S]*?</script>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RegexScriptBlocks();

    [GeneratedRegex(@"<style\b[^>]*>[\s\S]*?</style>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RegexStyleBlocks();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex RegexTags();

    /// <summary>解析 GitHub Releases 清单为事实行（版本 + 日期 + 是否预发布）。解析失败返回空清单。</summary>
    public static List<string> ParseGitHubReleases(string body)    {
        var lines = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return lines;

            foreach (var release in root.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object
                    || !release.TryGetProperty("tag_name", out var tagEl)
                    || tagEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var tag = tagEl.GetString();
                var isPrerelease = release.TryGetProperty("prerelease", out var preEl) && preEl.ValueKind == JsonValueKind.True;
                string dateText = "未知日期";
                if (release.TryGetProperty("published_at", out var dateEl) && dateEl.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(dateEl.GetString(), out var publishedAt))
                {
                    dateText = publishedAt.ToString("yyyy-MM-dd");
                }

                lines.Add($"- {tag}（发布于 {dateText}，{(isPrerelease ? "预发布" : "正式版")}）");
            }
        }
        catch
        {
            return [];
        }
        return lines;
    }
}
