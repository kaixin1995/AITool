using System.Net;
using AITool.Application.Networking;
using AITool.Application.Proxy;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.ApplicationTests.Proxy;

public sealed class ForwardCredentialSendTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_attempt_uses_live_token_and_releases_gate_before_refresh(bool streaming)
    {
        var liveToken = "fresh-one";
        var resolver = new FakeCredentials(() => liveToken);
        var seen = new List<string?>();
        var handler = new Handler(request =>
        {
            seen.Add(request.Headers.Authorization?.Parameter);
            request.Headers.Contains("x-api-key").Should().BeFalse();
            return seen.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("unauthorized") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}") };
        });
        var service = new ProxyForwardService(new HttpClient(handler), NullLogger<ProxyForwardService>.Instance,
            networkPolicyResolver: new FakeNetwork(), credentialResolver: resolver);
        var request = new ProxyForwardRequest
        {
            SiteId = Guid.NewGuid(), ManagedSource = "device_oauth",
            AccountIdentity = new AccountIdentity("device-oauth", Guid.NewGuid()),
            TargetBaseUrl = "http://127.0.0.1:12345", TargetApiKey = "stale-site-key",
            RequestBody = "{}", RequestTimeoutSeconds = 10,
            ForwardHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer attacker", ["x-api-key"] = "attacker"
            },
            RefreshTargetApiKeyAsync = (stale, _) =>
            {
                stale.Should().Be("fresh-one");
                resolver.Active.Should().Be(0);
                liveToken = "fresh-two";
                return Task.FromResult<string?>("ignored-callback-result");
            }
        };

        if (streaming)
            await service.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask);
        else
            await service.ForwardAsync(request);

        seen.Should().Equal("fresh-one", "fresh-two");
        resolver.Calls.Should().Be(2);
        resolver.Active.Should().Be(0);
        request.TargetApiKey.Should().Be("stale-site-key");
    }

    [Theory]
    [InlineData(false, 401, false)]
    [InlineData(true, 401, false)]
    [InlineData(false, 407, false)]
    [InlineData(true, 407, false)]
    [InlineData(false, 401, true)]
    [InlineData(true, 401, true)]
    public async Task Only_final_upstream_401_invalidates_the_sent_credential(bool streaming, int status, bool refresh)
    {
        var seen = new List<string>();
        var invalidated = new List<(string, object?)>();
        var snapshot = new object();
        var handler = new Handler(request =>
        {
            seen.Add(request.Headers.Authorization?.Parameter ?? "");
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("rejected") };
        });
        var resolver = new SnapshotCredentials(snapshot);
        var service = new ProxyForwardService(new HttpClient(handler), NullLogger<ProxyForwardService>.Instance,
            networkPolicyResolver: new FakeNetwork(), credentialResolver: resolver);
        var request = new ProxyForwardRequest
        {
            SiteId = Guid.NewGuid(), TargetBaseUrl = "http://127.0.0.1:12345", TargetApiKey = "first",
            RequestBody = "{}", RequestTimeoutSeconds = 10,
            RefreshTargetApiKeyAsync = refresh ? (_, _) => Task.FromResult<string?>("second") : null,
            InvalidateTargetCredentialAsync = (token, state, _) => { invalidated.Add((token, state)); return Task.CompletedTask; }
        };
        var result = streaming ? await service.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask)
            : await service.ForwardAsync(request);
        result.StatusCode.Should().Be(status);
        if (status == 401)
        {
            invalidated.Should().ContainSingle();
            invalidated[0].Item1.Should().Be(refresh ? "second" : "first");
            invalidated[0].Item2.Should().BeSameAs(snapshot);
        }
        else invalidated.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upstream_401_with_null_refresh_invalidates_sent_snapshot(bool streaming)
    {
        var snapshot = new object();
        var rejected = new List<(string, object?)>();
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("rejected") });
        var service = new ProxyForwardService(new HttpClient(handler), NullLogger<ProxyForwardService>.Instance,
            networkPolicyResolver: new FakeNetwork(), credentialResolver: new SnapshotCredentials(snapshot));
        var request = new ProxyForwardRequest
        {
            SiteId = Guid.NewGuid(), TargetBaseUrl = "https://example.test", TargetApiKey = "sent",
            RequestBody = "{}", RequestTimeoutSeconds = 10,
            RefreshTargetApiKeyAsync = (_, _) => Task.FromResult<string?>(null),
            InvalidateTargetCredentialAsync = (token, state, _) => { rejected.Add((token, state)); return Task.CompletedTask; }
        };
        var result = streaming ? await service.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask) : await service.ForwardAsync(request);
        result.StatusCode.Should().Be(401);
        handler.Calls.Should().Be(1);
        rejected.Should().ContainSingle();
        rejected[0].Should().Be(("sent", snapshot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Network_failure_without_upstream_response_does_not_invalidate(bool streaming)
    {
        var invalidations = 0;
        var service = new ProxyForwardService(new HttpClient(new Handler(_ => throw new HttpRequestException("offline"))),
            NullLogger<ProxyForwardService>.Instance, networkPolicyResolver: new FakeNetwork(), credentialResolver: new SnapshotCredentials(new object()));
        var request = new ProxyForwardRequest
        {
            SiteId = Guid.NewGuid(), TargetBaseUrl = "https://example.test", TargetApiKey = "sent", RequestBody = "{}", RequestTimeoutSeconds = 10,
            InvalidateTargetCredentialAsync = (_, _, _) => { invalidations++; return Task.CompletedTask; }
        };
        var result = streaming ? await service.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask) : await service.ForwardAsync(request);
        result.Success.Should().BeFalse();
        invalidations.Should().Be(0);
    }

    private sealed class SnapshotCredentials(object snapshot) : IForwardCredentialResolver
    {
        public Task<ForwardCredentialLease> ResolveAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ForwardCredentialLease(request.TargetApiKey, credentialSnapshot: snapshot));
    }

    [Fact]
    public async Task Send_failure_releases_gate()
    {
        var resolver = new FakeCredentials(() => "live");
        var handler = new Handler(_ => throw new HttpRequestException("fake failure"));
        var service = new ProxyForwardService(new HttpClient(handler), NullLogger<ProxyForwardService>.Instance,
            networkPolicyResolver: new FakeNetwork(), credentialResolver: resolver);
        var result = await service.ForwardAsync(new ProxyForwardRequest
        {
            SiteId = Guid.NewGuid(), TargetBaseUrl = "http://127.0.0.1:12345", TargetApiKey = "stale",
            RequestBody = "{}", RequestTimeoutSeconds = 10
        });
        result.Success.Should().BeFalse();
        resolver.Active.Should().Be(0);
    }

    [Fact]
    public async Task Resolver_failure_blocks_without_sending()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        var service = new ProxyForwardService(new HttpClient(handler), NullLogger<ProxyForwardService>.Instance,
            networkPolicyResolver: new FakeNetwork(), credentialResolver: new FailingCredentials());
        var result = await service.ForwardAsync(new ProxyForwardRequest
        {
            SiteId = Guid.NewGuid(), TargetBaseUrl = "http://127.0.0.1:12345", TargetApiKey = "stale",
            RequestBody = "{}", RequestTimeoutSeconds = 10, RetryCount = 3
        });
        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(503);
        handler.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Resolver_and_transport_exceptions_never_leak_secrets(bool streaming, bool transport)
    {
        const string secret = "password_secret_token_123";
        var logger = new RecordingLogger();
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        var service = new ProxyForwardService(new HttpClient(handler), logger,
            networkTransport: new SecretTransport(secret), networkPolicyResolver: new SecretNetwork(secret, transport),
            credentialResolver: new FakeCredentials(() => "live"));
        var request = new ProxyForwardRequest
        {
            SiteId = Guid.NewGuid(), TargetBaseUrl = "http://127.0.0.1:12345", TargetApiKey = "stale",
            RequestBody = "{}", RequestTimeoutSeconds = 10
        };
        var result = streaming ? await service.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask)
            : await service.ForwardAsync(request);
        result.StatusCode.Should().Be(503);
        result.ErrorMessage.Should().NotContain(secret);
        logger.Records.Should().NotContain(secret);
        handler.Calls.Should().Be(0);
    }

    private sealed class RecordingLogger : ILogger<ProxyForwardService>
    {
        public string Records = string.Empty;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Records += formatter(state, exception) + exception;
    }

    private sealed class SecretNetwork(string secret, bool transport) : IForwardNetworkPolicyResolver
    {
        public Task<ForwardNetworkPolicyResolution> ResolveAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
        {
            if (!transport) throw new InvalidOperationException(secret);
            return Task.FromResult(new ForwardNetworkPolicyResolution(ResolvedNetworkConfiguration.Direct, "test"));
        }
    }

    private sealed class SecretTransport(string secret) : INetworkTransport
    {
        public ValueTask<INetworkTransportLease> AcquireAsync(ResolvedNetworkConfiguration configuration, string isolationScope,
            string purpose, CancellationToken cancellationToken = default) => throw new InvalidOperationException(secret);
    }

    private sealed class FakeNetwork : IForwardNetworkPolicyResolver
    {
        public Task<ForwardNetworkPolicyResolution> ResolveAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ForwardNetworkPolicyResolution(ResolvedNetworkConfiguration.Legacy, "test", true));
    }

    private sealed class FakeCredentials(Func<string> token) : IForwardCredentialResolver
    {
        public int Calls { get; private set; }
        public int Active { get; private set; }
        public Task<ForwardCredentialLease> ResolveAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Active++;
            return Task.FromResult(new ForwardCredentialLease(token(), true, new Release(() => Active--)));
        }
    }

    private sealed class FailingCredentials : IForwardCredentialResolver
    {
        public Task<ForwardCredentialLease> ResolveAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
            => throw new NetworkPolicyException("bad_owner", "Invalid owner");
    }

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(callback(request));
        }
    }
}
