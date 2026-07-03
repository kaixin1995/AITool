using AITool.Application.Conversations;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Conversations;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.IntegrationTests.Conversations;

/// <summary>
/// 针对 FileConversationLogStore 的内存上限回归测试。
/// 覆盖历史上“修复内存暴涨”时遗漏的场景：会话列表查询不传 GroupKey 时，
/// QueryAsync 仍必须在达到上限后停止读取，而不是把整个文件全量物化进内存。
/// </summary>
public sealed class FileConversationLogStoreMemoryTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"aitool-store-mem-{Guid.NewGuid():N}");
    private readonly FileConversationLogStore _store;

    public FileConversationLogStoreMemoryTests()
    {
        Directory.CreateDirectory(_rootPath);
        _store = new FileConversationLogStore(
            new ConversationLogFileOptions { RootPath = _rootPath },
            NullLogger<FileConversationLogStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootPath, recursive: true); } catch { /* 测试清理容忍 */ }
    }

    /// <summary>
    /// 写入远超 MaxQueryTurns 的记录后，不传 GroupKey 查询也必须只返回上限条数，
    /// 证明“达到上限立即停止”在列表查询路径上同样生效（历史 bug 根因）。
    /// </summary>
    [Fact]
    public async Task QueryAsync_without_group_key_stops_at_max_turns()
    {
        var now = DateTimeOffset.Now;
        var total = ConversationLogStoragePolicy.MaxQueryTurns + 800;

        var batch = Enumerable.Range(0, total).Select(i => new ConversationTurnLog
        {
            CreatedAt = now.AddSeconds(i),
            SourceTool = "chat",
            SessionId = $"session-{i}",
            ConversationGroupKey = $"chat:session-{i}",
            RequestModel = "test-model",
            UserInputText = $"用户输入 {i}",
            AssistantOutputMarkdown = $"回复 {i}",
            InputTokens = 1,
            OutputTokens = 1
        }).ToList();

        await _store.AppendBatchAsync(batch);

        var results = await _store.QueryAsync(new ConversationLogQuery
        {
            StartTime = now.AddSeconds(-1),
            EndTime = now.AddSeconds(total + 10)
            // 注意：不传 GroupKey，这是历史 bug 触发条件
        });

        results.Count.Should().Be(ConversationLogStoragePolicy.MaxQueryTurns,
            "不传 GroupKey 的列表查询同样必须受 MaxQueryTurns 上限约束，否则会全量物化导致内存暴涨");
    }

    /// <summary>
    /// 不传 GroupKey 时即使有多个会话，也不应超过上限。
    /// </summary>
    [Fact]
    public async Task QueryAsync_without_group_key_caps_results_even_with_single_session()
    {
        var now = DateTimeOffset.Now;
        var total = ConversationLogStoragePolicy.MaxQueryTurns + 200;
        var groupKey = "chat:single-busy-session";

        var batch = Enumerable.Range(0, total).Select(i => new ConversationTurnLog
        {
            CreatedAt = now.AddSeconds(i),
            SourceTool = "chat",
            SessionId = "single-busy-session",
            ConversationGroupKey = groupKey,
            UserInputText = $"输入 {i}",
            AssistantOutputMarkdown = $"输出 {i}",
            InputTokens = 2,
            OutputTokens = 3
        }).ToList();

        await _store.AppendBatchAsync(batch);

        var results = await _store.QueryAsync(new ConversationLogQuery
        {
            StartTime = now.AddSeconds(-1),
            EndTime = now.AddSeconds(total + 10)
        });

        results.Count.Should().Be(ConversationLogStoragePolicy.MaxQueryTurns);
    }

    /// <summary>
    /// QuerySessionSummariesAsync 流式聚合应返回正确的会话摘要（轮数、token、首条输入），
    /// 且按最近活动时间倒序排列。
    /// </summary>
    [Fact]
    public async Task QuerySessionSummariesAsync_aggregates_sessions_without_full_records()
    {
        var baseTime = DateTimeOffset.Now;

        await _store.AppendBatchAsync(
        [
            new ConversationTurnLog
            {
                CreatedAt = baseTime,
                SourceTool = "claude-code",
                SessionId = "session-a",
                ConversationGroupKey = "claude-code:session-a",
                ConversationTitle = string.Empty,
                UserInputText = "第一会话首条输入",
                AssistantOutputMarkdown = "回复A1",
                InputTokens = 10,
                CachedTokens = 5,
                OutputTokens = 20
            },
            new ConversationTurnLog
            {
                CreatedAt = baseTime.AddMinutes(10),
                SourceTool = "claude-code",
                SessionId = "session-a",
                ConversationGroupKey = "claude-code:session-a",
                ConversationTitle = "自定义标题A",
                UserInputText = "第二输入",
                AssistantOutputMarkdown = "回复A2",
                InputTokens = 30,
                OutputTokens = 40
            },
            new ConversationTurnLog
            {
                CreatedAt = baseTime.AddMinutes(5),
                SourceTool = "chat",
                SessionId = "session-b",
                ConversationGroupKey = "chat:session-b",
                UserInputText = "另一会话输入",
                AssistantOutputMarkdown = "回复B1",
                InputTokens = 1,
                OutputTokens = 1
            }
        ]);

        var summaries = await _store.QuerySessionSummariesAsync(new ConversationLogQuery
        {
            StartTime = baseTime.AddSeconds(-1),
            EndTime = baseTime.AddHours(1)
        });

        summaries.Should().HaveCount(2);
        // 最近活动时间倒序：session-a 最后活动在 +10 分钟，应排第一。
        summaries[0].GroupKey.Should().Be("claude-code:session-a");
        summaries[0].TurnCount.Should().Be(2);
        summaries[0].TotalTokens.Should().Be(10 + 5 + 20 + 30 + 40);
        summaries[0].LastActivityAt.Should().Be(baseTime.AddMinutes(10));
        summaries[0].SourceTool.Should().Be("claude-code");
        summaries[0].SessionId.Should().Be("session-a");
        // 首条非空自定义标题应被保留。
        summaries[0].ConversationTitle.Should().Be("自定义标题A");
        // 首条用户输入的压缩原文（此处短于压缩阈值，直接是明文）。
        summaries[0].FirstUserInputTextCompressed.Should().Be("第一会话首条输入");

        summaries[1].GroupKey.Should().Be("chat:session-b");
        summaries[1].TurnCount.Should().Be(1);
    }

    /// <summary>
    /// QuerySessionSummariesAsync 应尊重 SourceTool 等过滤条件。
    /// </summary>
    [Fact]
    public async Task QuerySessionSummariesAsync_respects_source_tool_filter()
    {
        var now = DateTimeOffset.Now;
        await _store.AppendBatchAsync(
        [
            new ConversationTurnLog
            {
                CreatedAt = now,
                SourceTool = "chat",
                SessionId = "s1",
                ConversationGroupKey = "chat:s1",
                UserInputText = "chat 输入",
                AssistantOutputMarkdown = "r"
            },
            new ConversationTurnLog
            {
                CreatedAt = now,
                SourceTool = "proxy",
                SessionId = "s2",
                ConversationGroupKey = "proxy:s2",
                UserInputText = "proxy 输入",
                AssistantOutputMarkdown = "r"
            }
        ]);

        var summaries = await _store.QuerySessionSummariesAsync(new ConversationLogQuery
        {
            StartTime = now.AddSeconds(-1),
            EndTime = now.AddMinutes(1),
            SourceTool = "chat"
        });

        summaries.Should().ContainSingle();
        summaries[0].GroupKey.Should().Be("chat:s1");
    }
}
