using System.Net;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.Admin.IntegrationTests;

/// <summary>
/// 对话记录页面与 API 集成测试，验证 Admin 宿主下会话查询、对话轮次查询和删除功能。
/// <para>
/// 此测试从 AITool.IntegrationTests.Conversations.ConversationPageTests 拆分而来。
/// 原始测试同时覆盖了 /Admin/Chat 页面（仍留在 Web 宿主）和 /Admin/Conversations 页面及 API。
/// 由于 Chat 页面及其 ChatApiController 尚未迁移到 Admin 宿主（依赖 IProxyForwardService 等代理组件），
/// 本文件只覆盖已迁移到 Admin 的 Conversations 页面和 conversations API。
/// Chat 页面相关断言仍保留在 Web 端原始测试中。
/// </para>
/// </summary>
public sealed class ConversationPageTests
{
    /// <summary>
    /// 验证对话记录最小化布局页面包含渲染器初始化脚本和关键 UI 元素。
    /// </summary>
    [Fact]
    public async Task Get_conversations_minimal_page_contains_renderer_and_ui_elements()
    {
        await using var factory = new ConversationPageWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Conversations?layout=minimal");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, html);
        // 渲染器 link 方法确保前端能正常生成对话中的超链接
        html.Should().Contain("conversationRenderer.link = function");
        // URL 判断工具函数，用于区分 HTTP 链接和非链接文本
        html.Should().Contain("isConversationHttpUrl");
        // 消息元数据样式，展示时间、模型和 token 统计
        html.Should().Contain("conversation-msg-meta");
        // token 计数格式化，确保大数字能以 K/M/G 缩写展示
        html.Should().Contain("formatTokenCount");
        // 工具调用标题样式，用于区分工具调用和普通对话
        html.Should().Contain("conversation-tool-title");
        // 工具结果中的文件路径展示样式
        html.Should().Contain("conversation-tool-file");
        // 代码块最大高度限制，避免过长代码撑开页面
        html.Should().Contain("max-height: 520px");
        // Markdown 围栏修复，确保代码块和 JSON 之间的 ``` 不会导致渲染异常
        html.Should().Contain("normalizeMarkdownFenceBreaks");
        // 正则匹配行内 code 后紧跟反引号的场景，插入换行避免解析歧义
        html.Should().Contain("([^\\n])```");
        // 代码块语言标识中的 text 作为默认语言
        html.Should().Contain("code.text");
        // 工具参数展示判断，Edit/Write 等操作不展示参数以减少视觉噪音
        html.Should().Contain("shouldShowToolArguments");
        // 删除确认弹窗，使用自定义模态框替代浏览器原生 confirm
        html.Should().Contain("conversationDeleteModal");
        html.Should().Contain("showDeleteSessionModal");
        // 确保不使用 window.confirm 原生对话框
        html.Should().NotContain("window.confirm");
    }

    /// <summary>
    /// 验证按天查询会话列表能返回种子数据中的 claude-code 会话。
    /// </summary>
    [Fact]
    public async Task Get_sessions_by_day_returns_claude_code_session_with_token_stats()
    {
        await using var factory = new ConversationPageWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/conversations/sessions?rangeType=day&sourceTool=claude-code");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("claude-code");
        // 会话短标识来自 SessionId 前 8 位
        body.Should().Contain("4a101580");
        // Token 统计文本确保前端能展示用量信息
        body.Should().Contain("totalTokensText");
    }

    /// <summary>
    /// 验证 rangeType=all 时返回 400，因为全量查询会超过最大查询天数限制。
    /// </summary>
    [Fact]
    public async Task Get_sessions_with_rangeType_all_returns_bad_request()
    {
        await using var factory = new ConversationPageWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/conversations/sessions?rangeType=all&sourceTool=claude-code");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("最多只允许查询");
    }

    /// <summary>
    /// 验证按天查询对话轮次只返回今天的记录，不包含昨天的历史消息。
    /// </summary>
    [Fact]
    public async Task Get_turns_by_day_returns_today_record_without_yesterday()
    {
        await using var factory = new ConversationPageWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/conversations/turns?groupKey=claude-code%3A4a101580-d563-4945-aca8-76347b001a20");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        // 今天的用户输入内容
        body.Should().Contain("请帮我分析这个报错");
        // 默认按天查询不应返回昨天的消息
        body.Should().NotContain("昨天的历史消息");
        // 确保 UserCreatedAt 文本字段存在，用于前端展示消息时间
        body.Should().Contain("userCreatedAtText");
        // 工具调用标识和参数
        body.Should().Contain("工具调用: Edit");
        body.Should().Contain("\\\"action\\\":\\\"update\\\"");
        // Markdown 代码块中的 csharp 标记
        body.Should().Contain("```csharp");
    }

    /// <summary>
    /// 验证用自定义范围（覆盖多天）查询对话轮次能同时返回今天和昨天的记录。
    /// <para>
    /// 注意：原测试用 rangeType=week，但当测试运行日是周一时，本周一即为今天，
    /// 昨天属于上周会被排除，导致测试不稳定。改为显式 custom 范围（覆盖昨天 00:00 到明天 00:00），
    /// 避免对当前是星期几的依赖。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Get_turns_by_week_returns_both_today_and_yesterday_records()
    {
        await using var factory = new ConversationPageWebApplicationFactory();
        using var client = factory.CreateClient();

        // 显式指定覆盖昨天到今天的范围，不依赖今天是星期几。
        var today = DateTimeOffset.Now;
        var start = today.AddDays(-1).Date;
        var end = today.Date.AddDays(1);
        var response = await client.GetAsync($"/api/admin/conversations/turns?rangeType=custom&startTime={Uri.EscapeDataString(start.ToString("O"))}&endTime={Uri.EscapeDataString(end.ToString("O"))}&groupKey=claude-code%3A4a101580-d563-4945-aca8-76347b001a20");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        // 自定义范围应该包含昨天的历史消息
        body.Should().Contain("昨天的历史消息");
    }

    /// <summary>
    /// 验证删除会话后返回删除数量统计。
    /// </summary>
    [Fact]
    public async Task Delete_session_returns_deleted_count()
    {
        await using var factory = new ConversationPageWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync("/api/admin/conversations/sessions?groupKey=claude-code%3A4a101580-d563-4945-aca8-76347b001a20");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("deletedCount");
    }
}

/// <summary>
/// 用于构建 ConversationPageTests 对应的 Admin 测试宿主，并准备隔离的对话记录数据。
/// </summary>
internal sealed class ConversationPageWebApplicationFactory : WebApplicationFactory<AITool.Admin.AdminProgramMarker>
{
    /// <summary>
    /// 保存当前测试使用的临时数据库文件路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-conversations-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 重写测试宿主依赖，接入隔离数据库。
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
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
    /// 准备当前测试场景所需的对话记录数据，与 Web 端原始测试的种子数据保持一致。
    /// </summary>
    private async Task SeedAsync()
    {
        await IntegrationTestDbHelper.InitializeDatabaseAsync(Services);
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var today = DateTimeOffset.Now;
        var yesterday = today.AddDays(-1);

        // 显式插入系统运行时设置，确保 ConversationLogEnabled 为 true
        db.SystemRuntimeSettings.Add(new AITool.Domain.Operations.SystemRuntimeSettings
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
            ConversationLogEnabled = true
        });

        // 对话记录现在只走本地 JSONL 文件（不再写 DB 表），通过 IConversationLogStore 写入种子数据。
        var store = scope.ServiceProvider.GetRequiredService<AITool.Application.Conversations.IConversationLogStore>();
        await store.AppendBatchAsync(
        [
            new ConversationTurnLog
            {
                RequestId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                CreatedAt = today,
                UserCreatedAt = today,
                SourceTool = "claude-code",
                SessionId = "4a101580-d563-4945-aca8-76347b001a20",
                ConversationGroupKey = "claude-code:4a101580-d563-4945-aca8-76347b001a20",
                AccessKeyId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                RequestModel = "claude-sonnet-4-6",
                ProtocolType = "OpenAI",
                RequestPath = "/v1/messages",
                Source = "claude-code",
                UserInputText = "请帮我分析这个报错",
                AssistantOutputMarkdown = "工具调用: Edit\n{\"file\":\"Foo.cs\",\"action\":\"update\"}\n\n```csharp\nConsole.WriteLine(\"hello\");\n```",
                InputTokens = 10,
                CachedTokens = 0,
                OutputTokens = 20,
                IsStreaming = false,
                Status = "success",
                MetadataJson = "{}"
            },
            new ConversationTurnLog
            {
                RequestId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                CreatedAt = yesterday,
                UserCreatedAt = yesterday,
                SourceTool = "claude-code",
                SessionId = "4a101580-d563-4945-aca8-76347b001a20",
                ConversationGroupKey = "claude-code:4a101580-d563-4945-aca8-76347b001a20",
                AccessKeyId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                RequestModel = "claude-sonnet-4-6",
                ProtocolType = "OpenAI",
                RequestPath = "/v1/messages",
                Source = "claude-code",
                UserInputText = "昨天的历史消息",
                AssistantOutputMarkdown = "昨天的历史回复",
                InputTokens = 3,
                CachedTokens = 0,
                OutputTokens = 4,
                IsStreaming = false,
                Status = "success",
                MetadataJson = "{}"
            }
        ]);
    }
}
