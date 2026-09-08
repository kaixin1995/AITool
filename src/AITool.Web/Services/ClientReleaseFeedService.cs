using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AITool.Web.Services;

/// <summary>
/// 客户端官方发布源查询服务：按请求头模板档案的 Key 拉取官方发布页数据
/// （GitHub Releases / npm registry），输出事实清单供 AI 总结最新版本。
/// <para>
/// 确定性数据优先、AI 只做归纳，而不是让 AI 凭训练记忆猜版本号。
/// 没有公开发布源的档案（如 ZCode / Antigravity 闭源客户端）返回失败，调用方回落 AI 自身知识。
/// 查询结果按档案 Key 进程级缓存 30 分钟（GitHub 未认证配额 60 次/小时，手动低频按钮足够）。
/// </para>
/// </summary>
public sealed class ClientReleaseFeedService
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
            // antigravity.google/changelog 按产品分四个板块（hub/cli/ide/sdk），CLI 版本在 data-list-panel="cli" 面板内。
            new ReleaseSource("changelog", "官方更新页: antigravity.google/changelog（Antigravity CLI 板块）", "https://antigravity.google/changelog", Section: "cli")
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

    private sealed record ReleaseSource(string Kind, string Label, string Url, string? Section = null);

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
                        "changelog" => string.IsNullOrEmpty(source.Section)
                            ? ParseChangelogHtml(body)
                            : ParseSectionedChangelogHtml(body, source.Section),
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

    /// <summary>
    /// 解析带产品板块的 changelog 页面（如 antigravity.google/changelog）：页面按产品把更新表
    /// 放进 <c>&lt;div data-list-panel="cli"&gt;</c> 等面板容器（tab 切换显示），先用 Section 键切片出
    /// 目标面板，再提取「version-link 锚点 + 紧随其后的日期文本」配对。面板结构缺失时回退整页行配对。
    /// </summary>
    public static List<string> ParseSectionedChangelogHtml(string html, string section)
    {
        try
        {
            var marker = $"data-list-panel=\"{section}\"";
            var start = html.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return ParseChangelogHtml(html);
            start = html.IndexOf('>', start) + 1;
            var next = html.IndexOf("data-list-panel=", start, StringComparison.Ordinal);
            var pane = next > start ? html[start..next] : html[start..];

            var lines = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pairRegex = new Regex(
                """class="version-link[^"]*"[^>]*>(?<version>[^<]+)</a>\s*<br[^>]*>\s*(?<date>[^<\r\n]+)""",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
            foreach (Match match in pairRegex.Matches(pane))
            {
                var version = match.Groups["version"].Value.Trim();
                var date = match.Groups["date"].Value.Trim();
                if (version.Length == 0 || date.Length == 0 || !seen.Add(version)) continue;
                lines.Add($"- {version}（发布于 {date}，正式版）");
                if (lines.Count >= 12) break;
            }
            if (lines.Count > 0) return lines;

            // 结构化配对失败：对切片内容退回通用行配对。
            return ParseChangelogHtml(pane);
        }
        catch
        {
            return [];
        }
    }

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
    /// 解析官网 changelog 页面（如 zcode.z.ai/changelog 的 Next.js SSR HTML）为事实行：
    /// 剥掉 script/style 与标签后按「版本号行 + Released 日期行」配对提取，取前 12 个版本。
    /// </summary>
    public static List<string> ParseChangelogHtml(string html)
    {
        var lines = new List<string>();
        try
        {
            var text = html
                .Replace("\r", "\n", StringComparison.Ordinal)
                .Replace("<script", "\n<script", StringComparison.OrdinalIgnoreCase)
                .Replace("</script>", "</script>\n", StringComparison.OrdinalIgnoreCase)
                .Replace("<style", "\n<style", StringComparison.OrdinalIgnoreCase)
                .Replace("</style>", "</style>\n", StringComparison.OrdinalIgnoreCase);
            var stripped = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "\n");
            stripped = stripped
                .Replace("&amp;", "&", StringComparison.Ordinal)
                .Replace("&quot;", "\"", StringComparison.Ordinal)
                .Replace("&#x27;", "'", StringComparison.Ordinal)
                .Replace("&#39;", "'", StringComparison.Ordinal)
                .Replace("&lt;", "<", StringComparison.Ordinal)
                .Replace("&gt;", ">", StringComparison.Ordinal)
                .Replace("&nbsp;", " ", StringComparison.Ordinal);

            var rawLines = stripped.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rawLines.Count && lines.Count < 12; i++)
            {
                var candidate = rawLines[i];
                if (!IsBareVersionLine(candidate)) continue;
                if (i + 1 >= rawLines.Count || !rawLines[i + 1].StartsWith("Released", StringComparison.OrdinalIgnoreCase)) continue;

                var dateText = rawLines[i + 1]["Released".Length..].Trim().TrimEnd('.');
                if (!seen.Add(candidate)) continue;
                lines.Add($"- {candidate}（发布于 {dateText}，正式版）");
            }
        }
        catch
        {
            return [];
        }
        return lines;

        static bool IsBareVersionLine(string line)
            => line.Length is >= 3 and <= 20
               && char.IsAsciiDigit(line[0])
               && line.Count(c => c == '.') is 1 or 2
               && line.All(c => char.IsAsciiDigit(c) || c == '.');
    }

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
