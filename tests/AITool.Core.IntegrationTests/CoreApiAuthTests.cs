using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AITool.Core.IntegrationTests;

/// <summary>
/// 跨宿主共享密钥鉴权矩阵（CoreApiAuthMiddleware）。
/// 生产环境 + 显式密钥：无/错密钥 401、对密钥 200；/v1 与 /health 不受影响；Testing 环境全放行。
/// </summary>
public sealed class CoreApiAuthTests
{
    private const string TestSecret = "test-shared-secret-123456";

    /// <summary>
    /// 配置了密钥时，无密钥头请求 /api/core/* 应 401。
    /// </summary>
    [Fact]
    public async Task Api_core_without_secret_returns_401()
    {
        await using var factory = new CoreApiAuthWebApplicationFactory(TestSecret);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/core/config/status");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// 配置了密钥时，错误密钥头请求 /api/core/* 应 401。
    /// </summary>
    [Fact]
    public async Task Api_core_with_wrong_secret_returns_401()
    {
        await using var factory = new CoreApiAuthWebApplicationFactory(TestSecret);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Core-Auth", "wrong-secret");

        var response = await client.GetAsync("/api/core/config/status");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// 配置了密钥时，正确密钥头请求 /api/core/* 应放行（200 而非 401）。
    /// </summary>
    [Fact]
    public async Task Api_core_with_correct_secret_is_allowed()
    {
        await using var factory = new CoreApiAuthWebApplicationFactory(TestSecret);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Core-Auth", TestSecret);

        var response = await client.GetAsync("/api/core/config/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// 鉴权失败应返回统一的 invalid_core_auth 错误结构（便于调用方区分 401 与网络不可达）。
    /// </summary>
    [Fact]
    public async Task Api_core_unauthorized_returns_unified_error_shape()
    {
        await using var factory = new CoreApiAuthWebApplicationFactory(TestSecret);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/core/config/status");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("invalid_core_auth");
        body.Should().Contain("X-Core-Auth");
    }

    /// <summary>
    /// 密钥对 /v1 代理端点零影响：仍由 AccessKey 自校验，返回自身的 invalid_access_key 而非中间件 401。
    /// </summary>
    [Fact]
    public async Task V1_proxy_is_not_affected_by_core_auth()
    {
        await using var factory = new CoreApiAuthWebApplicationFactory(TestSecret);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "auto", messages = new[] { new { role = "user", content = "hi" } } });
        var body = await response.Content.ReadAsStringAsync();

        // 未带 AccessKey → 控制器层 401（invalid_access_key），而非中间件 401（invalid_core_auth）。
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        body.Should().Contain("invalid_access_key");
        body.Should().NotContain("invalid_core_auth");
    }

    /// <summary>
    /// /health 保持匿名可访问。
    /// </summary>
    [Fact]
    public async Task Health_remains_anonymous()
    {
        await using var factory = new CoreApiAuthWebApplicationFactory(TestSecret);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// 未配置密钥时回环访问放行（生产环境，同机部署语义）。
    /// </summary>
    [Fact]
    public async Task Loopback_is_allowed_when_secret_not_configured()
    {
        await using var factory = new CoreApiAuthWebApplicationFactory(secret: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/core/config/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Testing 环境全放行（存量集成测试的兼容语义）：配置了密钥也不拦截。
    /// </summary>
    [Fact]
    public async Task Testing_environment_bypasses_always()
    {
        await using var factory = new CoreApiAuthWebApplicationFactory(TestSecret, testing: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/core/config/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// 握手端点（POST）同样受密钥保护。
    /// </summary>
    [Fact]
    public async Task Handshake_endpoint_requires_secret()
    {
        await using var factory = new CoreApiAuthWebApplicationFactory(TestSecret);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/core/config/handshake",
            new { adminInstanceId = "auth-test", appliedConfigVersion = 0L, adminBuild = "1.0.1.22" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// SSE 事件流端点同样受密钥保护（无密钥连接应 401，而非开始流）。
    /// </summary>
    [Fact]
    public async Task Event_stream_requires_secret()
    {
        await using var factory = new CoreApiAuthWebApplicationFactory(TestSecret);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/core/events/stream");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// SSE 事件流带正确密钥应建立连接（返回 200）。
    /// </summary>
    [Fact]
    public async Task Event_stream_with_secret_connects()
    {
        await using var factory = new CoreApiAuthWebApplicationFactory(TestSecret);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Core-Auth", TestSecret);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.GetAsync("/api/core/events/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>
/// 生产环境 + 可注入密钥的 Core 宿主测试工厂。
/// 非 Testing 环境使鉴权中间件真实生效；secret 为 null 时模拟「未配置密钥」退化场景。
/// </summary>
public sealed class CoreApiAuthWebApplicationFactory : WebApplicationFactory<AITool.Core.CoreProgramMarker>
{
    private readonly string? _secret;
    private readonly bool _testing;

    public CoreApiAuthWebApplicationFactory(string? secret, bool testing = false)
    {
        _secret = secret;
        _testing = testing;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_testing)
        {
            builder.UseEnvironment("Testing");
        }
        else
        {
            builder.UseEnvironment("Production");
        }

        if (_secret is not null)
        {
            builder.UseSetting("CoreAuth:SharedSecret", _secret);
        }
    }
}