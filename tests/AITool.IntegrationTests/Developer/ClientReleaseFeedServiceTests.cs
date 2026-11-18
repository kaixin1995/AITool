using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

namespace AITool.IntegrationTests.Developer;

/// <summary>
/// 客户端发布源解析契约：GitHub Releases 清单与 npm /latest 响应都要能转成
/// 给 AI 归纳用的事实行。用例取自真实接口数据形态（tag 含 rust-v 前缀、prerelease 标记等）。
/// </summary>
public sealed class ClientReleaseFeedServiceTests
{
    [Fact]
    public async Task Claude_sources_keep_successes_when_another_source_fails()
    {
        using var client = new HttpClient(new ReleaseHandler());
        var service = new ClientReleaseFeedService(new ReleaseClientFactory(client), NullLogger<ClientReleaseFeedService>.Instance);
        var sources = await service.LookupClaudeDesktopAsync(CancellationToken.None);
        sources.Single(x => x.Key == "cli").Version.Should().Be("2.1.327");
        sources.Single(x => x.Key == "npm").Error.Should().NotBeNullOrEmpty();
        sources.Single(x => x.Key == "desktop").Version.Should().Be("6.6395.0");
    }

    private sealed class ReleaseClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            var response = host switch
            {
                "code.claude.com" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""<div class="update-container" id="2-1-327">""") },
                "aiticker.site" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""<ul class="rel-list"><li><a href="/"><code>v6.6395.0</code>""") },
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            };
            return Task.FromResult(response);
        }
    }
    [Fact]
    public void Claude_release_sources_extract_only_structured_release_entries()
    {
        const string cli = """<a href="#2-9-999">noise</a><div class="update relative update-container" id="2-1-327"><button>2.1.327</button></div>""";
        const string desktop = """<meta name="description" content="latest v99.0.0"><ul class="rel-list"><li><a href="/tools/claude-desktop/6.6395.0/"><code>v6.6395.0</code></a></li></ul>""";
        ClientReleaseFeedService.ParseClaudeChangelogVersion(cli).Should().Be("2.1.327");
        ClientReleaseFeedService.ParseClaudeDesktopVersion(desktop).Should().Be("6.6395.0");
        ClientReleaseFeedService.ParseClaudeChangelogVersion("<div>2.1.327</div>").Should().BeNull();
        ClientReleaseFeedService.ParseClaudeDesktopVersion("<p>latest 6.6395.0</p>").Should().BeNull();
    }
    [Fact]
    public void GitHub_releases_parse_to_fact_lines()
    {
        const string body = """
        [
          {"tag_name":"rust-v0.154.0-alpha.6","prerelease":true,"published_at":"2026-09-05T08:30:00Z"},
          {"tag_name":"rust-v0.154.0","prerelease":false,"published_at":"2026-09-01T10:00:00Z"},
          {"tag_name":"rust-v0.153.3","prerelease":false,"published_at":"2026-08-20T10:00:00Z"}
        ]
        """;
        var lines = ClientReleaseFeedService.ParseGitHubReleases(body);

        lines.Should().HaveCount(3);
        lines[0].Should().Contain("rust-v0.154.0-alpha.6").And.Contain("预发布").And.Contain("2026-09-05");
        lines[1].Should().Contain("rust-v0.154.0").And.Contain("正式版");
    }

    [Fact]
    public void GitHub_releases_handles_malformed_body()
    {
        ClientReleaseFeedService.ParseGitHubReleases("not json").Should().BeEmpty();
        ClientReleaseFeedService.ParseGitHubReleases("{}").Should().BeEmpty();
        ClientReleaseFeedService.ParseGitHubReleases("[]").Should().BeEmpty();
    }

    [Fact]
    public void Npm_latest_parses_version()
    {
        const string body = """
        {"name":"@anthropic-ai/claude-code","version":"2.1.170","bin":{"claude":"bin/claude.exe"}}
        """;
        ClientReleaseFeedService.ParseNpmLatest(body)
            .Should().ContainSingle().Which.Should().Contain("2.1.170").And.Contain("正式版");
    }

    [Fact]
    public void Npm_latest_handles_malformed_body()
    {
        ClientReleaseFeedService.ParseNpmLatest("not json").Should().BeEmpty();
        ClientReleaseFeedService.ParseNpmLatest("{}").Should().BeEmpty();
    }

    [Fact]
    public void Changelog_digest_handles_workbuddy_release_headings()
    {
        // WorkBuddy 官方更新日志形态：「x.y.z 版本发布 🚀（日期）」标题按时间倒序；
        // 长功能行丢弃不影响版本归纳（真实页面降噪后 88 个版本标题全保留）。
        const string html = """
        <html><head><script>var tracking=1;</script></head><body>
        <nav>WorkBuddy 更新日志</nav>
        <h2 id="_5-9-3-版本发布-🚀-2026-10-27">5.9.3 版本发布 🚀（2026-10-27）</h2>
        <p>CLI 优先使用 unpacked 路径，减少启动耗时；修复 CLI 连接器按需托管 Node.js 运行时与首次连接进度提示的问题。</p>
        <h2 id="_5-9-2-版本发布-2026-10-26">5.9.2 版本发布（2026-10-26）</h2>
        </body></html>
        """;
        var digest = ClientReleaseFeedService.ExtractChangelogDigest(html);

        digest.Should().Contain("5.9.3 版本发布").And.Contain("2026-10-27").And.Contain("5.9.2 版本发布");
    }

    [Fact]
    public void Changelog_digest_keeps_short_fact_lines_and_strips_noise()
    {
        // 结构无关的降噪契约：script/style 块整体移除；标签剥成文本行；
        // 短行（版本号/日期/板块标题/功能标题）保留，长段落丢弃——版本识别交给 AI。
        const string html = """
        <html><head><script>var tracking=1;</script><style>.a{color:red}</style></head><body>
        <nav>Antigravity CLI</nav>
        <h2>1.1.25</h2><span>September 3, 2026</span>
        <p>Adds an opt-in workspace-grouped view to the resume picker with toggling, adds Gemini 3.8 Flash to the model catalog for enterprise users, updates Markdown-defined custom agents to inherit ambient skills, rules, and subagents by default.</p>
        <h2>1.1.24</h2><span>September 2, 2026</span>
        </body></html>
        """;
        var digest = ClientReleaseFeedService.ExtractChangelogDigest(html);

        digest.Should().Contain("1.1.25").And.Contain("September 3, 2026").And.Contain("1.1.24");
        digest.Should().Contain("Antigravity CLI", "板块标题应保留，供 AI 判断版本归属");
        digest.Should().NotContain("workspace-grouped view to the resume picker", "长段落应被丢弃");
        digest.Should().NotContain("tracking", "script 内容应被移除");
    }

    [Fact]
    public void Changelog_digest_respects_character_budget()
    {
        var manyVersions = string.Concat(Enumerable.Range(0, 500)
            .Select(i => $"<div>9.9.{i}</div><span>January 1, 2026</span>"));
        var digest = ClientReleaseFeedService.ExtractChangelogDigest($"<html><body>{manyVersions}</body></html>", maxChars: 800);

        digest.Length.Should().BeLessThanOrEqualTo(820);
    }

    [Fact]
    public void Changelog_digest_handles_malformed_body()
    {
        ClientReleaseFeedService.ExtractChangelogDigest("").Should().BeEmpty();
        ClientReleaseFeedService.ExtractChangelogDigest("plain text no tags").Should().Be("plain text no tags");
    }
}
