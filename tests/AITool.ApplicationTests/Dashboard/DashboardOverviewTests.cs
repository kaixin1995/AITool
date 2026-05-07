using AITool.Domain.Detection;
using AITool.Domain.Models;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AITool.ApplicationTests.Dashboard;

// 看板概览统计查询测试
public sealed class DashboardOverviewTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private Guid _enabledSite1Id;
    private Guid _enabledSite2Id;

    public DashboardOverviewTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);
    }

    [Fact]
    public async Task Overview_counts_enabled_sites_and_models()
    {
        // 预置站点、模型和映射数据，验证看板统计数据正确
        SeedDashboardData();

        // 模拟看板查询逻辑
        var enabledSiteCount = await _dbContext.Sites.CountAsync(s => s.IsEnabled);
        var modelCount = await _dbContext.ModelLibraryItems.CountAsync();
        var mappingCount = await _dbContext.SiteModelMappings.CountAsync();

        enabledSiteCount.Should().Be(2);
        modelCount.Should().Be(2);
        mappingCount.Should().Be(3);
    }

    [Fact]
    public async Task Overview_counts_recent_requests_and_success_rate()
    {
        // 预置使用日志，验证24小时内的调用统计与成功率
        SeedDashboardData();

        _dbContext.ProxyUsageLogs.AddRange(
            new ProxyUsageLog { AccessKeyId = Guid.NewGuid(), ProtocolType = "OpenAI", RequestModel = "gpt-5.4", AttemptedModel = "gpt-5.4", TargetSiteId = _enabledSite1Id, Status = "success", ErrorMessage = string.Empty, ReasoningEffort = string.Empty, IsFinalResult = true, RequestedAt = DateTimeOffset.UtcNow.AddHours(-1) },
            new ProxyUsageLog { AccessKeyId = Guid.NewGuid(), ProtocolType = "OpenAI", RequestModel = "gpt-5.4", AttemptedModel = "gpt-5.4", TargetSiteId = _enabledSite2Id, Status = "success", ErrorMessage = string.Empty, ReasoningEffort = string.Empty, IsFinalResult = true, RequestedAt = DateTimeOffset.UtcNow.AddHours(-2) },
            new ProxyUsageLog { AccessKeyId = Guid.NewGuid(), ProtocolType = "OpenAI", RequestModel = "gpt-5.4", AttemptedModel = "gpt-5.4", TargetSiteId = _enabledSite1Id, Status = "fail", ErrorMessage = "timeout", ReasoningEffort = string.Empty, IsFinalResult = true, RequestedAt = DateTimeOffset.UtcNow.AddHours(-3) },
            new ProxyUsageLog { AccessKeyId = Guid.NewGuid(), ProtocolType = "OpenAI", RequestModel = "gpt-5.4", AttemptedModel = "gpt-5.4", TargetSiteId = _enabledSite1Id, Status = "success", ErrorMessage = string.Empty, ReasoningEffort = string.Empty, IsFinalResult = false, RequestedAt = DateTimeOffset.UtcNow.AddHours(-1) },
            // 超过24小时的日志不应计入
            new ProxyUsageLog { AccessKeyId = Guid.NewGuid(), ProtocolType = "OpenAI", RequestModel = "gpt-5.4", AttemptedModel = "gpt-5.4", TargetSiteId = _enabledSite1Id, Status = "success", ErrorMessage = string.Empty, ReasoningEffort = string.Empty, IsFinalResult = true, RequestedAt = DateTimeOffset.UtcNow.AddHours(-25) }
        );
        await _dbContext.SaveChangesAsync();

        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        var enabledSiteIds = await _dbContext.Sites
            .Where(s => s.IsEnabled)
            .Select(s => s.Id)
            .ToListAsync();
        var recentRequests = (await _dbContext.ProxyUsageLogs.ToListAsync())
            .Where(x => x.IsFinalResult)
            .Where(x => x.RequestedAt >= cutoff)
            .Where(x => enabledSiteIds.Contains(x.TargetSiteId))
            .ToList();

        var recentRequestCount = recentRequests.Count;
        var recentSuccessCount = recentRequests.Count(d => d.Status == "success");
        var recentSuccessRate = (double)recentSuccessCount / recentRequestCount;

        recentRequestCount.Should().Be(3);
        recentSuccessRate.Should().BeApproximately(0.6667, 0.01);
    }

    [Fact]
    public async Task Overview_counts_enabled_detection_tasks()
    {
        // 预置检测任务，验证启用任务数统计正确
        _dbContext.DetectionTasks.AddRange(
            new DetectionTask { Name = "每30分钟检测", CronExpression = "*/30 * * * *", IsEnabled = true },
            new DetectionTask { Name = "每小时检测", CronExpression = "0 * * * *", IsEnabled = true },
            new DetectionTask { Name = "已禁用任务", CronExpression = "0 0 * * *", IsEnabled = false }
        );
        await _dbContext.SaveChangesAsync();

        var enabledTaskCount = await _dbContext.DetectionTasks.CountAsync(t => t.IsEnabled);
        enabledTaskCount.Should().Be(2);
    }

    // 预置站点、模型和映射的基础测试数据
    private void SeedDashboardData()
    {
        var site1 = new Site { Name = "站点A", BaseUrl = "https://a.com", ApiKey = "key1", IsEnabled = true };
        var site2 = new Site { Name = "站点B", BaseUrl = "https://b.com", ApiKey = "key2", IsEnabled = true };
        var site3 = new Site { Name = "站点C", BaseUrl = "https://c.com", ApiKey = "key3", IsEnabled = false };
        var model1 = new ModelLibraryItem { ModelName = "gpt-5.4", DisplayName = "GPT-5.4" };
        var model2 = new ModelLibraryItem { ModelName = "claude-4", DisplayName = "Claude-4" };

        _enabledSite1Id = site1.Id;
        _enabledSite2Id = site2.Id;

        _dbContext.Sites.AddRange(site1, site2, site3);
        _dbContext.ModelLibraryItems.AddRange(model1, model2);
        _dbContext.SiteModelMappings.AddRange(
            new SiteModelMapping { SiteId = site1.Id, ModelLibraryItemId = model1.Id, RemoteModelName = "gpt-5.4" },
            new SiteModelMapping { SiteId = site1.Id, ModelLibraryItemId = model2.Id, RemoteModelName = "claude-4" },
            new SiteModelMapping { SiteId = site2.Id, ModelLibraryItemId = model1.Id, RemoteModelName = "gpt-5.4" }
        );
        _dbContext.SaveChanges();
    }

    public void Dispose() => _dbContext.Dispose();
}
