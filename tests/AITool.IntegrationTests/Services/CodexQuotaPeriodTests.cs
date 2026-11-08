using AITool.Application.Codex;
using AITool.Application.Pricing;
using AITool.Domain.Codex;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;

namespace AITool.IntegrationTests.Services;

public sealed class CodexQuotaPeriodTests
{
    private sealed class TestPricing : IModelPricingService
    {
        public Task<ModelPricingCatalog> GetCatalogAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ModelPricingCatalog());
        public Task<ModelPricingCatalog> SaveCatalogAsync(ModelPricingCatalog catalog, CancellationToken cancellationToken = default) => Task.FromResult(catalog);
        public ModelPriceEntry? ResolveEntry(string? modelName) => null;
        public ModelUsageCost CalculateCostUsd(string? modelName, DateTimeOffset requestedAt, int inputTokens, int cachedTokens, int outputTokens)
            => new() { CostUsd = modelName == "terra" ? inputTokens + cachedTokens * 0.1m + outputTokens * 2m : null };
    }

    [Fact]
    public async Task Observations_aggregate_actual_site_and_keep_closed_periods()
    {
        var path = Path.Combine(Path.GetTempPath(), $"quota-period-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection().AddSqlSugar($"Data Source={path}").BuildServiceProvider();
        try
        {
            var db = services.GetRequiredService<ISqlSugarClient>();
            SqlSugarSetup.InitializeDatabase(db);
            var now = DateTimeOffset.UtcNow;
            var account = new CodexAccount { Id = Guid.NewGuid(), LinkedSiteId = Guid.NewGuid() };
            await db.Insertable(new[]
            {
                new ProxyUsageLog { TargetSiteId = account.LinkedSiteId, RequestedAt = now.AddHours(-1), InputTokens = 10, CachedTokens = 20, OutputTokens = 3, TotalTokens = 33, Status = "success", AttemptedModel = "terra" },
                new ProxyUsageLog { TargetSiteId = account.LinkedSiteId, RequestedAt = now.AddMinutes(-1), Status = "canceled" },
                new ProxyUsageLog { TargetSiteId = account.LinkedSiteId, RequestedAt = now.AddMinutes(-1), InputTokens = 5, TotalTokens = 5, Status = "success", AttemptedModel = "unpriced" },
                new ProxyUsageLog { TargetSiteId = Guid.NewGuid(), RequestedAt = now.AddMinutes(-1), TotalTokens = 900 }
            }).ExecuteCommandAsync();
            CodexQuotaInfo Snapshot(DateTimeOffset reset) => new() { Windows = [new CodexQuotaWindow { Id = "weekly", Label = "周限额", ResetAtUtc = reset, LimitWindowSeconds = 604800, UsedPercent = 30 }] };
            var reset = now.AddDays(1);
            var pricing = new TestPricing();
            await CodexQuotaPeriodService.ObserveAsync(db, account, Snapshot(reset), now, default, pricing);
            await CodexQuotaPeriodService.ObserveAsync(db, account, Snapshot(reset.AddSeconds(30)), now.AddMinutes(1), default, pricing);
            var first = await db.Queryable<CodexQuotaPeriod>().FirstAsync();
            first.InputTokens.Should().Be(15);
            first.CachedTokens.Should().Be(20);
            first.OutputTokens.Should().Be(3);
            first.RequestCount.Should().Be(3);
            first.UnknownCount.Should().Be(1);
            first.EstimatedCostUsd.Should().Be(18m);
            first.UnpricedCount.Should().Be(1);
            (await db.Queryable<CodexQuotaPeriod>().CountAsync()).Should().Be(1);
            await CodexQuotaPeriodService.ObserveAsync(db, account, Snapshot(now.AddDays(7)), now.AddMinutes(2), default, pricing);
            var periods = await db.Queryable<CodexQuotaPeriod>().OrderBy(x => x.FirstObservedAt).ToListAsync();
            periods.Should().HaveCount(2);
            periods[0].EndAt.Should().NotBeNull();
            periods[0].BoundaryUncertain.Should().BeTrue();
            periods[0].InputTokens.Should().Be(15);
            periods[0].EstimatedCostUsd.Should().Be(18m);
            periods[0].UnpricedCount.Should().Be(1);
            periods[1].BoundaryUncertain.Should().BeTrue();
            periods[1].StartAt.Should().BeCloseTo(now.AddMinutes(2), TimeSpan.FromSeconds(1));
        }
        finally
        {
            services.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
        }
    }
}
