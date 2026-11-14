using AITool.Application.Codex;
using AITool.Application.Pricing;
using AITool.Domain.Codex;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using AITool.IntegrationTests.Proxy;
using System.Text.Json.Nodes;
using System.Net.Http.Json;
using System.Net;

namespace AITool.IntegrationTests.Services;

public sealed class CodexQuotaPeriodTests
{
    [Fact]
    public async Task Quota_api_uses_each_windows_current_observation_without_modifying_saved_history()
    {
        await using var factory = new ResponsesWebApplicationFactory(new ResponsesFakeProxyForwardService(), configureServices: services =>
        {
            // Match the production SQLite date conversion instead of the generic test fixture's raw provider.
            var descriptor = services.Single(x => x.ServiceType == typeof(ISqlSugarClient));
            var connection = ((ISqlSugarClient)descriptor.ImplementationInstance!).CurrentConnectionConfig.ConnectionString;
            services.Remove(descriptor);
            services.AddSqlSugar(connection);
        });
        using var client = factory.CreateClient();
        var now = DateTimeOffset.UtcNow;
        var site = Guid.NewGuid();
        var account = new CodexAccount { Id = Guid.NewGuid(), LinkedSiteId = site };
        var weekly = new CodexQuotaPeriod { AccountId = account.Id, SiteId = site, WindowId = "weekly", StartAt = now.AddDays(-2), ResetAt = now.AddDays(5), LastObservedAt = now.AddMinutes(-5), FirstObservedAt = now.AddDays(-2), UsedPercent = 20, RequestCount = 777, BoundaryUncertain = true };
        var shortWindow = new CodexQuotaPeriod { AccountId = account.Id, SiteId = site, WindowId = "five-hour", StartAt = now.AddHours(-2), ResetAt = now.AddHours(3), LastObservedAt = now.AddMinutes(-10), FirstObservedAt = now.AddHours(-2), UsedPercent = 50, RequestCount = 888 };
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Client.Insertable(account).ExecuteCommandAsync();
            await db.Client.Updateable<AITool.Domain.Operations.SystemRuntimeSettings>()
                .SetColumns(x => x.OAuthFeaturesEnabled == true).Where(x => x.Id == 1).ExecuteCommandAsync();
            scope.ServiceProvider.GetRequiredService<AITool.Web.Services.ProxyRequestMetadataCache>().InvalidateRuntimeSettings();
            await db.Client.Insertable(new[] { weekly, shortWindow }).ExecuteCommandAsync();
            await db.Client.Insertable(new[]
            {
                new ProxyUsageLog { TargetSiteId = site, RequestedAt = now.AddMinutes(-30), InputTokens = 100, TotalTokens = 100, Status = "success", AttemptedModel = "terra" },
                new ProxyUsageLog { TargetSiteId = site, RequestedAt = now.AddMinutes(-7), InputTokens = 50, TotalTokens = 50, Status = "success", AttemptedModel = "terra" },
                new ProxyUsageLog { TargetSiteId = site, RequestedAt = now.AddMinutes(-1), InputTokens = 200, TotalTokens = 200, Status = "success", AttemptedModel = "terra" }
            }).ExecuteCommandAsync();
        }
        using var response = await client.GetAsync($"/api/admin/oauth/accounts/{account.Id}/quota-periods");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonArray>())!;
        var w = rows.Single(x => x!["windowId"]!.GetValue<string>() == "weekly")!;
        var s = rows.Single(x => x!["windowId"]!.GetValue<string>() == "five-hour")!;
        w["requestCount"]!.GetValue<int>().Should().Be(3);
        w["projectionBasis"]!["requestCount"]!.GetValue<int>().Should().Be(2);
        w["projectionBasis"]!["totalTokens"]!.GetValue<long>().Should().Be(150);
        w["usedPercent"]!.GetValue<double>().Should().Be(20);
        s["projectionBasis"]!["requestCount"]!.GetValue<int>().Should().Be(1);
        s["usedPercent"]!.GetValue<double>().Should().Be(50);
        await using var verify = factory.Services.CreateAsyncScope();
        var saved = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        (await saved.CodexQuotaPeriods.InSingleAsync(weekly.Id)).RequestCount.Should().Be(777);
        (await saved.CodexQuotaPeriods.InSingleAsync(shortWindow.Id)).RequestCount.Should().Be(888);
    }

    [Fact]
    public async Task Projection_basis_uses_only_current_site_calls_recorded_before_the_quota_observation()
    {
        await WithDatabase(async db =>
        {
            var observed = new DateTimeOffset(2026, 11, 14, 6, 0, 0, TimeSpan.Zero);
            var site = Guid.NewGuid();
            var period = new CodexQuotaPeriod { SiteId = site, StartAt = observed.AddHours(-1), ResetAt = observed.AddDays(6), LastObservedAt = observed };
            await db.Insertable(new[]
            {
                new ProxyUsageLog { TargetSiteId = site, RequestedAt = observed.AddMinutes(-10), TotalDurationMs = 1000, InputTokens = 100, TotalTokens = 100, Status = "success" },
                new ProxyUsageLog { TargetSiteId = site, RequestedAt = observed.AddSeconds(-10), TotalDurationMs = 20000, InputTokens = 200, TotalTokens = 200, Status = "success" },
                new ProxyUsageLog { TargetSiteId = site, RequestedAt = observed.AddSeconds(1), InputTokens = 300, TotalTokens = 300, Status = "success" },
                new ProxyUsageLog { TargetSiteId = Guid.NewGuid(), RequestedAt = observed.AddMinutes(-1), InputTokens = 400, TotalTokens = 400, Status = "success" }
            }).ExecuteCommandAsync();
            await CodexQuotaPeriodService.AggregateAsync(db, period, observed.AddMinutes(1), default);
            period.RequestCount.Should().Be(3);
            var basis = await CodexQuotaPeriodService.AggregateAtObservationAsync(db, period, observed.AddMinutes(1), default);
            basis.Should().NotBeNull();
            basis!.RequestCount.Should().Be(2);
            basis.InputTokens.Should().Be(300);
            period.RequestCount.Should().Be(3, "the projection must not change the live or saved period summary");
            (await CodexQuotaPeriodService.AggregateAtObservationAsync(db, period, observed.AddSeconds(-1), default)).Should().BeNull();
        });
    }

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
            first.SuccessCount.Should().Be(2);
            first.LastUsageAt.Should().BeCloseTo(now.AddMinutes(-1), TimeSpan.FromSeconds(1));
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
    [Theory]
    [InlineData("weekly", 604800)]
    [InlineData("five-hour", 18000)]
    public async Task Idle_countdown_drift_and_first_use_keep_the_original_period(string windowId, int duration)
    {
        await WithDatabase(async db =>
        {
            var start = new DateTimeOffset(2026, 11, 13, 9, 44, 0, TimeSpan.Zero);
            var account = new CodexAccount { Id = Guid.NewGuid(), LinkedSiteId = Guid.NewGuid() };
            CodexQuotaInfo Snapshot(DateTimeOffset at, double used) => new()
            {
                Windows = [new CodexQuotaWindow { Id = windowId, ResetAtUtc = at.AddSeconds(duration), LimitWindowSeconds = duration, UsedPercent = used }]
            };
            await CodexQuotaPeriodService.ObserveAsync(db, account, Snapshot(start, 0), start, default);
            var first = await db.Queryable<CodexQuotaPeriod>().SingleAsync();
            await db.Insertable(new ProxyUsageLog { TargetSiteId = account.LinkedSiteId, RequestedAt = start.AddMinutes(45), InputTokens = 21, OutputTokens = 9, TotalTokens = 30, Status = "success" }).ExecuteCommandAsync();
            await CodexQuotaPeriodService.ObserveAsync(db, account, Snapshot(start.AddHours(1), 0), start.AddHours(1), default);
            await CodexQuotaPeriodService.ObserveAsync(db, account, Snapshot(start.AddHours(2), 11), start.AddHours(2), default);
            var periods = await db.Queryable<CodexQuotaPeriod>().ToListAsync();
            periods.Should().ContainSingle();
            var current = periods[0];
            current.Id.Should().Be(first.Id);
            current.StartAt.Should().Be(first.StartAt);
            current.ResetAt.Should().Be(start.AddHours(2).AddSeconds(duration));
            current.BoundaryUncertain.Should().BeTrue();
            current.RequestCount.Should().Be(1);
            current.InputTokens.Should().Be(21);
            current.OutputTokens.Should().Be(9);
            current.UsedPercent.Should().Be(11);
            // 使用后截止时间固定，后续正常观测也不切分。
            await CodexQuotaPeriodService.ObserveAsync(db, account, Snapshot(start.AddHours(2), 20), start.AddHours(3), default);
            (await db.Queryable<CodexQuotaPeriod>().CountAsync()).Should().Be(1);
        });
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(40, false)]
    [InlineData(40, true)]
    public async Task True_expiry_and_early_percentage_reset_still_close_the_previous_period(double used, bool early)
    {
        await WithDatabase(async db =>
        {
            var start = new DateTimeOffset(2026, 11, 13, 9, 44, 0, TimeSpan.Zero);
            var account = new CodexAccount { Id = Guid.NewGuid(), LinkedSiteId = Guid.NewGuid() };
            CodexQuotaInfo Snapshot(DateTimeOffset reset, double percent) => new()
            {
                Windows = [new CodexQuotaWindow { Id = "weekly", ResetAtUtc = reset, LimitWindowSeconds = 604800, UsedPercent = percent }]
            };
            await db.Insertable(new ProxyUsageLog { TargetSiteId = account.LinkedSiteId, RequestedAt = start.AddHours(1), InputTokens = 100, TotalTokens = 100, Status = "success" }).ExecuteCommandAsync();
            await CodexQuotaPeriodService.ObserveAsync(db, account, Snapshot(start.AddDays(7), used), start, default);
            var observed = early ? start.AddDays(1) : start.AddDays(7);
            await CodexQuotaPeriodService.ObserveAsync(db, account, Snapshot(observed.AddDays(7), 0), observed, default);
            var periods = await db.Queryable<CodexQuotaPeriod>().OrderBy(x => x.FirstObservedAt).ToListAsync();
            periods.Should().HaveCount(2);
            periods[0].EndAt.Should().Be(observed);
            periods[0].RequestCount.Should().Be(1);
            periods[0].InputTokens.Should().Be(100);
            periods[0].BoundaryUncertain.Should().Be(early);
            periods[1].StartAt.Should().Be(observed);
            periods[1].RequestCount.Should().Be(0);
            periods[1].BoundaryUncertain.Should().Be(early);
        });
    }

    [Theory]
    [InlineData(40, 0, 604800, 604800)] // 同一截止时间下百分比显著回落。
    [InlineData(0, 0, 604800, 1209600)] // 空闲但窗口长度改变。
    [InlineData(0, 0, 608400, 604800)] // 空闲但新截止时间不是完整窗口倒计时。
    public async Task Resets_that_do_not_match_idle_drift_keep_separate_periods(double previousUsed, double nextUsed, int resetSeconds, int duration)
    {
        await WithDatabase(async db =>
        {
            var start = new DateTimeOffset(2026, 11, 13, 9, 44, 0, TimeSpan.Zero);
            var account = new CodexAccount { Id = Guid.NewGuid(), LinkedSiteId = Guid.NewGuid() };
            CodexQuotaInfo Snapshot(DateTimeOffset reset, double used, int seconds) => new()
            {
                Windows = [new CodexQuotaWindow { Id = "weekly", ResetAtUtc = reset, LimitWindowSeconds = seconds, UsedPercent = used }]
            };
            await CodexQuotaPeriodService.ObserveAsync(db, account, Snapshot(start.AddDays(7), previousUsed, 604800), start, default);
            await CodexQuotaPeriodService.ObserveAsync(db, account, Snapshot(start.AddSeconds(resetSeconds), nextUsed, duration), start.AddHours(2), default);
            var periods = await db.Queryable<CodexQuotaPeriod>().OrderBy(x => x.FirstObservedAt).ToListAsync();
            periods.Should().HaveCount(2);
            periods[0].EndAt.Should().Be(start.AddHours(2));
            periods[0].BoundaryUncertain.Should().BeTrue();
            periods[1].BoundaryUncertain.Should().BeTrue();
        });
    }

    [Fact]
    public async Task Closing_a_period_preserves_an_uncertain_start_even_when_the_end_is_known()
    {
        await WithDatabase(async db =>
        {
            var start = new DateTimeOffset(2026, 11, 13, 9, 44, 0, TimeSpan.Zero);
            var account = new CodexAccount { Id = Guid.NewGuid(), LinkedSiteId = Guid.NewGuid() };
            await db.Insertable(new CodexQuotaPeriod
            {
                AccountId = account.Id, SiteId = account.LinkedSiteId, WindowId = "weekly", DurationSeconds = 604800,
                StartAt = start, ResetAt = start.AddDays(7), FirstObservedAt = start, LastObservedAt = start,
                UsedPercent = 40, BoundaryUncertain = true
            }).ExecuteCommandAsync();
            var observed = start.AddDays(7);
            var info = new CodexQuotaInfo
            {
                Windows = [new CodexQuotaWindow { Id = "weekly", ResetAtUtc = observed.AddDays(7), LimitWindowSeconds = 604800, UsedPercent = 0 }]
            };
            await CodexQuotaPeriodService.ObserveAsync(db, account, info, observed, default);
            var periods = await db.Queryable<CodexQuotaPeriod>().OrderBy(x => x.FirstObservedAt).ToListAsync();
            periods[0].BoundaryUncertain.Should().BeTrue();
            periods[1].BoundaryUncertain.Should().BeFalse();
        });
    }

    private static async Task WithDatabase(Func<ISqlSugarClient, Task> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"quota-period-{Guid.NewGuid():N}.db");
        using var services = new ServiceCollection().AddSqlSugar($"Data Source={path}").BuildServiceProvider();
        try
        {
            var db = services.GetRequiredService<ISqlSugarClient>();
            SqlSugarSetup.InitializeDatabase(db);
            await action(db);
        }
        finally
        {
            services.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
        }
    }
}
