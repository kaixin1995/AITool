using AITool.Application.Conversations;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Retention;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AITool.ApplicationTests.Retention;

/// <summary>
/// 验证日志保留服务会按运行时配置删除过期日志，并把本次清理结果回写到设置表。
/// </summary>
public sealed class LogRetentionServiceTests : IDisposable
{
    /// <summary>
    /// 内存数据库上下文，用于准备测试数据并验证清理结果。
    /// </summary>
    private readonly AppDbContext _dbContext;
    /// <summary>
    /// 空实现的对话记录存储，供保留策略测试复用。
    /// </summary>
    private readonly IConversationLogStore _conversationLogStore = new NoopConversationLogStore();

    /// <summary>
    /// 被测服务，负责根据保留策略执行日志清理。
    /// </summary>
    private readonly LogRetentionService _service;

    /// <summary>
    /// 为每个测试创建独立的内存数据库，避免保留策略互相干扰。
    /// </summary>
    public LogRetentionServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);
        _service = new LogRetentionService(_dbContext, _conversationLogStore);
    }

    /// <summary>
    /// 开启自动清理后，服务应删除超过保留天数的日志，并记录本次删除数量和时间。
    /// </summary>
    [Fact]
    public async Task PruneAsync_uses_runtime_retention_settings_and_writes_back_prune_result()
    {
        // 这里显式写入运行时设置，模拟系统已启用按天数保留日志的场景。
        _dbContext.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            UsageLogRetentionDays = 3,
            UsageLogAutoCleanupEnabled = true
        });

        _dbContext.ProxyUsageLogs.AddRange(
            new ProxyUsageLog
            {
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                RequestModel = "gpt-5",
                AttemptedModel = "gpt-5",
                TargetSiteId = Guid.NewGuid(),
                Status = "fail",
                ErrorMessage = string.Empty,
                ReasoningEffort = string.Empty,
                InputTokens = 10,
                OutputTokens = 5,
                TotalTokens = 15,
                RequestedAt = DateTimeOffset.UtcNow.AddDays(-4)
            },
            new ProxyUsageLog
            {
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                RequestModel = "gpt-5",
                AttemptedModel = "gpt-5",
                TargetSiteId = Guid.NewGuid(),
                Status = "success",
                ErrorMessage = string.Empty,
                ReasoningEffort = string.Empty,
                InputTokens = 10,
                OutputTokens = 5,
                TotalTokens = 15,
                RequestedAt = DateTimeOffset.UtcNow.AddDays(-2)
            });

        await _dbContext.SaveChangesAsync();

        // 记录清理前时间，用来校验回写时间和边界是否合理。
        var beforePruneAt = DateTimeOffset.UtcNow;
        var result = await _service.PruneAsync();
        // 重新读取设置，确认服务已把统计结果写回数据库。
        var settings = await _dbContext.SystemRuntimeSettings.SingleAsync(x => x.Id == 1);

        result.UsageLogPrunedCount.Should().Be(1);
        _dbContext.ProxyUsageLogs.Should().ContainSingle();
        _dbContext.ProxyUsageLogs.Single().RequestedAt.Should().BeAfter(beforePruneAt.AddDays(-3).AddMinutes(-1));
        settings.LastUsageLogPrunedCount.Should().Be(1);
        settings.LastUsageLogPrunedAt.Should().NotBeNull();
        settings.LastUsageLogPrunedAt.Should().BeOnOrAfter(beforePruneAt);
    }

    /// <summary>
    /// 正好位于保留截止点上的日志不应被误删，避免边界判断过严。
    /// </summary>
    [Fact]
    public async Task PruneAsync_keeps_logs_at_exact_cutoff_boundary()
    {
        // 固定当前时间，确保截止点计算可重复、可断言。
        var baseTime = new DateTimeOffset(2026, 04, 28, 12, 00, 00, TimeSpan.Zero);
        // 通过注入时钟委托，让清理逻辑按固定时间执行。
        var service = new LogRetentionService(_dbContext, _conversationLogStore, () => baseTime);

        _dbContext.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            UsageLogRetentionDays = 3,
            UsageLogAutoCleanupEnabled = true
        });
        // 这条日志恰好处于保留窗口边界，用来验证比较条件是否包含等号。
        _dbContext.ProxyUsageLogs.Add(new ProxyUsageLog
        {
            AccessKeyId = Guid.NewGuid(),
            ProtocolType = "OpenAI",
            RequestModel = "gpt-5",
            AttemptedModel = "gpt-5",
            TargetSiteId = Guid.NewGuid(),
            Status = "success",
            ErrorMessage = string.Empty,
            ReasoningEffort = string.Empty,
            InputTokens = 10,
            OutputTokens = 5,
            TotalTokens = 15,
            RequestedAt = baseTime.AddDays(-3)
        });
        await _dbContext.SaveChangesAsync();

        var result = await service.PruneAsync();
        // 清理完成后重新查询设置，确认统计信息同步更新。
        var settings = await _dbContext.SystemRuntimeSettings.SingleAsync(x => x.Id == 1);

        result.UsageLogPrunedCount.Should().Be(0);
        _dbContext.ProxyUsageLogs.Should().ContainSingle();
        settings.LastUsageLogPrunedCount.Should().Be(0);
        settings.LastUsageLogPrunedAt.Should().Be(baseTime);
    }

    /// <summary>
    /// 自动清理关闭时，即使日志已经很旧，也不应触发删除。
    /// </summary>
    [Fact]
    public async Task PruneAsync_skips_cleanup_when_auto_cleanup_disabled()
    {
        // 固定当前时间，避免时间流逝影响断言结果。
        var baseTime = new DateTimeOffset(2026, 04, 28, 12, 00, 00, TimeSpan.Zero);
        // 使用固定时钟，验证禁用清理时回写的时间值是否稳定。
        var service = new LogRetentionService(_dbContext, _conversationLogStore, () => baseTime);

        _dbContext.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            UsageLogRetentionDays = 3,
            UsageLogAutoCleanupEnabled = false
        });
        // 放入一条明显超过保留期的日志，确认它不会被误删。
        _dbContext.ProxyUsageLogs.Add(new ProxyUsageLog
        {
            AccessKeyId = Guid.NewGuid(),
            ProtocolType = "OpenAI",
            RequestModel = "gpt-5",
            AttemptedModel = "gpt-5",
            TargetSiteId = Guid.NewGuid(),
            Status = "success",
            ErrorMessage = string.Empty,
            ReasoningEffort = string.Empty,
            RequestedAt = baseTime.AddDays(-10)
        });
        await _dbContext.SaveChangesAsync();

        var result = await service.PruneAsync();
        // 读取最新设置，确认虽然未删除日志，但仍记录了本次执行结果。
        var settings = await _dbContext.SystemRuntimeSettings.SingleAsync(x => x.Id == 1);

        result.UsageLogPrunedCount.Should().Be(0);
        _dbContext.ProxyUsageLogs.Should().ContainSingle();
        settings.LastUsageLogPrunedCount.Should().Be(0);
        settings.LastUsageLogPrunedAt.Should().Be(baseTime);
    }

    /// <summary>
    /// 释放测试使用的数据库上下文。
    /// </summary>
    public void Dispose() => _dbContext.Dispose();

    /// <summary>
    /// 保留策略测试不关心对话文件写入，这里提供一个空实现即可。
    /// </summary>
    private sealed class NoopConversationLogStore : IConversationLogStore
    {
        public Task AppendBatchAsync(IReadOnlyList<ConversationTurnLog> logs, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConversationTurnLog>> QueryAsync(ConversationLogQuery query, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ConversationTurnLog>>([]);
        }

        public Task<IReadOnlyList<ConversationSessionSummary>> QuerySessionSummariesAsync(ConversationLogQuery query, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ConversationSessionSummary>>([]);
        }

        public Task<int> DeleteSessionAsync(string groupKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<int> UpdateSessionTitleAsync(string groupKey, string title, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task PruneExpiredAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
