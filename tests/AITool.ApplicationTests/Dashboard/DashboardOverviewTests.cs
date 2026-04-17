using AITool.Application.Dashboard;
using AITool.Domain.Detection;
using AITool.Domain.Models;
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
    public async Task Overview_counts_recent_detections_and_success_rate()
    {
        // 预置检测日志，验证24小时内的检测统计与成功率
        SeedDashboardData();

        // 添加检测日志
        _dbContext.DetectionLogs.AddRange(
            new DetectionLog { SiteId = Guid.NewGuid(), ModelLibraryItemId = Guid.NewGuid(), Status = "success", DurationMs = 100, CheckedAt = DateTimeOffset.UtcNow.AddHours(-1) },
            new DetectionLog { SiteId = Guid.NewGuid(), ModelLibraryItemId = Guid.NewGuid(), Status = "success", DurationMs = 200, CheckedAt = DateTimeOffset.UtcNow.AddHours(-2) },
            new DetectionLog { SiteId = Guid.NewGuid(), ModelLibraryItemId = Guid.NewGuid(), Status = "fail", DurationMs = 300, CheckedAt = DateTimeOffset.UtcNow.AddHours(-3) },
            // 超过24小时的日志不应计入
            new DetectionLog { SiteId = Guid.NewGuid(), ModelLibraryItemId = Guid.NewGuid(), Status = "success", DurationMs = 50, CheckedAt = DateTimeOffset.UtcNow.AddHours(-25) }
        );
        await _dbContext.SaveChangesAsync();

        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        var recentDetections = await _dbContext.DetectionLogs
            .Where(d => d.CheckedAt >= cutoff)
            .ToListAsync();

        var recentDetectionCount = recentDetections.Count;
        var recentSuccessCount = recentDetections.Count(d => d.Status == "success");
        var recentSuccessRate = (double)recentSuccessCount / recentDetectionCount;

        recentDetectionCount.Should().Be(3);
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
