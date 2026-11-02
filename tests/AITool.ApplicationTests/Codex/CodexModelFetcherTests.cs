using System.Net;
using System.Net.Http;
using AITool.Application.Codex;
using AITool.Application.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Codex;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FluentAssertions;

namespace AITool.ApplicationTests.Codex;

public sealed class CodexModelFetcherTests
{
    [Fact]
    public async Task FetchAsync_supports_id_based_model_entries()
    {
        using var httpClient = new HttpClient(new StubHandler(
            "{\"models\":[{\"id\":\"gpt-5.6-codex\",\"name\":\"GPT-5.6 Codex\"}]}"));
        var fetcher = new CodexModelFetcher(
            httpClient,
            new CodexClientVersionResolver(
                new StubProfileCatalog(),
                Options.Create(new CodexUpstreamOptions { ClientVersion = "0.153.3" }),
                NullLogger<CodexClientVersionResolver>.Instance),
            NullLogger<CodexModelFetcher>.Instance);

        var models = await fetcher.FetchAsync("access-token", "account-id", default);

        models.Should().ContainSingle(model =>
            model.Slug == "gpt-5.6-codex"
            && model.DisplayName == "GPT-5.6 Codex");
    }

    [Fact]
    public async Task FetchAsync_preserves_401_when_response_body_is_unreadable()
    {
        using var httpClient = new HttpClient(new BrokenBodyHandler());
        var fetcher = new CodexModelFetcher(httpClient,
            new CodexClientVersionResolver(new StubProfileCatalog(),
                Options.Create(new CodexUpstreamOptions { ClientVersion = "0.153.3" }),
                NullLogger<CodexClientVersionResolver>.Instance),
            NullLogger<CodexModelFetcher>.Instance);
        await Assert.ThrowsAsync<ModelAccessTokenUnauthorizedException>(
            () => fetcher.FetchAsync("access-token", "account-id", default));
    }

    private sealed class BrokenBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new BrokenBody() });
    }

    private sealed class BrokenBody : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.FromException(new IOException("broken response body"));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    /// <summary>
    /// 恒返回 null 的模板库桩：验证解析失败时按配置兜底、请求照常发出。
    /// </summary>
    private sealed class StubProfileCatalog : IHeaderProfileCatalogService
    {
        public Task<IReadOnlyList<HeaderProfile>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<HeaderProfile>>([]);

        public Task<HeaderProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<HeaderProfile?>(null);

        public Task<HeaderProfile?> GetByKeyAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult<HeaderProfile?>(null);

        public Task<IReadOnlyDictionary<string, string>> GetActiveProfilesDictionaryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<HeaderProfile> CreateAsync(HeaderProfile profile, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeaderProfile?> UpdateAsync(Guid id, Action<HeaderProfile> updateAction, CancellationToken cancellationToken = default)
            => Task.FromResult<HeaderProfile?>(null);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<HeaderProfile>> ResetBuiltInsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<HeaderProfile>>(new List<HeaderProfile>());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public StubHandler(string responseBody)
        {
            _responseBody = responseBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody)
            });
        }
    }
}
