using System.Net;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.Conversations;

/// <summary>
/// 验证对话记录页面与接口可正常读取结构化对话。
/// </summary>
public sealed class ConversationPageTests
{
    [Fact]
    public async Task Get_conversations_page_and_api_returns_session_and_turns()
    {
        await using var factory = new ConversationPageWebApplicationFactory();
        using var client = factory.CreateClient();

        var pageResponse = await client.GetAsync("/Admin/Chat");
        var pageHtml = await pageResponse.Content.ReadAsStringAsync();
        pageResponse.StatusCode.Should().Be(HttpStatusCode.OK, pageHtml);
        pageHtml.Should().Contain("对话测试");
        pageHtml.Should().Contain("对话记录");
        pageHtml.Should().Contain("conversationLogIframe");
        pageHtml.Should().Contain("/Admin/Conversations?layout=minimal");
        pageHtml.Should().Contain("appWrapper");
        pageHtml.Should().Contain("sidebarCollapseToggle");
        pageHtml.Should().Contain("sidebar-collapsed");
        pageHtml.Should().Contain("sidebar-brand-main");
        pageHtml.Should().Contain("aitool.sidebarCollapsed");

        var minimalPageResponse = await client.GetAsync("/Admin/Conversations?layout=minimal");
        var minimalPageHtml = await minimalPageResponse.Content.ReadAsStringAsync();
        minimalPageResponse.StatusCode.Should().Be(HttpStatusCode.OK, minimalPageHtml);
        minimalPageHtml.Should().Contain("conversationRenderer.link = function");
        minimalPageHtml.Should().Contain("isConversationHttpUrl");
        minimalPageHtml.Should().Contain("conversation-msg-meta");
        minimalPageHtml.Should().Contain("formatTokenCount");
        minimalPageHtml.Should().Contain("conversation-tool-title");
        minimalPageHtml.Should().Contain("conversation-tool-file");
        minimalPageHtml.Should().Contain("max-height: 520px");
        minimalPageHtml.Should().Contain("normalizeMarkdownFenceBreaks");
        minimalPageHtml.Should().Contain("([^\\n])```");
        minimalPageHtml.Should().Contain("code.text");
        minimalPageHtml.Should().Contain("shouldShowToolArguments");
        minimalPageHtml.Should().Contain("conversationDeleteModal");
        minimalPageHtml.Should().Contain("showDeleteSessionModal");
        minimalPageHtml.Should().NotContain("window.confirm");

        var sessionsResponse = await client.GetAsync("/api/admin/conversations/sessions?rangeType=day&sourceTool=claude-code");
        var sessionsBody = await sessionsResponse.Content.ReadAsStringAsync();
        sessionsResponse.StatusCode.Should().Be(HttpStatusCode.OK, sessionsBody);
        sessionsBody.Should().Contain("claude-code");
        sessionsBody.Should().Contain("4a101580");
        sessionsBody.Should().Contain("totalTokensText");

        var invalidRangeResponse = await client.GetAsync("/api/admin/conversations/sessions?rangeType=all&sourceTool=claude-code");
        var invalidRangeBody = await invalidRangeResponse.Content.ReadAsStringAsync();
        invalidRangeResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest, invalidRangeBody);
        invalidRangeBody.Should().Contain("最多只允许查询");

        var turnsResponse = await client.GetAsync("/api/admin/conversations/turns?groupKey=claude-code%3A4a101580-d563-4945-aca8-76347b001a20");
        var turnsBody = await turnsResponse.Content.ReadAsStringAsync();
        turnsResponse.StatusCode.Should().Be(HttpStatusCode.OK, turnsBody);
        turnsBody.Should().Contain("请帮我分析这个报错");
        turnsBody.Should().NotContain("昨天的历史消息");
        turnsBody.Should().Contain("userCreatedAtText");
        turnsBody.Should().Contain("工具调用: Edit");
        turnsBody.Should().Contain("\\\"action\\\":\\\"update\\\"");
        turnsBody.Should().Contain("```csharp");

        // 用 custom 范围显式覆盖昨天到明天，避免 rangeType=week 在周一运行时昨天属上周被排除。
        var now = DateTimeOffset.Now;
        var customStart = now.AddDays(-2);
        var customEnd = now.AddDays(1);
        var allTurnsResponse = await client.GetAsync($"/api/admin/conversations/turns?rangeType=custom&startTime={Uri.EscapeDataString(customStart.ToString("O"))}&endTime={Uri.EscapeDataString(customEnd.ToString("O"))}&groupKey=claude-code%3A4a101580-d563-4945-aca8-76347b001a20");
        var allTurnsBody = await allTurnsResponse.Content.ReadAsStringAsync();
        allTurnsResponse.StatusCode.Should().Be(HttpStatusCode.OK, allTurnsBody);
        allTurnsBody.Should().Contain("昨天的历史消息");

        var deleteResponse = await client.DeleteAsync("/api/admin/conversations/sessions?groupKey=claude-code%3A4a101580-d563-4945-aca8-76347b001a20");
        var deleteBody = await deleteResponse.Content.ReadAsStringAsync();
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK, deleteBody);
        deleteBody.Should().Contain("deletedCount");
    }
}

internal sealed class ConversationPageWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-conversations-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        SeedAsync().GetAwaiter().GetResult();
    }

    private async Task SeedAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SqlSugarSetup.InitializeDatabase(db.Client);

        var today = DateTimeOffset.Now;
        var yesterday = today.AddDays(-1);

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
                UserInputText = "<system-reminder>\nNote: c:\\Users\\kaikai.hao\\Desktop\\AI-Tool\\src\\AITool.Web\\Program.cs was modified\n</system-reminder>\n\n请帮我分析这个报错",
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
