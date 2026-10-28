using System.Net;
using System.Text;
using AITool.Application.OAuth;
using AITool.Infrastructure.OAuth;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.OAuth;

/// <summary>
/// OAuth model directory requests use only a stub handler: no test sends a credential to an upstream service.
/// </summary>
public sealed class OAuthRemoteModelFetchTests
{
    private static readonly DeviceOAuthTokenSet Token = new()
    {
        AccessToken = "stub-session-token",
        RefreshToken = "stub-refresh-token",
    };

    [Fact]
    public async Task Copilot_fetches_account_models_from_chat_base_with_editor_headers_and_filters_non_chat_models()
    {
        var handler = new StubHandler((request, _) => Task.FromResult(JsonResponse("""
            {"data":[
              {"id":"gpt-chat","name":"GPT Chat","model_picker_enabled":true,"capabilities":{"type":"chat"}},
              {"id":"claude-chat","name":"Claude Chat","model_picker_category":"versatile","policy":{"state":"disabled","terms":"Accept terms"},"capabilities":{"type":"chat"}},
              {"id":"embedding","name":"Embedding","model_picker_enabled":true,"capabilities":{"type":"embedding"}},
              {"id":"hidden","model_picker_enabled":false,"policy":{"state":"disabled"},"capabilities":{"type":"chat"}},
              {"id":"exec-agent-internal","model_picker_enabled":true,"capabilities":{"type":"chat"}}
            ]}
            """)));
        using var client = new HttpClient(handler);
        var vendor = new CopilotOAuthVendor(client);

        vendor.SupportsModelFetch.Should().BeTrue();
        var models = await vendor.FetchModelsAsync(Token, "{\"chatBaseUrl\":\"https://copilot-api.acme.ghe.com/\"}", CancellationToken.None);

        models.Should().BeEquivalentTo(
            [new DeviceOAuthRemoteModel("gpt-chat", "GPT Chat"), new DeviceOAuthRemoteModel("claude-chat", "Claude Chat")],
            options => options.WithStrictOrdering());
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.Should().Be("https://copilot-api.acme.ghe.com/models");
        request.Headers["Authorization"].Should().Be("Bearer stub-session-token");
        request.Headers["Accept"].Should().Contain("application/json");
        request.Headers["Editor-Version"].Should().Be("vscode/1.140.0");
        request.Headers["Editor-Plugin-Version"].Should().Be("copilot-chat/0.68.0");
        request.Headers["Copilot-Integration-Id"].Should().Be("vscode-chat");
        request.Headers["User-Agent"].Should().Be("GitHubCopilotChat/0.68.0");
        request.Headers["X-GitHub-Api-Version"].Should().Be("2026-01-09");
    }

    [Fact]
    public async Task Copilot_uses_the_fixed_public_api_when_extra_json_is_invalid_or_untrusted()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse("""
            {"data":[{"id":"gpt-4.1","model_picker_enabled":true,"capabilities":{"type":"chat"}}]}
            """)));
        using var client = new HttpClient(handler);
        var vendor = new CopilotOAuthVendor(client);

        await vendor.FetchModelsAsync(Token, "{\"chatBaseUrl\":\"https://example.invalid\"}", CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.Uri.Should().Be("https://api.githubcopilot.com/models");
    }

    [Theory]
    [InlineData("{\"data\":[],\"has_more\":true}")]
    [InlineData("{\"data\":[")]
    [InlineData("{\"models\":[]}")]
    [InlineData("{\"data\":[]}")]
    public async Task Copilot_rejects_paged_malformed_missing_and_empty_model_lists(string body)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(body)));
        using var client = new HttpClient(handler);
        var vendor = new CopilotOAuthVendor(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => vendor.FetchModelsAsync(Token, null, CancellationToken.None));
    }

    [Fact]
    public async Task Copilot_propagates_upstream_failure_and_cancellation()
    {
        var failureHandler = new StubHandler((_, _) => Task.FromResult(JsonResponse("{}", HttpStatusCode.Forbidden)));
        using (var client = new HttpClient(failureHandler))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new CopilotOAuthVendor(client).FetchModelsAsync(Token, null, CancellationToken.None));
            ex.Message.Should().Contain("403");
        }

        var blockedHandler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return JsonResponse("{}");
        });
        using var blockedClient = new HttpClient(blockedHandler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CopilotOAuthVendor(blockedClient).FetchModelsAsync(Token, null, cancellation.Token));
    }

    [Theory]
    [InlineData(false, "https://agent.minimax.cn/mavis/api/v1/models?region=cn&buildEnv=prod", "cn")]
    [InlineData(true, "https://agent.minimax.io/mavis/api/v1/models?region=en&buildEnv=prod", "en")]
    public async Task MiniMax_fetches_cn_and_global_live_catalog_in_mcode_order(bool global, string expectedUrl, string expectedRegion)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse("""
            {"providers":[
              {"providerId":"other","config":{"models":{"wrong":{"name":"Wrong"}}}},
              {"providerId":"minimax","config":{"model_order":["model-b","missing","model-a","model-b"],"models":{
                "model-a":{"name":"Model A"},"model-b":{"name":"Model B"},"model-c":{},"bad":null
              }}}
            ]}
            """)));
        using var client = new HttpClient(handler);
        var vendor = new MiniMaxOAuthVendor(client, NullLogger<MiniMaxOAuthVendor>.Instance, global);

        vendor.SupportsModelFetch.Should().BeTrue();
        vendor.VendorKey.Should().Be(global ? "minimax-global" : "minimax-cn");
        var models = await vendor.FetchModelsAsync(Token, null, CancellationToken.None);

        models.Should().BeEquivalentTo(
            [new DeviceOAuthRemoteModel("model-b", "Model B"), new DeviceOAuthRemoteModel("model-a", "Model A"), new DeviceOAuthRemoteModel("model-c", "model-c")],
            options => options.WithStrictOrdering());
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.Should().Be(expectedUrl);
        request.Headers["Authorization"].Should().Be("Bearer stub-session-token");
        request.Headers["Accept"].Should().Contain("application/json");
        expectedUrl.Should().Contain("region=" + expectedRegion);
    }

    [Fact]
    public async Task MiniMax_refetches_the_public_catalog_without_the_token_when_refused()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (request.Headers.Contains("Authorization"))
            {
                return Task.FromResult(JsonResponse("{}", HttpStatusCode.Unauthorized));
            }
            return Task.FromResult(JsonResponse("""
                {"providers":[{"providerId":"minimax","config":{"models":{"model-a":{"name":"Model A"}}}}]}
                """));
        });
        using var client = new HttpClient(handler);
        var vendor = new MiniMaxOAuthVendor(client, NullLogger<MiniMaxOAuthVendor>.Instance);

        var models = await vendor.FetchModelsAsync(Token, null, CancellationToken.None);

        models.Should().ContainSingle().Which.Slug.Should().Be("model-a");
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Headers.Should().ContainKey("Authorization");
        handler.Requests[1].Headers.Should().NotContainKey("Authorization");
    }

    [Fact]
    public async Task MiniMax_can_fetch_the_public_catalog_without_authentication()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse("""
            {"providers":[{"providerId":"minimax","config":{"models":{"model-a":{}}}}]}
            """)));
        using var client = new HttpClient(handler);
        var vendor = new MiniMaxOAuthVendor(client, NullLogger<MiniMaxOAuthVendor>.Instance);

        await vendor.FetchModelsAsync(new DeviceOAuthTokenSet(), null, CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.Headers.Should().NotContainKey("Authorization");
    }

    [Theory]
    [InlineData("{\"providers\":[{\"providerId\":\"minimax\",\"config\":{\"models\":{}}}]}")]
    [InlineData("{\"providers\":[{\"providerId\":\"minimax\",\"config\":{\"models\":{\"x\":null}}}]}")]
    [InlineData("{\"providers\":[{\"providerId\":\"minimax\",\"config\":{\"models\":{\"x\":{}}}}],\"has_more\":true}")]
    [InlineData("not-json")]
    [InlineData("{\"providers\":[]}")]
    public async Task MiniMax_rejects_paged_malformed_empty_and_missing_catalogs(string body)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(body)));
        using var client = new HttpClient(handler);
        var vendor = new MiniMaxOAuthVendor(client, NullLogger<MiniMaxOAuthVendor>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => vendor.FetchModelsAsync(Token, null, CancellationToken.None));
    }

    [Theory]
    [InlineData(false, "https://agent.minimax.cn/mavis/api/v1/models?region=cn&buildEnv=prod")]
    [InlineData(true, "https://agent.minimax.io/mavis/api/v1/models?region=en&buildEnv=prod")]
    public async Task MiniMax_propagates_upstream_failure_and_cancellation(bool global, string expectedUrl)
    {
        var failureHandler = new StubHandler((_, _) => Task.FromResult(JsonResponse("{}", HttpStatusCode.ServiceUnavailable)));
        using (var client = new HttpClient(failureHandler))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new MiniMaxOAuthVendor(client, NullLogger<MiniMaxOAuthVendor>.Instance, global).FetchModelsAsync(Token, null, CancellationToken.None));
            ex.Message.Should().Contain("503");
        }
        failureHandler.Requests.Should().ContainSingle().Which.Uri.Should().Be(expectedUrl);

        var blockedHandler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return JsonResponse("{}");
        });
        using var blockedClient = new HttpClient(blockedHandler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MiniMaxOAuthVendor(blockedClient, NullLogger<MiniMaxOAuthVendor>.Instance, global).FetchModelsAsync(Token, null, cancellation.Token));
    }

    [Fact]
    public async Task Claude_fetches_magpies_public_anthropic_catalog_without_sending_subscription_token()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse("""
            {"anthropic":{"models":{
              "claude-sonnet":{"id":"claude-sonnet","name":"Claude Sonnet"},
              "claude-haiku":{"name":"Claude Haiku","modalities":{"output":["text"]}},
              "image-only":{"name":"Image","modalities":{"output":["image"]}},
              "claude-embed":{"name":"Embed","modalities":{"output":["text"]}},
              "claude-audio":{"name":"Audio","modalities":{"output":["text"]}},
              "other-provider/model":{"name":"Other"}
            }}}
            """)));
        using var client = new HttpClient(handler);
        var vendor = new ClaudeOAuthVendor(client);

        vendor.SupportsModelFetch.Should().BeTrue();
        var models = await vendor.FetchModelsAsync(Token, null, CancellationToken.None);

        models.Should().BeEquivalentTo(
            [new DeviceOAuthRemoteModel("claude-sonnet", "Claude Sonnet"), new DeviceOAuthRemoteModel("claude-haiku", "Claude Haiku"), new DeviceOAuthRemoteModel("other-provider/model", "Other")],
            options => options.WithStrictOrdering());
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.Should().Be("https://models.dev/api.json");
        request.Headers["Accept"].Should().Contain("application/json");
        request.Headers["User-Agent"].Should().Be("AITool");
        request.Headers.Should().NotContainKey("Authorization");
        request.Headers.Should().NotContainKey("x-api-key");
    }

    [Theory]
    [InlineData("{\"anthropic\":{\"models\":{}}}")]
    [InlineData("{\"anthropic\":{\"models\":{\"image-only\":{\"modalities\":{\"output\":[\"image\"]}}}}}")]
    [InlineData("not-json")]
    [InlineData("{\"other\":{\"models\":{}}}")]
    public async Task Claude_rejects_paged_malformed_empty_and_missing_public_catalogs(string body)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(body)));
        using var client = new HttpClient(handler);
        var vendor = new ClaudeOAuthVendor(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => vendor.FetchModelsAsync(Token, null, CancellationToken.None));
    }

    [Fact]
    public async Task Claude_propagates_upstream_failure_and_cancellation_without_authentication()
    {
        var failureHandler = new StubHandler((_, _) => Task.FromResult(JsonResponse("{}", HttpStatusCode.BadGateway)));
        using (var client = new HttpClient(failureHandler))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new ClaudeOAuthVendor(client).FetchModelsAsync(Token, null, CancellationToken.None));
            ex.Message.Should().Contain("502");
        }
        failureHandler.Requests.Should().ContainSingle().Which.Headers.Should().NotContainKey("Authorization");

        var blockedHandler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return JsonResponse("{}");
        });
        using var blockedClient = new HttpClient(blockedHandler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ClaudeOAuthVendor(blockedClient).FetchModelsAsync(Token, null, cancellation.Token));
    }

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase)));
            return respond(request, cancellationToken);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, IReadOnlyDictionary<string, string> Headers);
}
