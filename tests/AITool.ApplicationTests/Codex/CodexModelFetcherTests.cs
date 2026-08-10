using System.Net;
using System.Net.Http;
using AITool.Infrastructure.Codex;
using FluentAssertions;

namespace AITool.ApplicationTests.Codex;

public sealed class CodexModelFetcherTests
{
    [Fact]
    public async Task FetchAsync_supports_id_based_model_entries()
    {
        using var httpClient = new HttpClient(new StubHandler(
            "{\"models\":[{\"id\":\"gpt-5.6-codex\",\"name\":\"GPT-5.6 Codex\"}]}"));
        var fetcher = new CodexModelFetcher(httpClient);

        var models = await fetcher.FetchAsync("access-token", "account-id", default);

        models.Should().ContainSingle(model =>
            model.Slug == "gpt-5.6-codex"
            && model.DisplayName == "GPT-5.6 Codex");
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
