using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Pages.Admin.Models;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.IntegrationTests.Models;

/// <summary>
/// 模型编辑缓存测试，验证保存后聊天相关缓存会立刻失效并重新加载。
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
        services.AddSqlSugar($"Data Source={_databasePath}");
        SqlSugarSetup.InitializeDatabase(services.BuildServiceProvider().GetRequiredService<SqlSugar.ISqlSugarClient>());
        services.AddSingleton<ProxyRequestMetadataCache>();
        _serviceProvider = services.BuildServiceProvider();
        _memoryCache = _serviceProvider.GetRequiredService<IMemoryCache>();
    }

    /// <summary>
    /// 验证保存模型后，已启用模型的缓存会立即失效并重新加载。
    /// </summary>
    [Fact]
    public async Task OnPostAsync_invalidates_enabled_model_cache_immediately()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        var modelId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        SqlSugarSetup.InitializeDatabase(db.Client);

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

        var cachedBeforeEdit = await cache.GetEnabledModelAsync(modelId, CancellationToken.None);
        cachedBeforeEdit.Should().NotBeNull();
        cachedBeforeEdit!.ModelName.Should().Be("old-model");

        var page = new EditModel(db, cache)
        {
            ModelName = "new-model",
            DisplayName = "New Model",
            IsEnabled = true
        };

        await page.OnPostAsync(modelId, CancellationToken.None);

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
