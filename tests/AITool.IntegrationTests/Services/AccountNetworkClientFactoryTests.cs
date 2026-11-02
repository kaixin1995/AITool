using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using AITool.Application.Networking;
using AITool.Domain.Operations;
using AITool.Infrastructure.Networking;
using AITool.Web.Services;

namespace AITool.IntegrationTests.Services;

public sealed class AccountNetworkClientFactoryTests
{
    private static readonly AccountIdentity Account = new("codex", Guid.NewGuid());

    [Fact]
    public async Task Clones_message_body_headers_options_and_version_without_mutating_shared_defaults()
    {
        var resolver = new RecordingResolver();
        HttpRequestMessage? received = null;
        string? body = null;
        using var transport = new RecordingTransport(async (request, ct) =>
        {
            received = request;
            body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("response") };
        });
        using var client = new AccountNetworkClientFactory(resolver, transport).CreateForAccount(Account, "token");
        using var original = new HttpRequestMessage(HttpMethod.Post, "https://vendor.invalid/token")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new StringContent("{\"token\":\"private\"}", Encoding.UTF8, "application/json")
        };
        original.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "account-token");
        original.Content.Headers.Add("X-Content-Metadata", "keep");
        original.Options.Set(new HttpRequestOptionsKey<string>("operation"), "value");
        using var response = await client.SendAsync(original, HttpCompletionOption.ResponseHeadersRead);
        Assert.NotSame(original, received);
        Assert.Equal(original.Method, received!.Method);
        Assert.Equal(original.RequestUri, received.RequestUri);
        Assert.Equal(original.Version, received.Version);
        Assert.Equal(original.VersionPolicy, received.VersionPolicy);
        Assert.Equal(original.Headers.Authorization, received.Headers.Authorization);
        Assert.Equal("{\"token\":\"private\"}", body);
        Assert.NotSame(original.Content, received.Content);
        Assert.Equal(original.Content.Headers.ContentType, received.Content!.Headers.ContentType);
        Assert.Equal("keep", Assert.Single(received.Content.Headers.GetValues("X-Content-Metadata")));
        Assert.True(received.Options.TryGetValue(new HttpRequestOptionsKey<string>("operation"), out var option));
        Assert.Equal("value", option);
        Assert.Same(original, response.RequestMessage);
        Assert.Empty(client.DefaultRequestHeaders);
        Assert.Empty(transport.Client.DefaultRequestHeaders);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.Equal(1, transport.Active);
        Assert.Equal("response", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, transport.Active);
        Assert.Equal("{\"token\":\"private\"}", await original.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Each_account_send_resolves_latest_policy_and_uses_provider_scoped_identity()
    {
        var resolver = new RecordingResolver();
        using var transport = new RecordingTransport();
        var factory = new AccountNetworkClientFactory(resolver, transport);
        using var client = factory.CreateForAccount(Account, "quota");
        using var first = await client.GetAsync("https://vendor.invalid/first");
        resolver.Current = ResolvedNetworkConfiguration.Direct;
        using var next = await client.GetAsync("https://vendor.invalid/next");
        Assert.Equal(2, resolver.Accounts.Count);
        Assert.All(resolver.Accounts, account => Assert.Equal(Account, account));
        Assert.Equal(new[] { NetworkMode.Legacy, NetworkMode.Direct }, transport.Acquisitions.Select(a => a.Configuration.Mode));
        Assert.All(transport.Acquisitions, acquisition =>
        {
            Assert.Equal($"codex:{Account.AccountId:D}", acquisition.Scope);
            Assert.Equal("quota", acquisition.Purpose);
        });
        using var otherProvider = factory.CreateForAccount(new AccountIdentity("google", Account.AccountId), "quota");
        using var third = await otherProvider.GetAsync("https://vendor.invalid/third");
        Assert.Equal($"google:{Account.AccountId:D}", transport.Acquisitions.Last().Scope);
    }

    [Theory]
    [InlineData("proxy_disabled")]
    [InlineData("profile_revision_changed")]
    public async Task Pending_multistep_login_keeps_pin_and_revalidates_before_next_send(string code)
    {
        var selection = new AccountNetworkSelection(NetworkMode.ProxyProfile, Guid.NewGuid());
        var operation = new NetworkOperationContext(null, "pending:opaque-operation", selection, 42);
        var resolver = new RecordingResolver();
        using var transport = new RecordingTransport();
        using var client = new AccountNetworkClientFactory(resolver, transport).CreateForOperation(operation, "oauth");
        using var first = await client.PostAsync("https://vendor.invalid/device", new StringContent("step-one"));
        resolver.Failure = new NetworkPolicyException(code, "Selected proxy is unavailable.");
        var error = await Assert.ThrowsAsync<NetworkPolicyException>(() => client.PostAsync(
            "https://vendor.invalid/token", new StringContent("step-two")));
        Assert.Equal(code, error.Code);
        Assert.Equal(2, resolver.Operations.Count);
        Assert.All(resolver.Operations, actual => Assert.Same(operation, actual));
        Assert.Empty(resolver.Accounts);
        Assert.Single(transport.Acquisitions);
        Assert.Equal("pending:opaque-operation", transport.Acquisitions.Single().Scope);
        Assert.Equal(1, transport.Sends);
    }

    [Fact]
    public async Task Operation_with_account_does_not_freeze_stale_selection()
    {
        var operation = new NetworkOperationContext(Account, "old-pending-key",
            new AccountNetworkSelection(NetworkMode.Legacy));
        var resolver = new RecordingResolver { Current = ResolvedNetworkConfiguration.Direct };
        using var transport = new RecordingTransport();
        using var client = new AccountNetworkClientFactory(resolver, transport).CreateForOperation(operation, "refresh");
        using var response = await client.GetAsync("https://vendor.invalid/refresh");
        Assert.Equal(Account, Assert.Single(resolver.Accounts));
        Assert.Empty(resolver.Operations);
        Assert.Equal(NetworkMode.Direct, transport.Acquisitions.Single().Configuration.Mode);
        Assert.Equal($"codex:{Account.AccountId:D}", transport.Acquisitions.Single().Scope);
    }

    [Fact]
    public async Task Headers_only_response_keeps_lease_until_disposal_even_if_outer_client_is_disposed()
    {
        var content = new TrackedContent();
        using var transport = new RecordingTransport((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        var client = new AccountNetworkClientFactory(new RecordingResolver(), transport).CreateForAccount(Account, "stream");
        var response = await client.GetAsync("https://vendor.invalid/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(1, transport.Active);
        client.Dispose();
        Assert.Equal(1, transport.Active);
        response.Dispose();
        response.Dispose();
        Assert.Equal(0, transport.Active);
        Assert.Equal(1, transport.Releases);
        Assert.True(content.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stream_EOF_or_disposal_releases_once_without_waiting_for_response_disposal(bool readToEnd)
    {
        using var transport = new RecordingTransport();
        using var client = new AccountNetworkClientFactory(new RecordingResolver(), transport).CreateForAccount(Account, "stream");
        using var response = await client.GetAsync("https://vendor.invalid/stream", HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync();
        Assert.Equal(1, transport.Active);
        if (readToEnd)
        {
            var buffer = new byte[16];
            while (await stream.ReadAsync(buffer) != 0) { }
            Assert.Equal(0, transport.Active);
        }
        await stream.DisposeAsync();
        response.Dispose();
        Assert.Equal(0, transport.Active);
        Assert.Equal(1, transport.Releases);
    }

    [Fact]
    public async Task Zero_length_read_does_not_release_active_stream()
    {
        using var transport = new RecordingTransport();
        using var client = new AccountNetworkClientFactory(new RecordingResolver(), transport).CreateForAccount(Account, "stream");
        using var response = await client.GetAsync("https://vendor.invalid/stream", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
        Assert.Equal(1, transport.Active);
    }

    [Fact]
    public async Task Send_failure_releases_lease_and_does_not_retry_or_fall_back()
    {
        using var transport = new RecordingTransport((_, _) => throw new HttpRequestException("controlled failure"));
        using var client = new AccountNetworkClientFactory(new RecordingResolver(), transport).CreateForAccount(Account, "quota");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://vendor.invalid/request"));
        Assert.Equal(1, transport.Sends);
        Assert.Equal(1, transport.Releases);
        Assert.Equal(0, transport.Active);
    }

    [Fact]
    public async Task Content_disposal_failure_still_releases_lease()
    {
        using var transport = new RecordingTransport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new TrackedContent(throwOnDispose: true)
        }));
        using var client = new AccountNetworkClientFactory(new RecordingResolver(), transport).CreateForAccount(Account, "stream");
        var response = await client.GetAsync("https://vendor.invalid/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.Throws<IOException>(() => response.Dispose());
        Assert.Equal(0, transport.Active);
        Assert.Equal(1, transport.Releases);
    }

    [Fact]
    public async Task Buffered_body_failure_still_releases_lease()
    {
        using var transport = new RecordingTransport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingStream(false))
        }));
        using var client = new AccountNetworkClientFactory(new RecordingResolver(), transport).CreateForAccount(Account, "quota");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://vendor.invalid/body"));
        Assert.Equal(0, transport.Active);
        Assert.Equal(1, transport.Releases);
    }

    [Fact]
    public async Task Cancellation_before_send_does_not_resolve_or_acquire()
    {
        var resolver = new RecordingResolver();
        using var transport = new RecordingTransport();
        using var client = new AccountNetworkClientFactory(resolver, transport).CreateForAccount(Account, "quota");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("https://vendor.invalid/request", canceled.Token));
        Assert.Empty(resolver.Accounts);
        Assert.Empty(transport.Acquisitions);
    }

    [Fact]
    public async Task Cancellation_during_inner_send_releases_lease()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new RecordingTransport(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new AccountNetworkClientFactory(new RecordingResolver(), transport).CreateForAccount(Account, "quota");
        using var cancellation = new CancellationTokenSource();
        var send = client.GetAsync("https://vendor.invalid/request", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.Equal(0, transport.Active);
        Assert.Equal(1, transport.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Response_body_failure_or_cancellation_releases_lease(bool cancel)
    {
        using var transport = new RecordingTransport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingStream(cancel))
        }));
        using var client = new AccountNetworkClientFactory(new RecordingResolver(), transport).CreateForAccount(Account, "stream");
        using var response = await client.GetAsync("https://vendor.invalid/stream", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel();
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.ReadAsync(new byte[1], cancellation.Token));
        else
            await Assert.ThrowsAsync<IOException>(async () => await stream.ReadAsync(new byte[1]));
        Assert.Equal(0, transport.Active);
        Assert.Equal(1, transport.Releases);
    }

    [Theory]
    [InlineData("ftp://vendor.invalid/private?secret=value")]
    [InlineData("https://user:password@vendor.invalid/private")]
    public async Task Invalid_request_scheme_or_user_info_fails_with_generic_error_before_resolution(string url)
    {
        var resolver = new RecordingResolver();
        using var transport = new RecordingTransport();
        using var client = new AccountNetworkClientFactory(resolver, transport).CreateForAccount(Account, "quota");
        var error = await Assert.ThrowsAsync<NetworkPolicyException>(() => client.GetAsync(url));
        Assert.Equal("invalid_network_request", error.Code);
        Assert.DoesNotContain(url, error.Message);
        Assert.DoesNotContain("password", error.Message);
        Assert.Empty(resolver.Accounts);
        Assert.Empty(transport.Acquisitions);
    }

    [Fact]
    public async Task Redirect_is_returned_and_explicit_followup_rechecks_policy()
    {
        var resolver = new RecordingResolver();
        using var transport = new RecordingTransport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://other-vendor.invalid/next") },
            Content = new StringContent("")
        }));
        using var client = new AccountNetworkClientFactory(resolver, transport).CreateForAccount(Account, "oauth");
        using var redirect = await client.GetAsync("https://vendor.invalid/start");
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal(1, transport.Sends);
        resolver.Failure = new NetworkPolicyException("proxy_disabled", "Proxy is unavailable.");
        await Assert.ThrowsAsync<NetworkPolicyException>(() => client.GetAsync(redirect.Headers.Location));
        Assert.Equal(1, transport.Sends);
        Assert.Equal(2, resolver.Accounts.Count);
    }

    [Fact]
    public async Task Real_transport_does_not_automatically_follow_loopback_redirect()
    {
        var target = new TcpListener(IPAddress.Loopback, 0);
        var source = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        source.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            var sourcePort = ((IPEndPoint)source.LocalEndpoint).Port;
            var serve = Task.Run(async () =>
            {
                using var accepted = await source.AcceptTcpClientAsync(timeout.Token);
                await using var stream = accepted.GetStream();
                var header = new List<byte>();
                var next = new byte[1];
                while (header.Count < 32768)
                {
                    await stream.ReadExactlyAsync(next, timeout.Token);
                    header.Add(next[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{targetPort}/target\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
            });
            var resolver = new RecordingResolver { Current = ResolvedNetworkConfiguration.Direct };
            using var transport = new NetworkTransport();
            using var client = new AccountNetworkClientFactory(resolver, transport).CreateForAccount(Account, "oauth");
            using var response = await client.GetAsync($"http://127.0.0.1:{sourcePort}/start", timeout.Token);
            await serve;
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.False(target.Pending(), "An automatic redirect must never contact the target.");
            Assert.Single(resolver.Accounts);
        }
        finally
        {
            source.Stop();
            target.Stop();
        }
    }

    private sealed class RecordingResolver : IAccountNetworkPolicyResolver
    {
        public ResolvedNetworkConfiguration Current { get; set; } = ResolvedNetworkConfiguration.Legacy;
        public Exception? Failure { get; set; }
        public ConcurrentQueue<AccountIdentity> Accounts { get; } = new();
        public ConcurrentQueue<NetworkOperationContext> Operations { get; } = new();
        public Task<ResolvedNetworkConfiguration> ResolveForAccountAsync(AccountIdentity account, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Accounts.Enqueue(account);
            return Failure is null ? Task.FromResult(Current) : Task.FromException<ResolvedNetworkConfiguration>(Failure);
        }
        public Task<ResolvedNetworkConfiguration> ResolveAsync(NetworkOperationContext operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Enqueue(operation);
            return Failure is null ? Task.FromResult(Current) : Task.FromException<ResolvedNetworkConfiguration>(Failure);
        }
    }

    private sealed class RecordingTransport : INetworkTransport, IDisposable
    {
        private int _active;
        private int _releases;
        private int _sends;
        public int Active => Volatile.Read(ref _active);
        public int Releases => Volatile.Read(ref _releases);
        public int Sends => Volatile.Read(ref _sends);
        public HttpClient Client { get; }
        public ConcurrentQueue<(ResolvedNetworkConfiguration Configuration, string Scope, string Purpose)> Acquisitions { get; } = new();

        public RecordingTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null)
        {
            Client = new HttpClient(new DelegateHandler((request, ct) =>
            {
                Interlocked.Increment(ref _sends);
                return send?.Invoke(request, ct) ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("response")
                });
            })) { Timeout = Timeout.InfiniteTimeSpan };
        }
        public ValueTask<INetworkTransportLease> AcquireAsync(ResolvedNetworkConfiguration configuration,
            string isolationScope, string purpose, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Acquisitions.Enqueue((configuration, isolationScope, purpose));
            Interlocked.Increment(ref _active);
            return ValueTask.FromResult<INetworkTransportLease>(new Lease(this));
        }
        public void Dispose() => Client.Dispose();
        private sealed class Lease(RecordingTransport owner) : INetworkTransportLease
        {
            private int _disposed;
            public HttpClient Client => owner.Client;
            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Decrement(ref owner._active);
                    Interlocked.Increment(ref owner._releases);
                }
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class TrackedContent(bool throwOnDispose = false) : HttpContent
    {
        public bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[] { 1 }).AsTask();
        protected override bool TryComputeLength(out long length) { length = 1; return true; }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
            if (disposing && throwOnDispose) throw new IOException("controlled disposal failure");
        }
    }

    private sealed class FailingStream(bool cancel) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("controlled read failure");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancel) cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("controlled read failure");
        }
    }
}
