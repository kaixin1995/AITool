using System.Net;
using System.Reflection;
using AITool.Application.Networking;
using AITool.Application.Proxy;
using AITool.Domain.Models;
using AITool.Domain.Operations;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Domain.Xai;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Web.Controllers.Admin;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.IntegrationTests.OAuth;

/// <summary>Exercises auxiliary forwarding with a private database and an in-memory upstream.</summary>
public sealed class AuxiliaryOAuthInvalidationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aux-oauth-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;
    private readonly AppDbContext _db;
    private readonly ProxyRequestMetadataCache _cache;
    private readonly Site _site;
    private readonly XaiAccount _account;

    public AuxiliaryOAuthInvalidationTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSqlSugar($"Data Source={_path}");
        services.AddScoped<ProxyRequestMetadataCache>();
        services.AddScoped<ForwardOAuthInvalidationService>();
        _services = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_services.GetRequiredService<SqlSugar.ISqlSugarClient>());
        _scope = _services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _cache = _scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        _site = new Site { Id = Guid.NewGuid(), Name = "aux", BaseUrl = "https://example.invalid/v1", ApiKey = "access", ManagedSource = "xai_oauth", IsEnabled = true, SupportsOpenAi = true };
        _account = new XaiAccount { Id = Guid.NewGuid(), LinkedSiteId = _site.Id, AccessToken = "access", RefreshToken = "refresh", IsEnabled = true };
        _db.InsertAsync(_site).GetAwaiter().GetResult();
        _db.InsertAsync(_account).GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData("Codex", true)]
    [InlineData("Google", true)]
    [InlineData("kimi_oauth", true)]
    [InlineData("device_oauth", true)]
    [InlineData("xai_oauth", false)]
    [InlineData(null, false)]
    [InlineData("ordinary", false)]
    public async Task Developer_prepares_callbacks_only_for_managed_sources(string? source, bool refresh)
    {
        var request = new ProxyForwardRequest { SiteId = _site.Id, ManagedSource = source, RequestBody = "{}" };
        await PrepareDeveloperAsync(request);
        (request.RefreshTargetApiKeyAsync is not null).Should().Be(refresh);
        (request.InvalidateTargetCredentialAsync is not null).Should().Be(refresh || source == "xai_oauth");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false, false)]
    [InlineData(HttpStatusCode.Unauthorized, true, true)]
    [InlineData(HttpStatusCode.Forbidden, false, true)]
    public async Task Developer_real_forward_only_invalidates_matching_401(HttpStatusCode status, bool rotate, bool enabled)
    {
        var request = new ProxyForwardRequest
        {
            SiteId = _site.Id, ManagedSource = _site.ManagedSource,
            AccountIdentity = new AccountIdentity("xai", _account.Id), TargetBaseUrl = _site.BaseUrl,
            TargetApiKey = "access", RequestBody = "{}", RequestTimeoutSeconds = 10
        };
        await PrepareDeveloperAsync(request);
        using var client = new HttpClient(new ResponseHandler(status, async () =>
        {
            if (rotate)
                await _db.Client.Updateable<XaiAccount>().SetColumns(x => new XaiAccount { RefreshToken = "rotated" })
                    .Where(x => x.Id == _account.Id).ExecuteCommandAsync();
        }));
        var result = await Forward(client).ForwardAsync(request);
        result.StatusCode.Should().Be((int)status);
        await AssertEnabledAsync(enabled);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    public async Task Assistant_real_forward_invalidates_only_upstream_401(HttpStatusCode status, bool enabled)
    {
        var model = new ModelLibraryItem { Id = Guid.NewGuid(), ModelName = "model", IsEnabled = true };
        var mapping = new SiteModelMapping { Id = Guid.NewGuid(), SiteId = _site.Id, ModelLibraryItemId = model.Id, RemoteModelName = "model", IsEnabled = true };
        await _db.InsertAsync(model);
        await _db.InsertAsync(mapping);
        await _db.InsertAsync(new SystemRuntimeSettings { DefaultAiTargetMappingId = mapping.Id });
        using var client = new HttpClient(new ResponseHandler(status, () => Task.CompletedTask));
        var assistant = new AiAssistantService(_db, _cache, Forward(client), NullLogger<AiAssistantService>.Instance,
            new PassThroughSession(), _scope.ServiceProvider);
        (await assistant.CompleteAsync("hello", CancellationToken.None)).Success.Should().BeFalse();
        await AssertEnabledAsync(enabled);
    }

    [Fact]
    public async Task Assistant_unreadable_401_body_still_invalidates_account()
    {
        var model = new ModelLibraryItem { Id = Guid.NewGuid(), ModelName = "model", IsEnabled = true };
        var mapping = new SiteModelMapping { Id = Guid.NewGuid(), SiteId = _site.Id, ModelLibraryItemId = model.Id, RemoteModelName = "model", IsEnabled = true };
        await _db.InsertAsync(model);
        await _db.InsertAsync(mapping);
        await _db.InsertAsync(new SystemRuntimeSettings { DefaultAiTargetMappingId = mapping.Id });
        using var client = new HttpClient(new BrokenResponseHandler());
        var assistant = new AiAssistantService(_db, _cache, Forward(client), NullLogger<AiAssistantService>.Instance,
            new PassThroughSession(), _scope.ServiceProvider);
        (await assistant.CompleteAsync("hello", CancellationToken.None)).Success.Should().BeFalse();
        await AssertEnabledAsync(false);
    }

    private sealed class BrokenResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new BrokenContent() });
    }

    private sealed class BrokenContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.FromException(new IOException("broken response body"));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private ProxyForwardService Forward(HttpClient client) => new(client, NullLogger<ProxyForwardService>.Instance,
        networkPolicyResolver: new DirectPolicy(), credentialResolver: new ForwardCredentialResolver(_services.GetRequiredService<IServiceScopeFactory>()));

    private async Task PrepareDeveloperAsync(ProxyForwardRequest request)
    {
        var controller = new DeveloperInvocationsApiController(null!, null!, null!, _cache, null!, _db, null!, null!)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = _scope.ServiceProvider } } };
        var method = typeof(DeveloperInvocationsApiController).GetMethod("PrepareForwardAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task<ProxyForwardRequest>)method.Invoke(controller, [request, CancellationToken.None])!;
    }

    private async Task AssertEnabledAsync(bool enabled)
    {
        var account = await _db.Client.Queryable<XaiAccount>().InSingleAsync(_account.Id);
        var site = await _db.Client.Queryable<Site>().InSingleAsync(_site.Id);
        account.IsEnabled.Should().Be(enabled);
        account.RequiresReauth.Should().Be(!enabled);
        site.IsEnabled.Should().Be(enabled);
    }

    private sealed class ResponseHandler(HttpStatusCode status, Func<Task> onSend) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await onSend();
            return new HttpResponseMessage(status) { Content = new StringContent("upstream response") };
        }
    }

    private sealed class DirectPolicy : IForwardNetworkPolicyResolver
    {
        public Task<ForwardNetworkPolicyResolution> ResolveAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ForwardNetworkPolicyResolution(ResolvedNetworkConfiguration.Legacy, "test", true));
    }

    private sealed class PassThroughSession : IAuxiliarySessionPreparation
    {
        public Task<AuxiliaryPreparedSession> PrepareAsync(string trustedScope, AccountIdentity account, string protocolType,
            bool isAntigravity, string originalRequestBody, string preparedRequestBody,
            IReadOnlyDictionary<string, string> mergedHeaders, CancellationToken cancellationToken = default)
            => Task.FromResult(new AuxiliaryPreparedSession(preparedRequestBody, new Dictionary<string, string>(mergedHeaders)));
    }

    public void Dispose()
    {
        _scope.Dispose();
        _services.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_path + suffix); } catch (IOException) { }
    }
}
