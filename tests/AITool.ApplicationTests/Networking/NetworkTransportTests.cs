using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AITool.Application.Networking;
using AITool.Domain.Operations;
using AITool.Infrastructure.Networking;

namespace AITool.ApplicationTests.Networking;

public sealed class NetworkTransportTests
{
    private static ResolvedNetworkConfiguration Profile(Uri endpoint, Guid? id = null, long revision = 1,
        NetworkCredential? credentials = null) => new()
    {
        Mode = NetworkMode.ProxyProfile,
        ProxyProfileId = id ?? Guid.NewGuid(),
        ProfileRevision = revision,
        ProxyEndpoint = endpoint,
        Credentials = credentials
    };

    [Fact]
    public async Task Pool_reuses_only_identical_opaque_scope_purpose_and_generation()
    {
        using var transport = new NetworkTransport();
        var profile = Profile(new Uri("http://127.0.0.1:3128"));
        await using var first = await transport.AcquireAsync(profile, "Account:A", "quota");
        await using var same = await transport.AcquireAsync(profile, "Account:A", "quota");
        await using var otherAccount = await transport.AcquireAsync(profile, "Account:B", "quota");
        await using var otherCase = await transport.AcquireAsync(profile, "account:A", "quota");
        await using var otherPurpose = await transport.AcquireAsync(profile, "Account:A", "oauth");
        await using var otherPurposeCase = await transport.AcquireAsync(profile, "Account:A", "Quota");
        await using var pending = await transport.AcquireAsync(profile, "pending:operation-1", "quota");
        Assert.Same(first.Client, same.Client);
        foreach (var different in new[] { otherAccount, otherCase, otherPurpose, otherPurposeCase, pending })
            Assert.NotSame(first.Client, different.Client);
        Assert.Equal(Timeout.InfiniteTimeSpan, first.Client.Timeout);
        Assert.Empty(first.Client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Profile_identity_and_mode_replace_clients_but_secrets_are_not_pool_keys()
    {
        using var transport = new NetworkTransport();
        var id = Guid.NewGuid();
        var firstConfiguration = Profile(new Uri("http://127.0.0.1:3128"), id,
            credentials: new NetworkCredential("first", "secret-one"));
        await using var first = await transport.AcquireAsync(firstConfiguration, "a", "quota");
        // The resolver must bump a revision for changes. Raw endpoint/credential values are never keys.
        await using var sameGeneration = await transport.AcquireAsync(Profile(new Uri("http://127.0.0.1:3129"), id,
            credentials: new NetworkCredential("second", "secret-two")), "a", "quota");
        Assert.Same(first.Client, sameGeneration.Client);
        await using var otherProfile = await transport.AcquireAsync(Profile(new Uri("http://127.0.0.1:3128")), "a", "quota");
        await using var direct = await transport.AcquireAsync(ResolvedNetworkConfiguration.Direct, "a", "quota");
        await using var legacy = await transport.AcquireAsync(ResolvedNetworkConfiguration.Legacy, "a", "quota");
        Assert.NotSame(first.Client, otherProfile.Client);
        Assert.NotSame(otherProfile.Client, direct.Client);
        Assert.NotSame(direct.Client, legacy.Client);
    }

    [Fact]
    public async Task Generation_switch_retires_old_client_until_final_idempotent_lease_release()
    {
        await using var origin = new LoopbackNetworkHarness(async (s, ct) =>
        {
            await LoopbackNetworkHarness.ReadHttpAsync(s, ct);
            await LoopbackNetworkHarness.ReplyAsync(s, "alive", ct);
        });
        using var transport = new NetworkTransport();
        var id = Guid.NewGuid();
        var old = await transport.AcquireAsync(Profile(origin.Url, id), "a", "quota");
        var oldClient = old.Client;
        var duplicate = await transport.AcquireAsync(Profile(origin.Url, id), "a", "quota");
        await using var next = await transport.AcquireAsync(Profile(origin.Url, id, 2), "a", "quota");
        Assert.NotSame(oldClient, next.Client);
        await old.DisposeAsync();
        await old.DisposeAsync();
        Assert.Equal("alive", await oldClient.GetStringAsync(origin.Url).WaitAsync(TimeSpan.FromSeconds(10)));
        await duplicate.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => oldClient.GetAsync(origin.Url));
        Assert.Throws<ObjectDisposedException>(() => old.Client);
    }

    [Fact]
    public async Task Capacity_never_evicts_active_clients_counts_retired_and_recovers_on_release()
    {
        using var transport = new NetworkTransport(1);
        var first = await transport.AcquireAsync(ResolvedNetworkConfiguration.Direct, "account-a", "quota");
        var client = first.Client;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await transport.AcquireAsync(ResolvedNetworkConfiguration.Direct, "account-b", "quota"));
        Assert.Contains("capacity", error.Message);
        Assert.DoesNotContain("account-a", error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await transport.AcquireAsync(ResolvedNetworkConfiguration.Legacy, "account-a", "quota"));
        await first.DisposeAsync();
        await using var replacement = await transport.AcquireAsync(ResolvedNetworkConfiguration.Legacy, "account-a", "quota");
        Assert.NotSame(client, replacement.Client);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetAsync("http://127.0.0.1:1"));
    }

    [Fact]
    public async Task Idle_LRU_eviction_preserves_active_entries()
    {
        using var transport = new NetworkTransport(2);
        var idle = await transport.AcquireAsync(ResolvedNetworkConfiguration.Direct, "idle", "quota");
        var idleClient = idle.Client;
        await idle.DisposeAsync();
        await using var active = await transport.AcquireAsync(ResolvedNetworkConfiguration.Direct, "active", "quota");
        await using var added = await transport.AcquireAsync(ResolvedNetworkConfiguration.Direct, "added", "quota");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => idleClient.GetAsync("http://127.0.0.1:1"));
        await using var activeAgain = await transport.AcquireAsync(ResolvedNetworkConfiguration.Direct, "active", "quota");
        Assert.Same(active.Client, activeAgain.Client);
    }

    [Fact]
    public async Task Concurrent_acquisitions_share_one_client_and_release_without_underflow()
    {
        using var transport = new NetworkTransport();
        var leases = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
            await transport.AcquireAsync(ResolvedNetworkConfiguration.Direct, "a", "quota"))));
        Assert.All(leases, lease => Assert.Same(leases[0].Client, lease.Client));
        var client = leases[0].Client;
        transport.Dispose();
        await Task.WhenAll(leases.SelectMany(lease => new[]
        {
            Task.Run(async () => await lease.DisposeAsync()),
            Task.Run(async () => await lease.DisposeAsync())
        }));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetAsync("http://127.0.0.1:1"));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await transport.AcquireAsync(ResolvedNetworkConfiguration.Direct, "a", "quota"));
    }

    [Fact]
    public async Task Cancellation_before_acquisition_creates_no_handler()
    {
        var created = 0;
        using var transport = new NetworkTransport(1, _ => Interlocked.Increment(ref created));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await transport.AcquireAsync(ResolvedNetworkConfiguration.Direct, "a", "quota", canceled.Token));
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task Direct_ignores_controlled_default_proxy_while_legacy_uses_it()
    {
        await using var defaultProxy = new LoopbackHttpProxy("default-proxy");
        await using var origin = new LoopbackNetworkHarness(async (s, ct) =>
        {
            await LoopbackNetworkHarness.ReadHttpAsync(s, ct);
            await LoopbackNetworkHarness.ReplyAsync(s, "origin-direct", ct);
        });
        // Assign the default-proxy equivalent before transport mode settings, without process-global changes.
        using var transport = new NetworkTransport(8, handler => handler.Proxy = new WebProxy(defaultProxy.Url));
        await using var direct = await transport.AcquireAsync(ResolvedNetworkConfiguration.Direct, "direct", "quota");
        Assert.Equal("origin-direct", await direct.Client.GetStringAsync(origin.Url).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, defaultProxy.Accepted);
        await using var legacy = await transport.AcquireAsync(ResolvedNetworkConfiguration.Legacy, "legacy", "quota");
        Assert.Equal("default-proxy", await legacy.Client.GetStringAsync(origin.Url).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, origin.Accepted);
        Assert.True(defaultProxy.Accepted > 0);
    }

    [Fact]
    public async Task Profiles_A_and_B_use_explicit_proxies_and_do_not_share_headers_or_authentication()
    {
        var credentialsA = new NetworkCredential("user-a", "secret-a");
        var credentialsB = new NetworkCredential("user-b", "secret-b");
        await using var proxyA = new LoopbackHttpProxy("A", credentialsA);
        await using var proxyB = new LoopbackHttpProxy("B", credentialsB);
        await using var defaultProxy = new LoopbackHttpProxy("MUST-NOT-RUN");
        using var transport = new NetworkTransport(8, handler => handler.Proxy = new WebProxy(defaultProxy.Url));
        await using var a = await transport.AcquireAsync(Profile(proxyA.Url, credentials: credentialsA), "a", "quota");
        await using var b = await transport.AcquireAsync(Profile(proxyB.Url, credentials: credentialsB), "b", "quota");
        // Mutating the resolver object after acquisition must not change transport credentials.
        credentialsA.Password = "changed-after-acquisition";
        using var requestA = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:54321/path?a=1");
        requestA.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "account-a-only");
        using var responseA = await a.Client.SendAsync(requestA).WaitAsync(TimeSpan.FromSeconds(10));
        using var responseB = await b.Client.GetAsync("http://127.0.0.1:54321/path").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("A", await responseA.Content.ReadAsStringAsync());
        Assert.Equal("B", await responseB.Content.ReadAsStringAsync());
        Assert.Contains(proxyA.Requests, r => r.RequestLine == "GET http://127.0.0.1:54321/path?a=1 HTTP/1.1");
        Assert.Contains(proxyA.Requests, r => r.Headers.GetValueOrDefault("Proxy-Authorization") == Basic("user-a", "secret-a"));
        Assert.Contains(proxyB.Requests, r => r.Headers.GetValueOrDefault("Proxy-Authorization") == Basic("user-b", "secret-b"));
        Assert.All(proxyB.Requests, r => Assert.False(r.Headers.ContainsKey("Authorization")));
        Assert.Empty(a.Client.DefaultRequestHeaders);
        Assert.Empty(b.Client.DefaultRequestHeaders);
        Assert.Equal(0, defaultProxy.Accepted);
        Assert.Empty(proxyA.Errors);
        Assert.Empty(proxyB.Errors);
    }

    [Fact]
    public async Task Proxy_407_and_connection_failure_never_fall_back_to_default_proxy_or_origin()
    {
        await using var proxy = new LoopbackHttpProxy("never", new NetworkCredential("required", "secret"));
        await using var defaultProxy = new LoopbackHttpProxy("MUST-NOT-RUN");
        await using var origin = new LoopbackNetworkHarness((s, ct) => LoopbackNetworkHarness.ReplyAsync(s, "MUST-NOT-RUN", ct));
        using var transport = new NetworkTransport(8, handler => handler.Proxy = new WebProxy(defaultProxy.Url));
        await using var rejected = await transport.AcquireAsync(Profile(proxy.Url,
            credentials: new NetworkCredential("wrong", "wrong")), "a", "quota");
        using var response = await rejected.Client.GetAsync(origin.Url).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
        var closedPort = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        closedPort.Start();
        var endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)closedPort.LocalEndpoint).Port}");
        closedPort.Stop();
        await using var failed = await transport.AcquireAsync(Profile(endpoint), "b", "quota");
        await Assert.ThrowsAsync<HttpRequestException>(() => failed.Client.GetAsync(origin.Url).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, defaultProxy.Accepted);
        Assert.Equal(0, origin.Accepted);
    }

    [Theory]
    [InlineData(NetworkMode.Direct)]
    [InlineData(NetworkMode.Legacy)]
    public async Task Cookies_and_request_authorization_are_never_replayed(NetworkMode mode)
    {
        var captures = new ConcurrentQueue<LoopbackNetworkHarness.HttpCapture>();
        await using var origin = new LoopbackNetworkHarness(async (s, ct) =>
        {
            captures.Enqueue(await LoopbackNetworkHarness.ReadHttpAsync(s, ct));
            await LoopbackNetworkHarness.ReplyAsync(s, "ok", ct, extraHeaders: "Set-Cookie: account=secret; Path=/\r\n");
        });
        using var transport = new NetworkTransport(8, handler => handler.Proxy = new WebProxy());
        var config = new ResolvedNetworkConfiguration { Mode = mode };
        await using var a = await transport.AcquireAsync(config, "a", "quota");
        await using var b = await transport.AcquireAsync(config, "b", "quota");
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "account-a");
        using var first = await a.Client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(10));
        using var second = await a.Client.GetAsync(origin.Url).WaitAsync(TimeSpan.FromSeconds(10));
        using var third = await b.Client.GetAsync(origin.Url).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(a.Client, b.Client);
        Assert.Equal(3, captures.Count);
        Assert.All(captures, capture => Assert.False(capture.Headers.ContainsKey("Cookie")));
        Assert.All(captures.Skip(1), capture => Assert.False(capture.Headers.ContainsKey("Authorization")));
    }

    [Theory]
    [InlineData("http://user:secret@127.0.0.1:1")]
    [InlineData("ftp://127.0.0.1:1")]
    [InlineData("http://127.0.0.1:1/path?secret=password")]
    public async Task Invalid_profile_is_rejected_without_fallback_or_secret_in_error(string endpoint)
    {
        var created = 0;
        using var transport = new NetworkTransport(2, _ => created++);
        var error = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await transport.AcquireAsync(Profile(new Uri(endpoint)), "a", "quota"));
        Assert.DoesNotContain(endpoint, error.Message);
        Assert.DoesNotContain("password", error.Message);
        Assert.DoesNotContain("secret", error.Message);
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task HTTP_proxy_CONNECT_reaches_TLS_origin_with_test_only_certificate_trust()
    {
        using var certificate = LoopbackNetworkHarness.CreateCertificate();
        await using var origin = new LoopbackNetworkHarness(async (s, ct) =>
        {
            var request = await LoopbackNetworkHarness.ReadHttpAsync(s, ct);
            Assert.False(request.Headers.ContainsKey("Proxy-Authorization"));
            await LoopbackNetworkHarness.ReplyAsync(s, "tls-upstream", ct);
        }, certificate);
        await using var proxy = new LoopbackHttpProxy("proxy", new NetworkCredential("tunnel", "secret"));
        using var transport = new NetworkTransport(8, handler => TrustOnly(handler, certificate));
        await using var lease = await transport.AcquireAsync(Profile(proxy.Url,
            credentials: new NetworkCredential("tunnel", "secret")), "a", "quota");
        Assert.Equal("tls-upstream", await lease.Client.GetStringAsync(origin.Url).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains(proxy.Requests, request => request.RequestLine == $"CONNECT 127.0.0.1:{origin.Port} HTTP/1.1");
        Assert.Empty(proxy.Errors);
        Assert.Empty(origin.Errors);
    }

    [Fact]
    public async Task HTTPS_proxy_endpoint_supports_TLS_to_proxy_separately_from_CONNECT()
    {
        using var certificate = LoopbackNetworkHarness.CreateCertificate();
        await using var proxy = new LoopbackHttpProxy("tls-proxy", tlsCertificate: certificate);
        using var transport = new NetworkTransport(8, handler => TrustOnly(handler, certificate));
        await using var lease = await transport.AcquireAsync(Profile(proxy.Url), "a", "quota");
        Assert.Equal("tls-proxy", await lease.Client.GetStringAsync("http://127.0.0.1:54321/path").WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains(proxy.Requests, request => request.RequestLine == "GET http://127.0.0.1:54321/path HTTP/1.1");
        Assert.Empty(proxy.Errors);
    }

    [Fact]
    public async Task Production_transport_does_not_trust_loopback_self_signed_TLS()
    {
        using var certificate = LoopbackNetworkHarness.CreateCertificate();
        await using var origin = new LoopbackNetworkHarness((s, ct) => LoopbackNetworkHarness.ReplyAsync(s, "never", ct), certificate);
        using var transport = new NetworkTransport();
        await using var lease = await transport.AcquireAsync(ResolvedNetworkConfiguration.Direct, "a", "quota");
        await Assert.ThrowsAsync<HttpRequestException>(() => lease.Client.GetAsync(origin.Url).WaitAsync(TimeSpan.FromSeconds(15)));
    }

    [Theory]
    [InlineData("socks4", "http://127.0.0.1:8080/path", "127.0.0.1:8080")]
    [InlineData("socks4a", "http://upstream.invalid:8080/path", "upstream.invalid:8080")]
    [InlineData("socks5", "http://upstream.invalid:8080/path", "upstream.invalid:8080")]
    public async Task SOCKS_profiles_use_declared_protocol_and_send_target_to_proxy(string scheme, string target, string expected)
    {
        var credentials = new NetworkCredential("socks-user", "socks-secret");
        await using var proxy = new LoopbackSocksProxy(scheme, scheme == "socks5" ? credentials : null);
        using var transport = new NetworkTransport();
        await using var lease = await transport.AcquireAsync(Profile(proxy.Url(scheme), credentials: credentials), "a", "quota");
        Assert.Equal(scheme, await lease.Client.GetStringAsync(target).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains(expected, proxy.Destinations);
        Assert.Contains("socks-user", proxy.UserNames);
        Assert.Empty(proxy.Errors);
    }

    [Fact]
    public async Task Active_stream_survives_revision_retirement_and_transport_disposal()
    {
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var origin = new LoopbackNetworkHarness(async (s, ct) =>
        {
            await LoopbackNetworkHarness.ReadHttpAsync(s, ct);
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 11\r\nConnection: close\r\n\r\nhello"), ct);
            await s.FlushAsync(ct);
            headersSent.TrySetResult();
            await finish.Task.WaitAsync(ct);
            await s.WriteAsync(Encoding.ASCII.GetBytes(" world"), ct);
        });
        using var transport = new NetworkTransport();
        var id = Guid.NewGuid();
        await using var old = await transport.AcquireAsync(Profile(origin.Url, id), "a", "stream");
        var oldClient = old.Client;
        using var response = await oldClient.GetAsync(origin.Url, HttpCompletionOption.ResponseHeadersRead)
            .WaitAsync(TimeSpan.FromSeconds(10));
        await headersSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var next = await transport.AcquireAsync(Profile(origin.Url, id, 2), "a", "stream");
        transport.Dispose();
        finish.TrySetResult();
        Assert.Equal("hello world", await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        await old.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => oldClient.GetAsync(origin.Url));
    }

    private static string Basic(string user, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));

    private static void TrustOnly(SocketsHttpHandler handler, X509Certificate2 certificate)
    {
        handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, errors) =>
            presented is not null && presented.GetCertHashString() == certificate.GetCertHashString() &&
            (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None;
    }
}
