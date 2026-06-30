using System.Net;
using System.Security.Cryptography;
using System.Text;
using AITool.Application.Proxy;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.Auth;

/// <summary>
/// 后台登录鉴权集成测试，验证仅管理端受保护，代理路由仍只按访问密钥工作。
/// </summary>
public sealed class AdminAuthTests
{
    /// <summary>
    /// 验证未登录访问首页时会跳转到登录页。
    /// </summary>
    [Fact]
    public async Task Get_root_redirects_to_login_when_not_authenticated()
    {
        await using var factory = new AdminAuthWebApplicationFactory(passwordHash: ComputeMd5("admin123"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.OriginalString.Should().StartWith("/Login?returnUrl=");
    }

    /// <summary>
    /// 验证未配置后台密码时，登录页会提示先完成初始化设置。
    /// </summary>
    [Fact]
    public async Task Get_login_shows_setup_message_when_password_not_configured()
    {
        await using var factory = new AdminAuthWebApplicationFactory(passwordHash: string.Empty);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Login");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("&#x9996;&#x6B21;&#x8BBF;&#x95EE;&#xFF0C;&#x8BF7;&#x5148;&#x8BBE;&#x7F6E;&#x540E;&#x53F0;&#x767B;&#x5F55;&#x5BC6;&#x7801;&#x3002;");
        html.Should().Contain("保存并登录");
    }

    /// <summary>
    /// 验证未登录访问后台接口时会返回未授权。
    /// </summary>
    [Fact]
    public async Task Get_admin_api_returns_unauthorized_when_not_authenticated()
    {
        await using var factory = new AdminAuthWebApplicationFactory(passwordHash: ComputeMd5("admin123"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/api/admin/analytics/options");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// 验证代理路由仍只校验访问密钥，不会跳转到后台登录页。
    /// </summary>
    [Fact]
    public async Task Proxy_route_still_uses_access_key_without_login_redirect()
    {
        await using var factory = new AdminAuthWebApplicationFactory(passwordHash: ComputeMd5("admin123"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":64,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("auth-proxy-ok");
    }

    /// <summary>
    /// 计算后台密码配置要使用的 MD5 值。
    /// </summary>
    private static string ComputeMd5(string value)
    {
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

/// <summary>
/// 用于构建 AdminAuthWebApplicationFactory 对应的测试宿主，并准备隔离的测试数据。
/// </summary>
internal sealed class AdminAuthWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// 保存当前测试使用的临时数据库路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-admin-auth-{Guid.NewGuid():N}.db");
    /// <summary>
    /// 保存当前测试注入的后台密码哈希。
    /// </summary>
    private readonly string _passwordHash;
    /// <summary>
    /// 提供固定代理响应，供鉴权测试复用。
    /// </summary>
    private readonly AdminAuthFakeProxyForwardService _fakeForwardService = new();

    /// <summary>
    /// 创建后台鉴权测试宿主，并记录当前要使用的后台密码配置。
    /// </summary>
    public AdminAuthWebApplicationFactory(string passwordHash)
    {
        _passwordHash = passwordHash;
    }

    /// <summary>
    /// 配置后台鉴权测试所需的应用参数、服务和数据库。
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdminAuth:PasswordHash"] = _passwordHash,
                ["Server:Port"] = "0"
            });
        });
        builder.ConfigureServices(services =>
        {
            IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
            services.RemoveAll<IProxyForwardService>();
            services.AddSingleton<IProxyForwardService>(_fakeForwardService);
        });
    }

    /// <summary>
    /// 创建客户端后初始化当前测试场景的数据。
    /// </summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        SeedAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 准备当前测试场景所需的数据。
    /// </summary>
    private async Task SeedAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SqlSugarSetup.InitializeDatabase(db.Client);

        var siteId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var accessKeyRaw = "anthropic-test-key";
        var accessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyRaw)));

        db.Sites.Add(new Site
        {
            Id = siteId,
            Name = "Auth Test Site",
            BaseUrl = "https://anthropic-proxy.example.com",
            ApiKey = "site-anthropic-key",
            ProtocolType = "Anthropic",
            SupportsOpenAi = false,
            SupportsAnthropic = true,
            IsEnabled = true
        });

        db.ProxyAccessKeys.Add(new ProxyAccessKey
        {
            Id = Guid.Parse("99999999-9999-9999-9999-999999999999"),
            KeyName = "anthropic",
            PlainKey = accessKeyRaw,
            AccessKeyHash = accessKeyHash,
            MaskedValue = "sk-***ropic",
            IsEnabled = true
        });

        db.ProxyRouteEntries.Add(new ProxyRouteEntry
        {
            EntryName = "claude-proxy"
        });

        db.ProxyRouteRules.Add(new ProxyRouteRule
        {
            Id = Guid.Parse("88888888-8888-8888-8888-888888888888"),
            ExternalModelName = "claude-proxy",
            UpstreamModelName = "claude-3-7-sonnet",
            SiteId = siteId,
            SiteModelName = "claude-3-7-sonnet-real",
            Priority = 0,
            ModelPriority = 0,
            InstancePriority = 0,
            IsEnabled = true
        });

        // 单例表 Id=1 可能已被启动逻辑创建，先删除避免唯一约束冲突。


        db.Client.Deleteable<SystemRuntimeSettings>().Where(x => x.Id == 1).ExecuteCommand();


        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 11,
            ProxyRetryCount = 1,
            DetectionRequestTimeoutSeconds = 60,
            DetectionRetryCount = 0,
            DetectionConcurrency = 1,
            CircuitBreakerFailureThreshold = 5,
            CircuitBreakerRecoveryMinutes = 2,
            UsageLogRetentionDays = 7,
            UsageLogAutoCleanupEnabled = true
        });

        await db.SaveChangesAsync();
    }
}

/// <summary>
/// 用于模拟代理转发结果，支撑 AdminAuthFakeProxyForwardService 相关断言。
/// </summary>
internal sealed class AdminAuthFakeProxyForwardService : IProxyForwardService
{
    /// <summary>
    /// 返回固定的 Anthropic 成功响应，供代理鉴权场景断言。
    /// </summary>
    public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = "{\"id\":\"msg_auth\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-3-7-sonnet-real\",\"content\":[{\"type\":\"text\",\"text\":\"auth-proxy-ok\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":3,\"output_tokens\":2}}",
            InputTokens = 3,
            OutputTokens = 2
        });
    }

    /// <summary>
    /// 复用非流式结果，模拟流式转发也能成功返回。
    /// </summary>
    public Task<ProxyForwardResult> ForwardStreamingAsync(
        ProxyForwardRequest request,
        Func<string, CancellationToken, Task> onSseDataAsync,
        CancellationToken cancellationToken = default)
    {
        return ForwardAsync(request, cancellationToken);
    }
}
