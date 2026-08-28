using System.Net;
using AITool.Application.Proxy;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.Core.IntegrationTests.Proxy;

public sealed class ProxyForwardAutoDisableTests
{
    [Fact]
    public async Task Forbidden_response_disables_target_and_skips_same_target_retries()
    {
        var handler = new ForbiddenHandler();
        using var httpClient = new HttpClient(handler);
        var service = new ProxyForwardService(httpClient, NullLogger<ProxyForwardService>.Instance);
        var disableCallCount = 0;

        var result = await service.ForwardAsync(new ProxyForwardRequest
        {
            TargetBaseUrl = "https://example.com",
            TargetPath = "/v1/chat/completions",
            TargetApiKey = "token",
            ProtocolType = "OpenAI",
            TargetModelName = "model",
            RequestBody = "{\"model\":\"model\",\"messages\":[]}",
            RequestTimeoutSeconds = 5,
            RetryCount = 3,
            DisableTargetCredentialAsync = _ =>
            {
                disableCallCount++;
                return Task.CompletedTask;
            }
        });

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
        disableCallCount.Should().Be(1);
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task Streaming_forbidden_response_also_disables_target_once()
    {
        var handler = new ForbiddenHandler();
        using var httpClient = new HttpClient(handler);
        var service = new ProxyForwardService(httpClient, NullLogger<ProxyForwardService>.Instance);
        var disableCallCount = 0;

        var result = await service.ForwardStreamingAsync(
            new ProxyForwardRequest
            {
                TargetBaseUrl = "https://example.com",
                TargetPath = "/v1/chat/completions",
                TargetApiKey = "token",
                ProtocolType = "OpenAI",
                TargetModelName = "model",
                RequestBody = "{\"model\":\"model\",\"messages\":[],\"stream\":true}",
                EnableStreaming = true,
                RequestTimeoutSeconds = 5,
                RetryCount = 3,
                DisableTargetCredentialAsync = _ =>
                {
                    disableCallCount++;
                    return Task.CompletedTask;
                }
            },
            (_, _) => Task.CompletedTask);

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
        disableCallCount.Should().Be(1);
        handler.RequestCount.Should().Be(1);
    }

    private sealed class ForbiddenHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"error\":{\"code\":403,\"status\":\"PERMISSION_DENIED\"}}")
            });
        }
    }
}
