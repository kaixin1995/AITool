using System.Net;
using AITool.Application.Codex;
using AITool.Application.Google;
using AITool.Application.Kimi;
using AITool.Application.OAuth;
using AITool.Application.Xai;
using AITool.Domain.Codex;
using AITool.Domain.Google;
using AITool.Domain.Kimi;
using AITool.Domain.Sites;
using AITool.Domain.Xai;
using AITool.Infrastructure.Codex;
using AITool.Infrastructure.Google;
using AITool.Infrastructure.Kimi;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Xai;
using AITool.Web.Controllers.Admin;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.IntegrationTests.OAuth;

/// <summary>Real model controller and fetcher paths, isolated SQLite and a single fake upstream.</summary>
public sealed class DedicatedModelCredentialIsolationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"model-isolation-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;
    private readonly AppDbContext _db;
    private readonly Guid _id = Guid.NewGuid();
    private readonly Guid _siteId = Guid.NewGuid();

    public DedicatedModelCredentialIsolationTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSqlSugar($"Data Source={_path}");
        services.AddScoped<ProxyRequestMetadataCache>();
        services.AddScoped<ForwardOAuthInvalidationService>();
        // ModelCredentialRecovery 的 401 刷新路径按 provider 从容器解析对应刷新服务；
        // 刷新上游统一抛"明确拒绝"异常 → recovery 走 Invalidate(旧快照) + 401，是否禁用交给
        // ForwardOAuthInvalidationService 的精确凭证匹配守卫判定——正是本测试锁定的主题
        // （凭证被并发轮换的账号绝不能被这次 401 误禁）。
        services.AddSingleton(new DeviceOAuthVendorRegistry([]));
        services.AddScoped<DeviceOAuthCredentialRefreshService>();
        services.AddScoped<CodexCredentialRefreshService>();
        services.AddScoped<GoogleCredentialRefreshService>();
        services.AddScoped<KimiCredentialRefreshService>();
        services.AddSingleton<ICodexOAuthClient>(new RefusingCodexOAuthClient());
        services.AddSingleton<IGoogleOAuthClient>(new RefusingGoogleOAuthClient());
        services.AddSingleton<IKimiOAuthClient>(new RefusingKimiOAuthClient());
        services.AddSingleton<IXaiOAuthClient>(new RefusingXaiOAuthClient());
        _services = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_services.GetRequiredService<SqlSugar.ISqlSugarClient>());
        _scope = _services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    [Theory]
    [InlineData("codex", "none", false)]
    [InlineData("google", "none", false)]
    [InlineData("kimi", "none", false)]
    [InlineData("xai", "none", false)]
    [InlineData("codex", "refresh", true)]
    [InlineData("google", "refresh", true)]
    [InlineData("kimi", "refresh", true)]
    [InlineData("xai", "refresh", true)]
    [InlineData("codex", "timestamp", true)]
    [InlineData("google", "timestamp", true)]
    [InlineData("kimi", "timestamp", true)]
    [InlineData("xai", "timestamp", true)]
    // access 组期望为 false（ec9c1c4 语义）：401 时 access 已被并发轮换 → 刷新服务复用新凭证
    // 重试一次（SentToken=new-access）→ 上游连当前有效凭证也拒绝 → Invalidate 精确匹配当前
    // 凭证并禁用。refresh/timestamp 组仅单一字段变化，Invalidate 的全字段守卫不匹配 → 不禁用。
    [InlineData("codex", "access", false)]
    [InlineData("google", "access", false)]
    [InlineData("kimi", "access", false)]
    [InlineData("xai", "access", false)]
    public async Task Model_401_only_disables_the_exact_credential_sent(string provider, string rotation, bool enabled)
    {
        await SeedAsync(provider);
        using var handler = new Handler(HttpStatusCode.Unauthorized, () => RotateAsync(provider, rotation));
        using var client = new HttpClient(handler);
        var result = await FetchAsync(provider, client);
        // access 组的最后一次请求是 recovery 用并发轮换后的新凭证做的重试；其余组只发送过一次。
        handler.SentToken.Should().Be(rotation == "access" ? "new-access" : "old-access");
        if (provider == "codex") result.Should().BeOfType<OkObjectResult>();
        else result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(401);
        await AssertStateAsync(provider, enabled, rotation);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Proxy_407_never_disables_account_even_if_body_mentions_403(string provider)
    {
        await SeedAsync(provider);
        using var handler = new Handler(HttpStatusCode.ProxyAuthenticationRequired, () => Task.CompletedTask);
        using var client = new HttpClient(handler);
        if (provider == "google")
        {
            var exception = await Assert.ThrowsAsync<HttpRequestException>(() => FetchAsync(provider, client));
            exception.StatusCode.Should().Be(HttpStatusCode.ProxyAuthenticationRequired);
        }
        else
        {
            (await FetchAsync(provider, client)).Should().BeOfType<OkObjectResult>();
        }
        handler.SentToken.Should().Be("old-access");
        await AssertStateAsync(provider, true, "none");
    }

    [Theory]
    [InlineData("none", 401)]
    [InlineData("none", 403)]
    [InlineData("none", 407)]
    [InlineData("none", 500)]
    [InlineData("access", 401)]
    [InlineData("refresh", 401)]
    [InlineData("timestamp", 401)]
    [InlineData("access", 403)]
    [InlineData("refresh", 403)]
    [InlineData("timestamp", 403)]
    [InlineData("access", 407)]
    [InlineData("refresh", 407)]
    [InlineData("timestamp", 407)]
    [InlineData("access", 500)]
    [InlineData("refresh", 500)]
    [InlineData("timestamp", 500)]
    public async Task Google_refresh_then_catalog_response_only_applies_to_the_sent_credential(string rotation, int status)
    {
        await SeedAsync("google");
        await _db.Client.Updateable<GoogleAccount>()
            .SetColumns(a => new GoogleAccount { TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) })
            .Where(a => a.Id == _id).ExecuteCommandAsync();
        var oauth = new RefreshGoogleOAuth();
        DateTimeOffset? refreshedAt = null;
        DateTimeOffset? replacementTime = null;
        using var handler = new Handler((HttpStatusCode)status, async () =>
        {
            // The fetcher runs after the real refresh service has persisted both tokens.
            var refreshed = await _db.Client.Queryable<GoogleAccount>().InSingleAsync(_id);
            refreshed.AccessToken.Should().Be("refreshed-access");
            refreshed.RefreshToken.Should().Be("refreshed-refresh");
            refreshed.LastRefreshAt.Should().NotBeNull();
            refreshedAt = refreshed.LastRefreshAt;
            (await _db.Client.Queryable<Site>().InSingleAsync(_siteId)).ApiKey.Should().Be("refreshed-access");
            switch (rotation)
            {
                case "access":
                    await _db.Client.Updateable<GoogleAccount>().SetColumns(a => new GoogleAccount { AccessToken = "rotated-access" }).Where(a => a.Id == _id).ExecuteCommandAsync();
                    await _db.Client.Updateable<Site>().SetColumns(s => new Site { ApiKey = "rotated-access" }).Where(s => s.Id == _siteId).ExecuteCommandAsync();
                    break;
                case "refresh":
                    await _db.Client.Updateable<GoogleAccount>().SetColumns(a => new GoogleAccount { RefreshToken = "rotated-refresh" }).Where(a => a.Id == _id).ExecuteCommandAsync();
                    break;
                case "timestamp":
                    replacementTime = refreshedAt!.Value.AddHours(1);
                    await _db.Client.Updateable<GoogleAccount>().SetColumns(a => new GoogleAccount { LastRefreshAt = replacementTime }).Where(a => a.Id == _id).ExecuteCommandAsync();
                    break;
            }
        }, "403 forbidden");
        using var client = new HttpClient(handler);
        var controller = GoogleController(new GoogleModelFetcher(client), oauth);
        if (status is 401 or 403)
        {
            var result = await controller.FetchModels(_id, CancellationToken.None);
            result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(status);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<HttpRequestException>(() => controller.FetchModels(_id, CancellationToken.None));
            exception.StatusCode.Should().Be((HttpStatusCode)status);
        }
        oauth.Calls.Should().Be(1);
        oauth.SentRefreshToken.Should().Be("old-refresh");
        handler.SentToken.Should().Be("refreshed-access");
        var account = await _db.Client.Queryable<GoogleAccount>().InSingleAsync(_id);
        account.AccessToken.Should().Be(rotation == "access" ? "rotated-access" : "refreshed-access");
        account.RefreshToken.Should().Be(rotation == "refresh" ? "rotated-refresh" : "refreshed-refresh");
        account.LastRefreshAt.Should().Be(rotation == "timestamp" ? replacementTime : refreshedAt);
        var disabled = rotation == "none" && (status is 401 or 403);
        account.IsEnabled.Should().Be(!disabled);
        account.DisabledByUpstream.Should().Be(rotation == "none" && status == 403);
        account.RequiresReauth.Should().Be(rotation == "none" && status == 401);
        var site = await _db.Client.Queryable<Site>().InSingleAsync(_siteId);
        site.IsEnabled.Should().Be(!disabled);
        site.ApiKey.Should().Be(rotation == "access" ? "rotated-access" : "refreshed-access");
    }

    [Fact]
    public async Task Google_500_with_forbidden_body_preserves_account_and_reports_actual_status()
    {
        await SeedAsync("google");
        using var client = new HttpClient(new Handler(HttpStatusCode.InternalServerError, () => Task.CompletedTask, "403 forbidden"));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => FetchAsync("google", client));
        exception.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        exception.Message.Should().Contain("403 forbidden");
        await AssertStateAsync("google", true, "none");
    }

    [Fact]
    public async Task Google_actual_403_disables_account_and_site()
    {
        await SeedAsync("google");
        using var client = new HttpClient(new Handler(HttpStatusCode.Forbidden, () => Task.CompletedTask, "no status text"));
        var result = await FetchAsync("google", client);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        var account = await _db.Client.Queryable<GoogleAccount>().InSingleAsync(_id);
        account.IsEnabled.Should().BeFalse();
        account.DisabledByUpstream.Should().BeTrue();
        account.RequiresReauth.Should().BeFalse();
        account.AccessToken.Should().Be("old-access");
        var site = await _db.Client.Queryable<Site>().InSingleAsync(_siteId);
        site.IsEnabled.Should().BeFalse();
        site.ApiKey.Should().Be("old-access");
    }

    [Fact]
    public async Task Google_403_with_unreadable_body_still_disables_account_and_site()
    {
        await SeedAsync("google");
        using var client = new HttpClient(new Handler(HttpStatusCode.Forbidden, () => Task.CompletedTask, content: new ThrowingContent(new TaskCanceledException("simulated read timeout"))));
        var result = await FetchAsync("google", client);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        var account = await _db.Client.Queryable<GoogleAccount>().InSingleAsync(_id);
        account.IsEnabled.Should().BeFalse();
        account.DisabledByUpstream.Should().BeTrue();
        account.RequiresReauth.Should().BeFalse();
        account.AccessToken.Should().Be("old-access");
        var site = await _db.Client.Queryable<Site>().InSingleAsync(_siteId);
        site.IsEnabled.Should().BeFalse();
        site.ApiKey.Should().Be("old-access");
    }

    [Theory]
    [InlineData("fetchAvailableModels returned 403: forbidden", true)]
    [InlineData("fetchAvailableModels returned 500: 403 forbidden", false)]
    [InlineData("unrelated 403 forbidden", false)]
    public async Task Google_legacy_model_fetcher_compatibility_is_limited_to_exact_403_format(string message, bool forbidden)
    {
        await SeedAsync("google");
        var controller = GoogleController(new LegacyGoogleFetcher(message));
        if (forbidden)
        {
            (await controller.FetchModels(_id, CancellationToken.None)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
            var account = await _db.Client.Queryable<GoogleAccount>().InSingleAsync(_id);
            account.IsEnabled.Should().BeFalse();
            account.DisabledByUpstream.Should().BeTrue();
            (await _db.Client.Queryable<Site>().InSingleAsync(_siteId)).IsEnabled.Should().BeFalse();
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.FetchModels(_id, CancellationToken.None));
            await AssertStateAsync("google", true, "none");
        }
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Successful_catalog_fetch_leaves_credentials_enabled(string provider)
    {
        await SeedAsync(provider);
        var body = provider switch
        {
            "codex" => "{\"models\":[{\"slug\":\"test-model\"}]}",
            "google" => "{\"models\":{\"test-model\":{}}}",
            _ => "{\"data\":[{\"id\":\"test-model\"}]}"
        };
        using var handler = new Handler(HttpStatusCode.OK, () => Task.CompletedTask, body);
        using var client = new HttpClient(handler);
        var result = (await FetchAsync(provider, client)).Should().BeOfType<OkObjectResult>().Subject;
        System.Text.Json.JsonSerializer.Serialize(result.Value).Should().Contain("test-model");
        handler.SentToken.Should().Be("old-access");
        await AssertStateAsync(provider, true, "none");
    }

    private async Task SeedAsync(string provider)
    {
        await _db.InsertAsync(new Site { Id = _siteId, Name = "test", BaseUrl = "https://example.invalid", ApiKey = "old-access", IsEnabled = true });
        switch (provider)
        {
            case "codex": await _db.InsertAsync(new CodexAccount { Id = _id, LinkedSiteId = _siteId, AccessToken = "old-access", RefreshToken = "old-refresh", IsEnabled = true }); break;
            case "google": await _db.InsertAsync(new GoogleAccount { Id = _id, LinkedSiteId = _siteId, AccessToken = "old-access", RefreshToken = "old-refresh", IsEnabled = true, TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1) }); break;
            case "kimi": await _db.InsertAsync(new KimiAccount { Id = _id, LinkedSiteId = _siteId, AccessToken = "old-access", RefreshToken = "old-refresh", IsEnabled = true }); break;
            case "xai": await _db.InsertAsync(new XaiAccount { Id = _id, LinkedSiteId = _siteId, AccessToken = "old-access", RefreshToken = "old-refresh", IsEnabled = true }); break;
        }
    }

    private async Task RotateAsync(string provider, string rotation)
    {
        if (rotation == "none") return;
        // Change only one field to exercise each component of the snapshot independently.
        var access = rotation == "access" ? "new-access" : "old-access";
        var refresh = rotation == "refresh" ? "new-refresh" : "old-refresh";
        DateTimeOffset? timestamp = rotation == "timestamp" ? DateTimeOffset.UtcNow : null;
        switch (provider)
        {
            case "codex": await _db.Client.Updateable<CodexAccount>().SetColumns(a => new CodexAccount { AccessToken = access, RefreshToken = refresh, LastRefreshAt = timestamp }).Where(a => a.Id == _id).ExecuteCommandAsync(); break;
            case "google": await _db.Client.Updateable<GoogleAccount>().SetColumns(a => new GoogleAccount { AccessToken = access, RefreshToken = refresh, LastRefreshAt = timestamp }).Where(a => a.Id == _id).ExecuteCommandAsync(); break;
            case "kimi": await _db.Client.Updateable<KimiAccount>().SetColumns(a => new KimiAccount { AccessToken = access, RefreshToken = refresh, LastRefreshAt = timestamp }).Where(a => a.Id == _id).ExecuteCommandAsync(); break;
            case "xai": await _db.Client.Updateable<XaiAccount>().SetColumns(a => new XaiAccount { AccessToken = access, RefreshToken = refresh, LastRefreshAt = timestamp }).Where(a => a.Id == _id).ExecuteCommandAsync(); break;
        }
        if (rotation == "access")
            await _db.Client.Updateable<Site>().SetColumns(a => new Site { ApiKey = access }).Where(a => a.Id == _siteId).ExecuteCommandAsync();
    }

    private async Task AssertStateAsync(string provider, bool enabled, string rotation)
    {
        var (access, refresh, lastRefresh, accountEnabled, reauth) = provider switch
        {
            "codex" => State(await _db.Client.Queryable<CodexAccount>().InSingleAsync(_id)),
            "google" => State(await _db.Client.Queryable<GoogleAccount>().InSingleAsync(_id)),
            "kimi" => State(await _db.Client.Queryable<KimiAccount>().InSingleAsync(_id)),
            _ => State(await _db.Client.Queryable<XaiAccount>().InSingleAsync(_id))
        };
        access.Should().Be(rotation == "access" ? "new-access" : "old-access");
        refresh.Should().Be(rotation == "refresh" ? "new-refresh" : "old-refresh");
        if (rotation == "timestamp") lastRefresh.Should().NotBeNull();
        accountEnabled.Should().Be(enabled);
        reauth.Should().Be(!enabled);
        var site = await _db.Client.Queryable<Site>().InSingleAsync(_siteId);
        site.IsEnabled.Should().Be(enabled);
        site.ApiKey.Should().Be(rotation == "access" ? "new-access" : "old-access");
    }

    private static (string?, string?, DateTimeOffset?, bool, bool) State(CodexAccount a) => (a.AccessToken, a.RefreshToken, a.LastRefreshAt, a.IsEnabled, a.RequiresReauth);
    private static (string?, string?, DateTimeOffset?, bool, bool) State(GoogleAccount a) => (a.AccessToken, a.RefreshToken, a.LastRefreshAt, a.IsEnabled, a.RequiresReauth);
    private static (string?, string?, DateTimeOffset?, bool, bool) State(KimiAccount a) => (a.AccessToken, a.RefreshToken, a.LastRefreshAt, a.IsEnabled, a.RequiresReauth);
    private static (string?, string?, DateTimeOffset?, bool, bool) State(XaiAccount a) => (a.AccessToken, a.RefreshToken, a.LastRefreshAt, a.IsEnabled, a.RequiresReauth);

    private Task<IActionResult> FetchAsync(string provider, HttpClient client)
    {
        ControllerBase controller = provider switch
        {
            "codex" => new CodexApiController(_db, null!, null!, new CodexModelFetcher(client, new VersionResolver(), NullLogger<CodexModelFetcher>.Instance), null!, null!, null!, null!, null!, NullLogger<CodexApiController>.Instance, null!),
            "google" => GoogleController(new GoogleModelFetcher(client)),
            "kimi" => new KimiAccountsApiController(_db, null!, null!, new KimiModelFetcher(client, NullLogger<KimiModelFetcher>.Instance), null!, null!, NullLogger<KimiAccountsApiController>.Instance),
            _ => new XaiAccountsApiController(_db, null!, null!, new XaiModelFetcher(client, NullLogger<XaiModelFetcher>.Instance), null!, NullLogger<XaiAccountsApiController>.Instance)
        };
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = _scope.ServiceProvider } };
        return controller switch
        {
            CodexApiController c => c.FetchModels(_id, CancellationToken.None),
            GoogleAccountsApiController c => c.FetchModels(_id, CancellationToken.None),
            KimiAccountsApiController c => c.FetchModels(_id, CancellationToken.None),
            XaiAccountsApiController c => c.FetchModels(_id, CancellationToken.None),
            _ => throw new InvalidOperationException()
        };
    }

    private GoogleAccountsApiController GoogleController(IGoogleModelFetcher fetcher, IGoogleOAuthClient? oauth = null)
    {
        var refresh = new GoogleCredentialRefreshService(_db, oauth!, _scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>(), NullLogger<GoogleCredentialRefreshService>.Instance);
        return new GoogleAccountsApiController(_db, null!, null!, fetcher, null!, refresh, NullLogger<GoogleAccountsApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = _scope.ServiceProvider } }
        };
    }

    // 拒绝型 OAuth client：RefreshTokenAsync 一律抛"明确拒绝"（上游判定凭证失效），
    // 触发 recovery 的 Invalidate+401 收口路径；其余成员本测试不会触达。
    // 对齐 ec9c1c4 在 DeviceOAuthModelUnauthorizedIsolationTests 的容器适配思路。
    private static Task<T> Refused<T>() => Task.FromException<T>(new OAuthRefreshRefusedException("test: refresh token refused"));

    private sealed class RefusingCodexOAuthClient : ICodexOAuthClient
    {
        public (string State, string Verifier) CreateOAuthSession() => throw new NotSupportedException();
        public Task<string> BuildAuthorizeUrlAsync(string state, string verifier, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CodexTokenSet> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CodexTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken) => Refused<CodexTokenSet>();
    }

    private sealed class RefusingGoogleOAuthClient : IGoogleOAuthClient
    {
        public GoogleOAuthSession CreateSession() => throw new NotSupportedException();
        public string BuildAuthorizeUrl(string accountKind, GoogleOAuthSession session) => throw new NotSupportedException();
        public Task<GoogleTokenSet> ExchangeCodeAsync(string accountKind, string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<GoogleTokenSet> RefreshTokenAsync(string accountKind, string refreshToken, CancellationToken ct) => Refused<GoogleTokenSet>();
        public Task<string?> GetUserEmailAsync(string accessToken, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetUserProjectsAsync(string accessToken, CancellationToken ct) => throw new NotSupportedException();
        public Task<GoogleCodeAssistProfile> LoadCodeAssistProfileAsync(string accountKind, string accessToken, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RefusingKimiOAuthClient : IKimiOAuthClient
    {
        public Task<KimiDeviceCodeResponse> StartDeviceFlowAsync(string? deviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<KimiTokenExchangeResult> ExchangeDeviceCodeAsync(string deviceCode, string? deviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<KimiTokenSet> RefreshTokenAsync(string refreshToken, string? deviceId, CancellationToken ct) => Refused<KimiTokenSet>();
    }

    private sealed class RefusingXaiOAuthClient : IXaiOAuthClient
    {
        public Task<XaiDeviceCodeResponse> StartDeviceFlowAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<XaiTokenExchangeResult> ExchangeDeviceCodeAsync(string deviceCode, CancellationToken ct) => throw new NotSupportedException();
        public Task<XaiTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken ct)
            => Task.FromException<XaiTokenSet>(new XaiRefreshTokenInvalidException());
    }

    private sealed class RefreshGoogleOAuth : IGoogleOAuthClient
    {
        public int Calls { get; private set; }
        public string? SentRefreshToken { get; private set; }
        public Task<GoogleTokenSet> RefreshTokenAsync(string accountKind, string refreshToken, CancellationToken ct)
        {
            Calls++;
            SentRefreshToken = refreshToken;
            return Task.FromResult(new GoogleTokenSet { AccessToken = "refreshed-access", RefreshToken = "refreshed-refresh", ExpiresIn = 3600 });
        }
        public GoogleOAuthSession CreateSession() => throw new NotSupportedException();
        public string BuildAuthorizeUrl(string accountKind, GoogleOAuthSession session) => throw new NotSupportedException();
        public Task<GoogleTokenSet> ExchangeCodeAsync(string accountKind, string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetUserEmailAsync(string accessToken, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetUserProjectsAsync(string accessToken, CancellationToken ct) => throw new NotSupportedException();
        public Task<GoogleCodeAssistProfile> LoadCodeAssistProfileAsync(string accountKind, string accessToken, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class LegacyGoogleFetcher(string message) : IGoogleModelFetcher
    {
        public Task<IReadOnlyList<(string Slug, string DisplayName)>> FetchAsync(string accountKind, string accessToken, CancellationToken ct)
            => Task.FromException<IReadOnlyList<(string Slug, string DisplayName)>>(new InvalidOperationException(message));
    }

    private sealed class VersionResolver : ICodexClientVersionResolver
    {
        public Task<CodexClientVersionInfo> ResolveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CodexClientVersionInfo { ClientVersion = "1.0", UserAgent = "test/1.0", Source = "test" });
    }

    private sealed class Handler(HttpStatusCode status, Func<Task> onSend, string body = "proxy: returned 403 forbidden", HttpContent? content = null) : HttpMessageHandler
    {
        public string? SentToken { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentToken = request.Headers.Authorization?.Parameter;
            await onSend();
            return new HttpResponseMessage(status) { Content = content ?? new StringContent(body) };
        }
    }

    private sealed class ThrowingContent(Exception exceptionToThrow) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.FromException(exceptionToThrow);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    public void Dispose()
    {
        _scope.Dispose();
        _services.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_path + suffix); } catch (IOException) { }
    }
}
