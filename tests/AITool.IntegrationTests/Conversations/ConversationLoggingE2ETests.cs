using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Conversations;
using AITool.Application.Proxy;
using AITool.Domain.Models;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Conversations;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;

namespace AITool.IntegrationTests.Conversations;

/// <summary>
/// 验证真实 HTTP 链路下的对话记录落库结果。
/// </summary>
public sealed class ConversationLoggingE2ETests
{
    [Fact]
    public async Task Claude_code_responses_stream_request_should_persist_user_and_assistant_content()
    {
        await using var factory = new ConversationLoggingWebApplicationFactory();
        using var client = factory.CreateClient();

        var requestBody = """
{
  "model": "claude-code-test-model",
  "stream": true,
  "input": [
    {
      "role": "user",
      "content": [
        { "type": "input_text", "text": "请帮我检查当前改动" }
      ]
    }
  ]
}
""";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ConversationLoggingWebApplicationFactory.ProxyAccessKeyValue);
        request.Headers.UserAgent.ParseAdd("claude-cli/2.1.153 (external, claude-vscode, agent-sdk/0.3.153)");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("X-Claude-Code-Session-Id", "session-claude-e2e");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().Contain("response.output_text.delta");
        responseBody.Should().Contain("response.completed");

        await factory.AssertConversationAsync(log =>
            log.SourceTool == "claude-code"
            && log.ConversationGroupKey == "claude-code:session-claude-e2e"
            && log.RequestPath == "/v1/responses",
            log =>
            {
                log.Source.Should().Be("claude-code");
                log.IsStreaming.Should().BeTrue();
                log.ProtocolType.Should().Be("OpenAI");
                GzipTextCompression.Decompress(log.UserInputText).Should().Be("请帮我检查当前改动");
                GzipTextCompression.Decompress(log.AssistantOutputMarkdown).Should().Be("先看 diff，再给你结论");
            });
    }

    [Fact]
    public async Task Chat_send_stream_should_persist_chat_group_and_assistant_content()
    {
        await using var factory = new ConversationLoggingWebApplicationFactory();
        using var client = factory.CreateClient();

        var requestBody = JsonSerializer.Serialize(new
        {
            modelId = ConversationLoggingWebApplicationFactory.ChatModelId,
            mappingId = ConversationLoggingWebApplicationFactory.ChatMappingId,
            message = "请输出一段带增删改说明的结果",
            enableReasoning = false,
            enableStreaming = true,
            reasoningEffort = "high"
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/chat/send-stream")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().Contain("event: token");
        responseBody.Should().Contain("event: done");
        responseBody.Should().Contain("\\u65B0\\u589E Foo.cs");
        responseBody.Should().Contain("\\u5220\\u9664\\u65E7\\u5206\\u652F\\u5224\\u65AD");

        await factory.AssertConversationAsync(log =>
            log.SourceTool == "chat"
            && log.ConversationGroupKey == "chat"
            && log.RequestPath == "/api/admin/chat",
            log =>
            {
                log.Source.Should().Be("chat");
                log.IsStreaming.Should().BeTrue();
                log.ProtocolType.Should().Be("OpenAI");
                GzipTextCompression.Decompress(log.UserInputText).Should().Be("请输出一段带增删改说明的结果");
                var assistantOutput = GzipTextCompression.Decompress(log.AssistantOutputMarkdown);
                assistantOutput.Should().Contain("新增 Foo.cs");
                assistantOutput.Should().Contain("修改 Bar.cs");
                assistantOutput.Should().Contain("删除旧分支判断");
            });
    }
}

internal sealed class ConversationLoggingWebApplicationFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid ChatModelId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid ChatMappingId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static readonly Guid ProxyAccessKeyId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    internal const string ProxyAccessKeyValue = "aitool-e2e-key";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-conversation-e2e-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IProxyForwardService>();
            services.RemoveAll<IHttpClientFactory>();
            IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
            services.AddSingleton<IProxyForwardService, StubConversationProxyForwardService>();
            services.AddSingleton<ConversationLoggingStreamingHttpMessageHandler>();
            services.AddSingleton<IHttpClientFactory, ConversationLoggingFakeHttpClientFactory>();
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        SeedAsync().GetAwaiter().GetResult();
    }

    internal async Task AssertConversationAsync(Func<ConversationTurnLog, bool> predicate, Action<ConversationTurnLog> assertAction)
    {
        await using var scope = Services.CreateAsyncScope();
        var conversationLogStore = scope.ServiceProvider.GetRequiredService<IConversationLogStore>();

        ConversationTurnLog? log = null;
        for (var i = 0; i < 20; i++)
        {
            log = (await conversationLogStore.QueryAsync(new ConversationLogQuery
                {
                    StartTime = DateTimeOffset.Now.AddDays(-7),
                    EndTime = DateTimeOffset.Now.AddDays(1)
                }))
                .Where(x => predicate(x))
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefault();

            if (log is not null)
            {
                break;
            }

            await Task.Delay(100);
        }

        log.Should().NotBeNull();
        assertAction(log!);
    }

    private async Task SeedAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SqlSugarSetup.InitializeDatabase(db.Client);

        var siteId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        db.Sites.Add(new Site
        {
            Id = siteId,
            Name = "E2E Mock Site",
            BaseUrl = "http://127.0.0.1/mock-upstream",
            EndpointPathMode = "standard-root",
            ApiKey = "upstream-key",
            ProtocolType = "OpenAI",
            SupportsOpenAi = true,
            SupportsAnthropic = false,
            IsEnabled = true
        });

        db.ModelLibraryItems.Add(new ModelLibraryItem
        {
            Id = ChatModelId,
            ModelName = "claude-code-test-model",
            DisplayName = "Claude Code Test Model",
            IsEnabled = true
        });

        db.SiteModelMappings.Add(new SiteModelMapping
        {
            Id = ChatMappingId,
            SiteId = siteId,
            ModelLibraryItemId = ChatModelId,
            RemoteModelName = "claude-code-test-model",
            LastStatus = "ok",
            IsEnabled = true,
            MaxConcurrency = 0
        });

        db.ProxyRouteRules.Add(new ProxyRouteRule
        {
            Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            ExternalModelName = "claude-code-test-model",
            UpstreamModelName = "claude-code-test-model",
            SiteId = siteId,
            SiteModelName = "claude-code-test-model",
            Priority = 0,
            ModelPriority = 0,
            InstancePriority = 0,
            IsEnabled = true
        });

        db.ProxyAccessKeys.Add(new ProxyAccessKey
        {
            Id = ProxyAccessKeyId,
            KeyName = "e2e-key",
            PlainKey = ProxyAccessKeyValue,
            AccessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ProxyAccessKeyValue))),
            MaskedValue = "aito***-key",
            IsEnabled = true
        });

        await db.SaveChangesAsync();
    }
}

internal sealed class StubConversationProxyForwardService : IProxyForwardService
{
    public async Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        if (request.EnableStreaming)
        {
            var payload = string.Join(
                "\n",
                "event: response.output_text.delta",
                "data: {\"type\":\"response.output_text.delta\",\"delta\":\"先看 diff\"}",
                string.Empty,
                "event: response.output_text.delta",
                "data: {\"type\":\"response.output_text.delta\",\"delta\":\"，再给你结论\"}",
                string.Empty,
                "event: response.completed",
                "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_e2e\",\"status\":\"completed\",\"output\":[]}}",
                string.Empty,
                "data: [DONE]",
                string.Empty);

            return await Task.FromResult(new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = payload,
                InputTokens = 12,
                CachedTokens = 0,
                OutputTokens = 18,
                IsStreaming = true,
                HasStartedStreaming = true,
                FirstTokenLatencyMs = 5,
                StreamDurationMs = 10,
                TotalDurationMs = 15
            });
        }

        var responseBody = """
{
  "id": "chatcmpl-e2e",
  "object": "chat.completion",
  "created": 1710000000,
  "model": "claude-code-test-model",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "新增 Foo.cs\n修改 Bar.cs\n删除旧分支判断"
      },
      "finish_reason": "stop"
    }
  ],
  "usage": {
    "prompt_tokens": 12,
    "completion_tokens": 18,
    "prompt_tokens_details": {
      "cached_tokens": 0
    }
  }
}
""";

        return await Task.FromResult(new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = responseBody,
            InputTokens = 12,
            CachedTokens = 0,
            OutputTokens = 18,
            IsStreaming = false,
            TotalDurationMs = 10
        });
    }

    public async Task<ProxyForwardResult> ForwardStreamingAsync(
        ProxyForwardRequest request,
        Func<string, CancellationToken, Task> onSseDataAsync,
        CancellationToken cancellationToken = default)
    {
        var payload = string.Join(
            "\n",
            "event: response.output_text.delta",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"先看 diff\"}",
            string.Empty,
            "event: response.output_text.delta",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"，再给你结论\"}",
            string.Empty,
            "event: response.completed",
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_e2e\",\"status\":\"completed\",\"output\":[]}}",
            string.Empty,
            "data: [DONE]",
            string.Empty);

        foreach (var line in payload.Replace("\r\n", "\n").Split('\n'))
        {
            await onSseDataAsync(line, cancellationToken);
        }

        return new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = payload,
            InputTokens = 12,
            CachedTokens = 0,
            OutputTokens = 18,
            IsStreaming = true,
            HasStartedStreaming = true,
            FirstTokenLatencyMs = 5,
            StreamDurationMs = 10,
            TotalDurationMs = 15
        };
    }
}

internal sealed class ConversationLoggingFakeHttpClientFactory : IHttpClientFactory
{
    private readonly ConversationLoggingStreamingHttpMessageHandler _handler;

    public ConversationLoggingFakeHttpClientFactory(ConversationLoggingStreamingHttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name)
    {
        return new HttpClient(_handler, disposeHandler: false);
    }
}

internal sealed class ConversationLoggingStreamingHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var payload = string.Join(
            "\n",
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"新增 Foo.cs\\n\"}}]}",
            string.Empty,
            "data: {\"choices\":[{\"delta\":{\"content\":\"修改 Bar.cs\\n\"}}]}",
            string.Empty,
            "data: {\"choices\":[{\"delta\":{\"content\":\"删除旧分支判断\"}}],\"usage\":{\"prompt_tokens\":14,\"completion_tokens\":21,\"prompt_tokens_details\":{\"cached_tokens\":0}}}",
            string.Empty,
            "data: [DONE]",
            string.Empty);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };

        return Task.FromResult(response);
    }
}
