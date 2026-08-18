using AITool.Application.Google;
using AITool.Application.Proxy;
using AITool.Infrastructure.Google;
using FluentAssertions;

namespace AITool.ApplicationTests.Google;

/// <summary>
/// Google 账号常量（接入方式/端点/scopes）、额度解析与协议解析器 Gemini 支持的单元测试。
/// </summary>
public sealed class GoogleAccountKindsTests
{
    [Fact]
    public void Normalize_maps_kind_names()
    {
        GoogleAccountKinds.Normalize("GeminiCli").Should().Be(GoogleAccountKinds.GeminiCli);
        GoogleAccountKinds.Normalize("geminicli").Should().Be(GoogleAccountKinds.GeminiCli);
        GoogleAccountKinds.Normalize("Antigravity").Should().Be(GoogleAccountKinds.Antigravity);
        GoogleAccountKinds.Normalize("antigravity").Should().Be(GoogleAccountKinds.Antigravity);
        GoogleAccountKinds.Normalize(null).Should().Be(GoogleAccountKinds.GeminiCli);
        GoogleAccountKinds.Normalize("bogus").Should().Be(GoogleAccountKinds.GeminiCli);
        GoogleAccountKinds.IsValid("Antigravity").Should().BeTrue();
        GoogleAccountKinds.IsValid("GeminiCli").Should().BeTrue();
        GoogleAccountKinds.IsValid("bogus").Should().BeFalse();
    }

    [Fact]
    public void GetBaseUrl_uses_distinct_endpoints()
    {
        // 端点对齐 gcli2api config.py：GeminiCLI→cloudcode-pa，Antigravity→daily-cloudcode-pa。
        GoogleAccountKinds.GetBaseUrl(GoogleAccountKinds.GeminiCli).Should().Be("https://cloudcode-pa.googleapis.com");
        GoogleAccountKinds.GetBaseUrl(GoogleAccountKinds.Antigravity).Should().Be("https://daily-cloudcode-pa.googleapis.com");
    }

    [Fact]
    public void GetScopes_antigravity_includes_extra_scopes()
    {
        var geminiCli = GoogleAccountKinds.GetScopes(GoogleAccountKinds.GeminiCli);
        geminiCli.Should().HaveCount(3);

        var antigravity = GoogleAccountKinds.GetScopes(GoogleAccountKinds.Antigravity);
        antigravity.Should().HaveCount(5);
        antigravity.Should().Contain("https://www.googleapis.com/auth/cclog");
        antigravity.Should().Contain("https://www.googleapis.com/auth/experimentsandconfigs");
    }

    [Fact]
    public void Client_ids_are_separated_per_kind()
    {
        GoogleAccountKinds.GetClientId(GoogleAccountKinds.GeminiCli)
            .Should().NotBe(GoogleAccountKinds.GetClientId(GoogleAccountKinds.Antigravity));
        GoogleAccountKinds.GetClientSecret(GoogleAccountKinds.GeminiCli)
            .Should().NotBe(GoogleAccountKinds.GetClientSecret(GoogleAccountKinds.Antigravity));
    }
}

public sealed class GoogleQuotaParserTests
{
    [Fact]
    public void Parse_maps_remaining_fraction_to_used_percent_windows()
    {
        var raw = """
        {
          "models": {
            "gemini-3-pro-preview": { "quotaInfo": { "remainingFraction": 0.85, "resetTime": "2026-08-20T02:30:00Z" } },
            "claude-sonnet-4-6": { "quotaInfo": { "remainingFraction": 0.05, "resetTime": "2026-08-19T10:00:00Z" } },
            "no-quota-model": { "enabled": true }
          }
        }
        """;

        var windows = GoogleQuotaParser.Parse(raw);
        windows.Should().NotBeNull();
        windows.Should().HaveCount(2);

        var gemini = windows!.Single(w => w.Id == "gemini-3-pro-preview");
        gemini.UsedPercent.Should().BeApproximately(15d, 0.01);
        gemini.ResetAtUtc.Should().NotBeNull();
        gemini.ResetLabel.Should().NotBe("N/A");

        var claude = windows.Single(w => w.Id == "claude-sonnet-4-6");
        claude.UsedPercent.Should().BeApproximately(95d, 0.01);
    }

    [Fact]
    public void Parse_returns_null_when_no_models_or_invalid_json()
    {
        GoogleQuotaParser.Parse("""{ "models": {} }""").Should().BeNull();
        GoogleQuotaParser.Parse("""{ "models": { "a": { "quotaInfo": {} } } }""").Should().BeNull();
        GoogleQuotaParser.Parse("not json").Should().BeNull();
        GoogleQuotaParser.Parse("").Should().BeNull();
    }
}

public sealed class ProxyProtocolResolverGeminiTests
{
    [Fact]
    public void ResolveSiteProtocolType_detects_gemini_site()
    {
        // Gemini 站点：三个 Supports* 全 false，靠 ProtocolType=Gemini 标识。
        ProxyProtocolResolver.ResolveSiteProtocolType(false, false, false, "Gemini")
            .Should().Be(ProxyProtocolResolver.Gemini);

        // 历史行为不受影响：全 false 且无 Gemini 标识仍归为 Responses。
        ProxyProtocolResolver.ResolveSiteProtocolType(false, false, false, null)
            .Should().Be(ProxyProtocolResolver.Responses);
    }

    [Fact]
    public void SupportsProtocol_gemini_only_for_gemini_sites()
    {
        ProxyProtocolResolver.SupportsProtocol("Gemini", false, false, false, "Gemini").Should().BeTrue();
        ProxyProtocolResolver.SupportsProtocol("Gemini", true, true, true, "OpenAI").Should().BeFalse();
    }

    [Fact]
    public void ResolveProtocolForClient_gemini_site_serves_all_client_protocols()
    {
        foreach (var clientProtocol in new[] { ProxyProtocolResolver.OpenAi, ProxyProtocolResolver.Anthropic, ProxyProtocolResolver.Responses })
        {
            ProxyProtocolResolver.ResolveProtocolForClient(clientProtocol, "Gemini", false, false, false, "Gemini")
                .Should().Be(ProxyProtocolResolver.Gemini, $"{clientProtocol} 客户端在 Gemini 站点应桥接到 Gemini");
        }
    }

    [Fact]
    public void ResolveProtocolForClient_non_gemini_sites_unchanged()
    {
        // OpenAI 站点服务 Anthropic 客户端：回落 Anthropic（历史行为）。
        ProxyProtocolResolver.ResolveProtocolForClient("Anthropic", "OpenAI", true, false, false, "OpenAI")
            .Should().Be(ProxyProtocolResolver.OpenAi);
        // Anthropic 站点服务 OpenAI 客户端：转 Anthropic。
        ProxyProtocolResolver.ResolveProtocolForClient("OpenAI", "Anthropic", false, true, false, "Anthropic")
            .Should().Be(ProxyProtocolResolver.Anthropic);
    }

    [Fact]
    public void NormalizeProtocol_includes_gemini()
    {
        ProxyProtocolResolver.NormalizeProtocol("gemini").Should().Be(ProxyProtocolResolver.Gemini);
        ProxyProtocolResolver.NormalizeProtocol("openai").Should().Be(ProxyProtocolResolver.OpenAi);
    }
}

public sealed class GoogleOAuthClientUrlTests
{
    [Fact]
    public void BuildAuthorizeUrl_includes_offline_consent_and_state()
    {
        var client = new GoogleOAuthClient(new HttpClient());
        var session = client.CreateSession();

        var url = client.BuildAuthorizeUrl(GoogleAccountKinds.Antigravity, session);

        url.Should().StartWith("https://accounts.google.com/o/oauth2/auth?");
        url.Should().Contain("response_type=code");
        url.Should().Contain("access_type=offline");
        url.Should().Contain("prompt=consent");
        url.Should().Contain("include_granted_scopes=true");
        url.Should().Contain($"state={Uri.EscapeDataString(session.State)}");
        url.Should().Contain($"client_id={Uri.EscapeDataString(GoogleAccountKinds.GetClientId(GoogleAccountKinds.Antigravity))}");
        url.Should().Contain("scope=");
        // Antigravity 独有 scope 需出现在授权 URL。
        url.Should().Contain(Uri.EscapeDataString("https://www.googleapis.com/auth/cclog"));
    }

    [Fact]
    public void BuildAuthorizeUrl_kind_scopes_separated()
    {
        var client = new GoogleOAuthClient(new HttpClient());
        var session = client.CreateSession();

        var geminiCliUrl = client.BuildAuthorizeUrl(GoogleAccountKinds.GeminiCli, session);
        geminiCliUrl.Should().NotContain(Uri.EscapeDataString("https://www.googleapis.com/auth/cclog"));
    }

    [Fact]
    public void CreateSession_state_is_url_safe_and_unique()
    {
        var client = new GoogleOAuthClient(new HttpClient());
        var first = client.CreateSession();
        var second = client.CreateSession();
        first.State.Should().NotBe(second.State);
        first.State.Should().NotContainAny("+", "/", "=");
        first.IsExpired.Should().BeFalse();
    }
}

public sealed class GoogleModelFetcherStaticListTests
{
    [Fact]
    public async Task GeminiCli_fetch_returns_shared_static_catalog()
    {
        var fetcher = new GoogleModelFetcher(new HttpClient());
        var models = await fetcher.FetchAsync(GoogleAccountKinds.GeminiCli, "token", CancellationToken.None);
        models.Select(m => m.Slug).Should().BeEquivalentTo(GoogleAccountKinds.GeminiCliModels);
    }
}

public sealed class GoogleModelFetcherDynamicListTests
{
    [Fact]
    public async Task Antigravity_fetch_keeps_slug_and_reads_display_name()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"models":{"claude-sonnet-4-6":{"displayName":"Claude Sonnet 4.6"},"gemini-3-pro":{"label":"Gemini 3 Pro"}}}
                """,
                System.Text.Encoding.UTF8,
                "application/json")
        });
        var fetcher = new GoogleModelFetcher(new HttpClient(handler));

        var models = await fetcher.FetchAsync(GoogleAccountKinds.Antigravity, "token", CancellationToken.None);

        models.Should().Contain(("claude-sonnet-4-6", "Claude Sonnet 4.6"));
        models.Should().Contain(("gemini-3-pro", "Gemini 3 Pro"));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
