using System.Net;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.Admin.IntegrationTests;

/// <summary>
/// 对话测试页面冒烟测试，验证 Chat/Index 页面从 Web 宿主迁移到 Admin 宿主后能正常渲染。
/// <para>
/// Chat 页面仅依赖 <see cref="ISystemRuntimeSettingsService"/>，该服务已在 Admin DI 中注册。
/// 页面中的 JavaScript 调用的 /api/admin/chat/* 端点仍由 Core 宿主提供，本测试仅验证
/// 页面本身的 GET 请求能正常返回 HTML。
/// </para>
/// </summary>
public sealed class ChatPageTests
{
    /// <summary>
    /// 验证 Chat 页面能正常渲染，返回 HTTP 200 并包含对话测试的核心 UI 元素。
    /// </summary>
    [Fact]
    public async Task Get_chat_page_returns_ok_with_chat_ui_elements()
    {
        await using var factory = new ChatPageWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Chat");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, html);
        // 对话测试页签
        html.Should().Contain("对话测试");
        // 模型搜索框
        html.Should().Contain("modelSearchInput");
        // 站点模型选择器
        html.Should().Contain("targetSelect");
        // 流式开关
        html.Should().Contain("streamingToggle");
        // 思考模式开关
        html.Should().Contain("reasoningToggle");
        // 发送按钮
        html.Should().Contain("btnSend");
        // 消息容器
        html.Should().Contain("chatMessages");
    }

    /// <summary>
    /// 验证当 ConversationLogEnabled 为 true 时，页面包含对话记录页签和 iframe。
    /// </summary>
    [Fact]
    public async Task Get_chat_page_with_log_enabled_shows_conversation_log_tab()
    {
        await using var factory = new ChatPageWebApplicationFactory(conversationLogEnabled: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Chat");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, html);
        html.Should().Contain("对话记录");
        html.Should().Contain("id=\"conversationLogTab\"");
        html.Should().Contain("data-bs-target=\"#conversationLogPane\"");
        html.Should().Contain("/Admin/Conversations?layout=minimal");
    }

    /// <summary>
    /// 验证当 ConversationLogEnabled 为 false 时，页面不包含对话记录页签。
    /// </summary>
    [Fact]
    public async Task Get_chat_page_with_log_disabled_hides_conversation_log_tab()
    {
        await using var factory = new ChatPageWebApplicationFactory(conversationLogEnabled: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Chat");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, html);
        // 页签按钮的 HTML id 属性不应存在（JS 变量名始终存在，所以必须检查 HTML 属性形式）
        html.Should().NotContain("id=\"conversationLogTab\"");
        html.Should().NotContain("data-bs-target=\"#conversationLogPane\"");
        // iframe 的 src 属性不应指向对话记录页面
        html.Should().NotContain("/Admin/Conversations?layout=minimal");
    }
}

/// <summary>
/// 用于构建 ChatPageTests 对应的 Admin 测试宿主。
/// </summary>
internal sealed class ChatPageWebApplicationFactory : WebApplicationFactory<AITool.Admin.AdminProgramMarker>
{
    /// <summary>
    /// 保存当前测试使用的临时数据库文件路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-chat-page-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 是否启用对话记录。
    /// </summary>
    private readonly bool _conversationLogEnabled;

    /// <summary>
    /// 创建 Chat 页面测试工厂，可控制对话记录开关。
    /// </summary>
    public ChatPageWebApplicationFactory(bool conversationLogEnabled = true)
    {
        _conversationLogEnabled = conversationLogEnabled;
    }

    /// <summary>
    /// 重写测试宿主依赖，接入隔离数据库。
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        });
    }

    /// <summary>
    /// 在客户端配置完成后执行测试数据初始化。
    /// </summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        SeedAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 准备 Chat 页面测试所需的最小数据：仅插入系统运行时设置以控制对话记录开关。
    /// </summary>
    private async Task SeedAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 9,
            ProxyRetryCount = 2,
            DetectionRequestTimeoutSeconds = 60,
            DetectionRetryCount = 0,
            DetectionConcurrency = 1,
            CircuitBreakerFailureThreshold = 5,
            CircuitBreakerRecoveryMinutes = 2,
            UsageLogRetentionDays = 7,
            UsageLogAutoCleanupEnabled = true,
            ConversationLogEnabled = _conversationLogEnabled
        });

        await db.SaveChangesAsync();
    }
}
