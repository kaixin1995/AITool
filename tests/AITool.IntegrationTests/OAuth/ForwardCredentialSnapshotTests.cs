using System.Net;
using AITool.Application.Networking;
using AITool.Application.Proxy;
using AITool.Domain.Google;
using AITool.Domain.OAuth;
using AITool.Domain.Operations;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.OAuth;

/// <summary>Real SQLite and the real device gate; upstream sends are fake only.</summary>
public sealed class ForwardCredentialSnapshotTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aitool-forward-snapshot-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;
    private readonly AppDbContext _db;
    private readonly Site _site = new() { Name = "device", BaseUrl = "https://example.test/tenantA", ApiKey = "stale", ManagedSource = "device_oauth" };
    private readonly DeviceOAuthAccount _account = new() { VendorKey = "test", DisplayName = "test", AccessToken = "latest" };

    public ForwardCredentialSnapshotTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_path}");
        _services = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_services.GetRequiredService<SqlSugar.ISqlSugarClient>());
        _scope = _services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _account.LinkedSiteId = _site.Id;
        _db.InsertAsync(_site).GetAwaiter().GetResult();
        _db.InsertAsync(_account).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Cross_provider_commit_while_gate_waits_blocks_send_and_prior_inflight_finishes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async _ =>
        {
            started.TrySetResult();
            await finish.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return Ok();
        });
        var service = CreateService(handler, new Network());
        var inFlight = service.ForwardAsync(Request());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnQueued(Guid id) { if (id == _account.Id) queued.TrySetResult(); }
        DeviceOAuthRefreshGate.Queued += OnQueued;
        try
        {
            Task<ProxyForwardResult> pending;
            using (await DeviceOAuthRefreshGate.AcquireAsync(_account.Id, default))
            {
                pending = service.ForwardAsync(Request());
                await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await _db.InsertAsync(new GoogleAccount { LinkedSiteId = _site.Id });
            }
            var blocked = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            blocked.Success.Should().BeFalse();
            blocked.StatusCode.Should().Be(503);
            handler.Calls.Should().Be(1, "the newly ambiguous owner must not send");
            finish.TrySetResult();
            (await inFlight.WaitAsync(TimeSpan.FromSeconds(10))).Success.Should().BeTrue("an already initiated send is not retroactively revoked");
        }
        finally { finish.TrySetResult(); DeviceOAuthRefreshGate.Queued -= OnQueued; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Policy_changes_while_waiting_for_gate_are_observed_before_send(bool disabled)
    {
        var handler = new Handler(_ => Task.FromResult(Ok()));
        var network = new Network();
        var service = CreateService(handler, network);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnQueued(Guid id) { if (id == _account.Id) queued.TrySetResult(); }
        DeviceOAuthRefreshGate.Queued += OnQueued;
        try
        {
            Task<ProxyForwardResult> pending;
            using (await DeviceOAuthRefreshGate.AcquireAsync(_account.Id, default))
            {
                pending = service.ForwardAsync(Request());
                await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
                network.Disabled = disabled;
                network.Mode = NetworkMode.ProxyProfile;
            }
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            if (disabled)
            {
                result.StatusCode.Should().Be(503);
                handler.Calls.Should().Be(0);
            }
            else
            {
                result.Success.Should().BeTrue();
                network.AcquiredMode.Should().Be(NetworkMode.ProxyProfile);
                handler.Calls.Should().Be(1);
            }
        }
        finally { DeviceOAuthRefreshGate.Queued -= OnQueued; }
    }

    [Fact]
    public async Task Device_source_whitespace_still_uses_live_token()
    {
        _site.ManagedSource = " device_oauth ";
        await _db.UpdateAsync(_site);
        var request = Request();
        request.ManagedSource = " device_oauth ";
        var resolver = new ForwardCredentialResolver(_services.GetRequiredService<IServiceScopeFactory>());
        using var credential = await resolver.ResolveAsync(request);
        credential.IsDeviceOAuth.Should().BeTrue();
        credential.ApiKey.Should().Be("latest");
    }

    [Fact]
    public async Task Managed_source_transitions_conflicting_identity_and_path_case_fail_closed()
    {
        var resolver = new ForwardCredentialResolver(_services.GetRequiredService<IServiceScopeFactory>());
        var request = Request();
        request.TargetBaseUrl = "https://EXAMPLE.test/tenanta";
        await resolver.Invoking(r => r.ResolveAsync(request)).Should().ThrowAsync<NetworkPolicyException>();
        request.TargetBaseUrl = _site.BaseUrl;
        request.ManagedSource = null;
        await resolver.Invoking(r => r.ResolveAsync(request)).Should().ThrowAsync<NetworkPolicyException>();
        request.ManagedSource = "device_oauth";
        _site.ManagedSource = null;
        await _db.UpdateAsync(_site);
        await resolver.Invoking(r => r.ResolveAsync(request)).Should().ThrowAsync<NetworkPolicyException>();
        _site.ManagedSource = "Google";
        await _db.UpdateAsync(_site);
        request.ManagedSource = "Google";
        request.AccountIdentity = new AccountIdentity("codex", Guid.NewGuid());
        await resolver.Invoking(r => r.ResolveAsync(request)).Should().ThrowAsync<NetworkPolicyException>();
    }

    private ProxyForwardService CreateService(Handler handler, Network network)
    {
        network.Client = new HttpClient(handler);
        return new ProxyForwardService(new HttpClient(new Handler(_ => throw new InvalidOperationException("old/default transport used"))),
            NullLogger<ProxyForwardService>.Instance, network, network,
            new ForwardCredentialResolver(_services.GetRequiredService<IServiceScopeFactory>()));
    }

    private ProxyForwardRequest Request() => new()
    {
        SiteId = _site.Id, ManagedSource = "device_oauth", AccountIdentity = new AccountIdentity("device-oauth", _account.Id),
        TargetBaseUrl = _site.BaseUrl, TargetApiKey = "stale", RequestBody = "{}", RequestTimeoutSeconds = 10
    };

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return send(request);
        }
    }

    private sealed class Network : IForwardNetworkPolicyResolver, INetworkTransport
    {
        public bool Disabled;
        public NetworkMode Mode = NetworkMode.Direct;
        public NetworkMode? AcquiredMode;
        public HttpClient Client = null!;
        public Task<ForwardNetworkPolicyResolution> ResolveAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
        {
            if (Disabled) throw new NetworkPolicyException("disabled", "Profile disabled");
            return Task.FromResult(new ForwardNetworkPolicyResolution(new ResolvedNetworkConfiguration { Mode = Mode }, "test"));
        }
        public ValueTask<INetworkTransportLease> AcquireAsync(ResolvedNetworkConfiguration configuration, string isolationScope,
            string purpose, CancellationToken cancellationToken = default)
        {
            AcquiredMode = configuration.Mode;
            return ValueTask.FromResult<INetworkTransportLease>(new Lease(Client));
        }
    }

    private sealed class Lease(HttpClient client) : INetworkTransportLease
    {
        public HttpClient Client { get; } = client;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _scope.Dispose();
        _services.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_path + suffix); } catch { }
    }
}
