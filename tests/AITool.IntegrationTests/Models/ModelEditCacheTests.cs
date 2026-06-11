using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.IntegrationTests.Models;

/// <summary>
/// 缓存失效集成测试。
/// 验证通过 <see cref="AdminCacheInvalidationService"/> 失效模型元数据后，
/// <see cref="ProxyRequestMetadataCache"/> 会立刻重新加载最新数据。
/// <para>
/// 此测试不再依赖具体的页面模型（EditModel 已迁移至 Admin 宿主），
/// 而是直接验证缓存失效服务与运行时缓存之间的交互。
/// </para>
/// </summary>
public sealed class ModelEditCacheTests : IAsyncDisposable
{
    /// <summary>
    /// 保存测试使用的服务提供器。
    /// </summary>
    private readonly ServiceProvider _serviceProvider;
    /// <summary>
    /// 保存测试使用的内存缓存实例。
    /// </summary>
    private readonly IMemoryCache _memoryCache;
    /// <summary>
    /// 保存当前测试使用的临时数据库路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-model-edit-cache-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 创建模型编辑缓存测试所需的服务容器和数据库配置。
    /// </summary>
    public ModelEditCacheTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        services.AddSingleton<ProxyRequestMetadataCache>();
        _serviceProvider = services.BuildServiceProvider();
        _memoryCache = _serviceProvider.GetRequiredService<IMemoryCache>();
    }

    /// <summary>
    /// 验证通过缓存失效服务失效模型元数据后，运行时缓存会立即重新加载最新数据。
    /// <para>
    /// 模拟管理员在后台修改模型名称的场景：先读取缓存确认旧值，
    /// 然后直接修改数据库记录并调用失效服务，最后验证缓存已刷新为新值。
    /// </para>
    /// </summary>
    [Fact]
    public async Task InvalidateModelMetadata_reloads_enabled_model_cache_immediately()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        var modelId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        db.Sites.Add(new Site
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Cache Site",
            BaseUrl = "https://cache.example.com",
            ApiKey = "cache-key",
            ProtocolType = "OpenAI",
            IsEnabled = true
        });
        db.ModelLibraryItems.Add(new ModelLibraryItem
        {
            Id = modelId,
            ModelName = "old-model",
            DisplayName = "Old Model",
            IsEnabled = true
        });
        db.SiteModelMappings.Add(new SiteModelMapping
        {
            SiteId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ModelLibraryItemId = modelId,
            RemoteModelName = "old-model-upstream",
            LastStatus = "ok",
            IsEnabled = true
        });
        await db.SaveChangesAsync();

        // 预热缓存，确认旧值
        var cachedBeforeEdit = await cache.GetEnabledModelAsync(modelId, CancellationToken.None);
        cachedBeforeEdit.Should().NotBeNull();
        cachedBeforeEdit!.ModelName.Should().Be("old-model");

        // 模拟后台修改模型操作：直接修改数据库并调用失效服务
        var model = await db.ModelLibraryItems.FindAsync([modelId]);
        model!.ModelName = "new-model";
        model.DisplayName = "New Model";
        await db.SaveChangesAsync();

        var cacheInvalidation = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        cacheInvalidation.InvalidateModelMetadata();
        cacheInvalidation.InvalidateRouteTargets();

        // 验证缓存已刷新为新值
        var cachedAfterEdit = await cache.GetEnabledModelAsync(modelId, CancellationToken.None);
        cachedAfterEdit.Should().NotBeNull();
        cachedAfterEdit!.ModelName.Should().Be("new-model");
        cachedAfterEdit.DisplayName.Should().Be("New Model");
    }

    /// <summary>
    /// 释放测试过程中创建的资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _memoryCache.Dispose();
        await _serviceProvider.DisposeAsync();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
