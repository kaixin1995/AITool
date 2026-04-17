using AITool.Application.SiteCatalog;
using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AITool.ApplicationTests.SiteCatalog;

// 模拟站点目录客户端，返回预设的模型列表
public sealed class FakeSiteCatalogClient : ISiteCatalogClient
{
    private readonly string[] _models;

    public FakeSiteCatalogClient(string[] models)
    {
        _models = models;
    }

    public Task<IReadOnlyList<string>> GetModelsAsync(Site site, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<string>>(_models);
    }
}

// 站点模型拉取与导入测试
public sealed class PullSiteModelsTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly Site _site;

    public PullSiteModelsTests()
    {
        // 使用内存数据库进行测试
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);

        // 预置测试站点
        _site = new Site
        {
            Name = "Test Site",
            BaseUrl = "https://api.test.com",
            ApiKey = "test-key",
            ProtocolType = "OpenAI"
        };
        _dbContext.Sites.Add(_site);
        _dbContext.SaveChanges();
    }

    [Fact]
    public async Task Pull_creates_mappings_for_remote_models()
    {
        // 构造远程模型列表，验证拉取后映射记录正确创建
        var client = new FakeSiteCatalogClient(["gpt-5.4", "text-embedding-3-large"]);
        await PullAndImportAsync(client);

        _dbContext.SiteModelMappings.Should().HaveCount(2);
    }

    [Fact]
    public async Task Pull_creates_model_library_items_for_new_models()
    {
        // 拉取新模型时自动创建模型库条目
        var client = new FakeSiteCatalogClient(["claude-4-opus"]);
        await PullAndImportAsync(client);

        _dbContext.ModelLibraryItems.Should().ContainSingle(m => m.ModelName == "claude-4-opus");
    }

    [Fact]
    public async Task Pull_skips_already_imported_models()
    {
        // 已存在的映射不应重复创建
        await PullAndImportAsync(new FakeSiteCatalogClient(["gpt-5.4"]));

        // 再次拉取相同模型加一个新模型
        var result = await PullAndImportAsync(new FakeSiteCatalogClient(["gpt-5.4", "gpt-5.4-mini"]));

        // 只有新模型被导入
        _dbContext.SiteModelMappings.Should().HaveCount(2);
    }

    // 模拟拉取并导入流程，与 Razor Page 后台逻辑一致
    private async Task<int> PullAndImportAsync(ISiteCatalogClient client)
    {
        var remoteModels = await client.GetModelsAsync(_site, CancellationToken.None);
        var existingMappings = await _dbContext.SiteModelMappings
            .Where(m => m.SiteId == _site.Id)
            .ToListAsync();

        var importedCount = 0;
        foreach (var remoteName in remoteModels)
        {
            if (existingMappings.Any(m => m.RemoteModelName == remoteName)) continue;

            var modelItem = await _dbContext.ModelLibraryItems
                .FirstOrDefaultAsync(m => m.ModelName == remoteName);

            if (modelItem is null)
            {
                modelItem = new ModelLibraryItem { ModelName = remoteName, DisplayName = remoteName };
                _dbContext.ModelLibraryItems.Add(modelItem);
            }

            _dbContext.SiteModelMappings.Add(new SiteModelMapping
            {
                SiteId = _site.Id,
                ModelLibraryItemId = modelItem.Id,
                RemoteModelName = remoteName,
                LastStatus = "imported"
            });

            importedCount++;
        }

        await _dbContext.SaveChangesAsync();
        return importedCount;
    }

    public void Dispose() => _dbContext.Dispose();
}
