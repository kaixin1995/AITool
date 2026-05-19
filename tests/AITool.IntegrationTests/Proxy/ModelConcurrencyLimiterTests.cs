using AITool.Domain.SiteCatalog;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.IntegrationTests.Proxy;

/// <summary>
/// 验证 ModelConcurrencyLimiter 在 SkipOnFull 和 WaitForSlot 两种模式下的并发控制行为。
/// </summary>
public sealed class ModelConcurrencyLimiterTests : IDisposable
{
    /// <summary>
    /// 临时数据库文件路径，每个测试独立一份。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-concurrency-test-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 数据库上下文，用于准备和校验并发配置数据。
    /// </summary>
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 服务容器，用于向 ModelConcurrencyLimiter 提供 AppDbContext。
    /// </summary>
    private readonly ServiceProvider _serviceProvider;

    /// <summary>
    /// 被测并发限制器实例。
    /// </summary>
    private readonly ModelConcurrencyLimiter _limiter;

    /// <summary>
    /// 为每个测试构建独立的 SQLite 文件数据库和服务容器，避免并发配置互相污染。
    /// </summary>
    public ModelConcurrencyLimiterTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        _serviceProvider = services.BuildServiceProvider();
        _dbContext = _serviceProvider.GetRequiredService<AppDbContext>();
        _dbContext.Database.EnsureCreated();
        _limiter = new ModelConcurrencyLimiter();
    }

    /// <summary>
    /// 未配置并发限制的模型，应直接返回获取成功。
    /// </summary>
    [Fact]
    public async Task AcquireAsync_returns_acquired_when_no_limit_configured()
    {
        var siteId = Guid.NewGuid();

        using var handle = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-a",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None);

        handle.Acquired.Should().BeTrue();
    }

    /// <summary>
    /// MaxConcurrency 设为 0 的模型等同于不限制，应直接返回获取成功。
    /// </summary>
    [Fact]
    public async Task AcquireAsync_returns_acquired_when_max_concurrency_is_zero()
    {
        var siteId = Guid.NewGuid();
        await SeedMappingAsync(siteId, "model-a", maxConcurrency: 0);

        using var handle = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-a",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None);

        handle.Acquired.Should().BeTrue();
    }

    /// <summary>
    /// SkipOnFull 模式下，并发数未打满时，应正常获取到许可。
    /// </summary>
    [Fact]
    public async Task SkipOnFull_returns_acquired_when_slots_available()
    {
        var siteId = Guid.NewGuid();
        await SeedMappingAsync(siteId, "model-a", maxConcurrency: 2);

        using var handle = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-a",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None);

        handle.Acquired.Should().BeTrue();
    }

    /// <summary>
    /// SkipOnFull 模式下，并发数打满后，应立即返回未获取。
    /// </summary>
    [Fact]
    public async Task SkipOnFull_returns_not_acquired_when_slots_exhausted()
    {
        var siteId = Guid.NewGuid();
        await SeedMappingAsync(siteId, "model-a", maxConcurrency: 1);

        // 先占用唯一一个槽位。
        using var blockingHandle = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-a",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None);
        blockingHandle.Acquired.Should().BeTrue();

        // 第二次获取应被跳过。
        using var handle = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-a",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None);

        handle.Acquired.Should().BeFalse();
    }

    /// <summary>
    /// SkipOnFull 模式下，释放槽位后，后续请求应能再次获取。
    /// </summary>
    [Fact]
    public async Task SkipOnFull_returns_acquired_after_slot_released()
    {
        var siteId = Guid.NewGuid();
        await SeedMappingAsync(siteId, "model-a", maxConcurrency: 1);

        // 占用后立即释放。
        using (var handle = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-a",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None))
        {
            handle.Acquired.Should().BeTrue();
        }

        // 释放后应能再次获取。
        using var secondHandle = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-a",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None);

        secondHandle.Acquired.Should().BeTrue();
    }

    /// <summary>
    /// WaitForSlot 模式下，有可用槽位时，应直接返回获取成功。
    /// </summary>
    [Fact]
    public async Task WaitForSlot_returns_acquired_when_slots_available()
    {
        var siteId = Guid.NewGuid();
        await SeedMappingAsync(siteId, "model-a", maxConcurrency: 2);

        using var handle = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-a",
            ConcurrencyAcquireMode.WaitForSlot, TimeSpan.FromSeconds(10), CancellationToken.None);

        handle.Acquired.Should().BeTrue();
    }

    /// <summary>
    /// WaitForSlot 模式下，并发打满后，应排队等待直到槽位释放。
    /// </summary>
    [Fact]
    public async Task WaitForSlot_waits_for_slot_release()
    {
        var siteId = Guid.NewGuid();
        await SeedMappingAsync(siteId, "model-a", maxConcurrency: 1);

        // 先占用唯一一个槽位。
        var blockingHandle = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-a",
            ConcurrencyAcquireMode.WaitForSlot, TimeSpan.FromSeconds(30), CancellationToken.None);
        blockingHandle.Acquired.Should().BeTrue();

        // 在后台释放槽位，模拟任务完成。
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            blockingHandle.Dispose();
        });

        using var handle = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-a",
            ConcurrencyAcquireMode.WaitForSlot, TimeSpan.FromSeconds(10), CancellationToken.None);

        handle.Acquired.Should().BeTrue();
    }

    /// <summary>
    /// WaitForSlot 模式下，排队超时后应返回未获取。
    /// </summary>
    [Fact]
    public async Task WaitForSlot_returns_not_acquired_on_timeout()
    {
        var siteId = Guid.NewGuid();
        await SeedMappingAsync(siteId, "model-a", maxConcurrency: 1);

        // 先占用唯一一个槽位且不释放。
        using var blockingHandle = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-a",
            ConcurrencyAcquireMode.WaitForSlot, TimeSpan.FromSeconds(30), CancellationToken.None);

        // 第二次获取在短超时后应失败。
        using var handle = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-a",
            ConcurrencyAcquireMode.WaitForSlot, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        handle.Acquired.Should().BeFalse();
    }

    /// <summary>
    /// WaitForSlot 模式下，请求被外部取消时应向上传播 OperationCanceledException。
    /// </summary>
    [Fact]
    public async Task WaitForSlot_throws_on_external_cancellation()
    {
        var siteId = Guid.NewGuid();
        await SeedMappingAsync(siteId, "model-a", maxConcurrency: 1);

        // 先占用唯一一个槽位且不释放。
        using var blockingHandle = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-a",
            ConcurrencyAcquireMode.WaitForSlot, TimeSpan.FromSeconds(30), CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = async () =>
        {
            using var handle = await _limiter.AcquireAsync(
                _serviceProvider, siteId, "model-a",
                ConcurrencyAcquireMode.WaitForSlot, TimeSpan.FromSeconds(30), cts.Token);
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// 不同站点的同一个模型名应各自独立计数，互不影响。
    /// </summary>
    [Fact]
    public async Task Different_sites_have_independent_concurrency()
    {
        var siteA = Guid.NewGuid();
        var siteB = Guid.NewGuid();
        await SeedMappingAsync(siteA, "model-a", maxConcurrency: 1);
        await SeedMappingAsync(siteB, "model-a", maxConcurrency: 1);

        // 占用站点 A 的槽位。
        using var handleA = await _limiter.AcquireAsync(
            _serviceProvider, siteA, "model-a",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None);
        handleA.Acquired.Should().BeTrue();

        // 站点 A 已打满，站点 B 应仍然可用。
        using var handleB = await _limiter.AcquireAsync(
            _serviceProvider, siteB, "model-a",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None);
        handleB.Acquired.Should().BeTrue();

        // 站点 A 再次请求应被跳过。
        using var handleA2 = await _limiter.AcquireAsync(
            _serviceProvider, siteA, "model-a",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None);
        handleA2.Acquired.Should().BeFalse();
    }

    /// <summary>
    /// 同一站点的不同模型名应各自独立计数，互不影响。
    /// </summary>
    [Fact]
    public async Task Different_models_on_same_site_have_independent_concurrency()
    {
        var siteId = Guid.NewGuid();
        await SeedMappingAsync(siteId, "model-a", maxConcurrency: 1);
        await SeedMappingAsync(siteId, "model-b", maxConcurrency: 1);

        // 占用 model-a 的槽位。
        using var handleA = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-a",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None);
        handleA.Acquired.Should().BeTrue();

        // model-a 已打满，model-b 应仍然可用。
        using var handleB = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-b",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None);
        handleB.Acquired.Should().BeTrue();

        // model-a 再次请求应被跳过。
        using var handleA2 = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-a",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None);
        handleA2.Acquired.Should().BeFalse();
    }

    /// <summary>
    /// 同一站点同一模型的真实活跃并发应聚合计数，并在释放后及时回收。
    /// </summary>
    [Fact]
    public async Task ListActive_aggregates_active_count_and_clears_after_release()
    {
        var siteId = Guid.NewGuid();

        using var handleA = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-a",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None);
        using var handleB = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "model-a",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None);

        var snapshots = _limiter.ListActive();
        snapshots.Should().ContainSingle();
        snapshots[0].SiteId.Should().Be(siteId);
        snapshots[0].SiteModelName.Should().Be("model-a");
        snapshots[0].ActiveCount.Should().Be(2);

        handleB.Dispose();
        var afterOneReleased = _limiter.ListActive();
        afterOneReleased.Should().ContainSingle();
        afterOneReleased[0].ActiveCount.Should().Be(1);

        handleA.Dispose();
        _limiter.ListActive().Should().BeEmpty();
    }

    /// <summary>
    /// 不同站点的同名模型在活跃快照中也应分别展示，不能合并。
    /// </summary>
    [Fact]
    public async Task ListActive_keeps_same_model_name_separate_between_sites()
    {
        var siteA = Guid.NewGuid();
        var siteB = Guid.NewGuid();

        using var handleA = await _limiter.AcquireAsync(
            _serviceProvider, siteA, "shared-model",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None);
        using var handleB = await _limiter.AcquireAsync(
            _serviceProvider, siteB, "shared-model",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None);

        var snapshots = _limiter.ListActive();
        snapshots.Should().HaveCount(2);
        snapshots.Should().ContainSingle(x => x.SiteId == siteA && x.SiteModelName == "shared-model" && x.ActiveCount == 1);
        snapshots.Should().ContainSingle(x => x.SiteId == siteB && x.SiteModelName == "shared-model" && x.ActiveCount == 1);
    }

    /// <summary>
    /// 最近快照在并发归零后仍应保留一段时间，并显示为 0。
    /// </summary>
    [Fact]
    public async Task ListRecent_keeps_zero_count_entries_within_retention_window()
    {
        var siteId = Guid.NewGuid();

        using (var handle = await _limiter.AcquireAsync(
            _serviceProvider, siteId, "recent-model",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None))
        {
            handle.Acquired.Should().BeTrue();
        }

        _limiter.ListActive().Should().BeEmpty();

        var recentSnapshots = _limiter.ListRecent(ModelConcurrencyLimiter.RecentRetention);
        recentSnapshots.Should().ContainSingle(x => x.SiteId == siteId && x.SiteModelName == "recent-model" && x.ActiveCount == 0);
    }

    /// <summary>
    /// ConcurrencyAcquireResult.Dispose 无论获取成功或失败都不应抛异常。
    /// </summary>
    [Fact]
    public void Dispose_never_throws_for_any_result_state()
    {
        // 验证未获取的实例可以安全释放。
        var notAcquired = ConcurrencyAcquireResult.NotAcquired;
        var act = () => notAcquired.Dispose();
        act.Should().NotThrow();
    }

    /// <summary>
    /// 并发限制器在数据库查询失败时不应抛异常，应按无限制处理。
    /// </summary>
    [Fact]
    public async Task AcquireAsync_does_not_throw_when_db_query_fails()
    {
        // 使用一个空的 ServiceProvider（没有注册 AppDbContext），模拟查询失败。
        using var emptyProvider = new ServiceCollection().BuildServiceProvider();

        using var handle = await _limiter.AcquireAsync(
            emptyProvider, Guid.NewGuid(), "model-a",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(10), CancellationToken.None);

        // 查询失败后应按无限制处理，直接通过。
        handle.Acquired.Should().BeTrue();
    }

    /// <summary>
    /// 向数据库插入一条站点模型映射并设置并发数。
    /// </summary>
    private async Task SeedMappingAsync(Guid siteId, string remoteModelName, int maxConcurrency)
    {
        _dbContext.SiteModelMappings.Add(new SiteModelMapping
        {
            Id = Guid.NewGuid(),
            SiteId = siteId,
            RemoteModelName = remoteModelName,
            IsEnabled = true,
            MaxConcurrency = maxConcurrency
        });
        await _dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// 释放测试使用的数据库和服务容器资源。
    /// </summary>
    public void Dispose()
    {
        _dbContext.Dispose();
        _serviceProvider.Dispose();
        try { File.Delete(_databasePath); } catch { }
    }
}
