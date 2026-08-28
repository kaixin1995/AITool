using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// 验证 SiteUsageTracker 能在启动预热时把最近使用时间聚合下推到 SQLite。
/// </summary>
public sealed class SiteUsageTrackerTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-site-usage-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _serviceProvider;

    public SiteUsageTrackerTests()
    {
        var services = new ServiceCollection();
        services.AddSqlSugar($"Data Source={_databasePath}");
        _serviceProvider = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_serviceProvider.GetRequiredService<SqlSugar.ISqlSugarClient>());
    }

    [Fact]
    public async Task WarmupAsync_uses_latest_requested_at_per_site()
    {
        var siteId = Guid.NewGuid();
        var otherSiteId = Guid.NewGuid();
        var oldest = DateTimeOffset.UtcNow.AddDays(-2);
        var newest = DateTimeOffset.UtcNow.AddMinutes(-2);

        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await dbContext.InsertRangeAsync([
                new ProxyUsageLog { RequestId = Guid.NewGuid(), TargetSiteId = siteId, RequestedAt = oldest },
                new ProxyUsageLog { RequestId = Guid.NewGuid(), TargetSiteId = siteId, RequestedAt = newest },
                new ProxyUsageLog { RequestId = Guid.NewGuid(), TargetSiteId = otherSiteId, RequestedAt = oldest }
            ]);
        }

        var tracker = new SiteUsageTracker();
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            await tracker.WarmupAsync(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                CancellationToken.None);
        }

        tracker.GetLastUsedAt(siteId).Should().NotBeNull();
        tracker.GetLastUsedAt(siteId)!.Value.UtcDateTime.Should().BeCloseTo(newest.UtcDateTime, TimeSpan.FromSeconds(1));
        tracker.GetLastUsedAt(otherSiteId).Should().NotBeNull();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        TryDelete(_databasePath);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
        try { File.Delete(path + "-wal"); } catch { }
        try { File.Delete(path + "-shm"); } catch { }
    }
}
