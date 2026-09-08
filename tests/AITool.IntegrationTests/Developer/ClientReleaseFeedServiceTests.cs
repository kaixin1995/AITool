using AITool.Web.Services;
using FluentAssertions;

namespace AITool.IntegrationTests.Developer;

/// <summary>
/// 客户端发布源解析契约：GitHub Releases 清单与 npm /latest 响应都要能转成
/// 给 AI 归纳用的事实行。用例取自真实接口数据形态（tag 含 rust-v 前缀、prerelease 标记等）。
/// </summary>
public sealed class ClientReleaseFeedServiceTests
{
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
    public void Changelog_html_extracts_version_date_pairs()
    {
        // 取自 zcode.z.ai/en/changelog 真实结构：版本号行 + Released 日期行 + Release 链接文字。
        const string html = """
        <html><head><script>var x=1;</script><style>a{}</style></head><body>
        <h2>3.11.2</h2><p>Released Sep 4, 2026</p><a href="/dl">Download</a><a>Release v3.11.2</a>
        <ul><li>New Features</li><li>Support for uploading PDF files</li></ul>
        <h2>3.10.0</h2><p>Released Aug 28, 2026</p><a>Release v3.10.0</a>
        <h2>3.9.1</h2><p>Released Aug 12, 2026</p><a>Release v3.9.1</a>
        </body></html>
        """;
        var lines = ClientReleaseFeedService.ParseChangelogHtml(html);

        lines.Should().HaveCount(3);
        lines[0].Should().Be("- 3.11.2（发布于 Sep 4, 2026，正式版）");
        lines[1].Should().Contain("3.10.0").And.Contain("Aug 28, 2026");
        lines[2].Should().Contain("3.9.1");
    }

    [Fact]
    public void Changelog_html_ignores_version_like_numbers_without_release_line()
    {
        // 正文里的普通数字（如 "10.0.17763"）后不跟 Released 行，不应被当作版本条目。
        const string html = """
        <html><body><h2>3.11.2</h2><p>Released Sep 4, 2026</p>
        <p>x-os-version 10.0.17763 win32-x64</p><p>runtime node 24</p>
        </body></html>
        """;
        ClientReleaseFeedService.ParseChangelogHtml(html)
            .Should().ContainSingle().Which.Should().Contain("3.11.2");
    }

    [Fact]
    public void Changelog_html_handles_malformed_body()
    {
        ClientReleaseFeedService.ParseChangelogHtml("").Should().BeEmpty();
        ClientReleaseFeedService.ParseChangelogHtml("no versions here").Should().BeEmpty();
    }
}
