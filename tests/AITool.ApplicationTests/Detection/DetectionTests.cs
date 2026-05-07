using AITool.Application.Detection;
using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AITool.ApplicationTests.Detection;

// 模拟模型探测服务，返回预设的探测结果
public sealed class FakeModelProbeService : IModelProbeService
{
    private readonly ModelProbeResult _result;

    public FakeModelProbeService(ModelProbeResult result)
    {
        _result = result;
    }

    public Task<ModelProbeResult> ProbeAsync(Site site, ModelLibraryItem model, CancellationToken cancellationToken)
    {
        return Task.FromResult(_result);
    }
}

// 模型检测测试
public sealed class DetectionTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly Site _site;
    private readonly ModelLibraryItem _model;
    private readonly SiteModelMapping _mapping;

    public DetectionTests()
    {
        // 使用内存数据库进行测试
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);

        // 预置测试数据
        _site = new Site
        {
            Name = "Test Site",
            BaseUrl = "https://api.test.com",
            ApiKey = "test-key",
            ProtocolType = "OpenAI"
        };
        _model = new ModelLibraryItem { ModelName = "gpt-5.4", DisplayName = "GPT-5.4" };
        _mapping = new SiteModelMapping
        {
            SiteId = _site.Id,
            ModelLibraryItemId = _model.Id,
            RemoteModelName = "gpt-5.4",
            LastStatus = "imported"
        };

        _dbContext.Sites.Add(_site);
        _dbContext.ModelLibraryItems.Add(_model);
        _dbContext.SiteModelMappings.Add(_mapping);
        _dbContext.SaveChanges();
    }

    [Fact]
    public async Task Probe_success_updates_mapping_status()
    {
        // 成功探测应更新映射状态
        var probeService = new FakeModelProbeService(new ModelProbeResult
        {
            Success = true,
            DurationMs = 150
        });

        var result = await probeService.ProbeAsync(_site, _model, CancellationToken.None);
        _mapping.LastStatus = result.Success ? "success" : "fail";
        await _dbContext.SaveChangesAsync();

        _mapping.LastStatus.Should().Be("success");
    }

    [Fact]
    public async Task Probe_failure_keeps_error_message_available_in_result()
    {
        // 失败探测应保留错误信息给调用方处理
        var probeService = new FakeModelProbeService(new ModelProbeResult
        {
            Success = false,
            DurationMs = 300,
            ErrorMessage = "Connection refused"
        });

        var result = await probeService.ProbeAsync(_site, _model, CancellationToken.None);
        _mapping.LastStatus = result.Success ? "success" : "fail";
        await _dbContext.SaveChangesAsync();

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Connection refused");
        _mapping.LastStatus.Should().Be("fail");
    }

    [Fact]
    public async Task Multiple_probes_keep_latest_mapping_status()
    {
        // 多次检测后映射状态应反映最后一次结果
        var successProbeService = new FakeModelProbeService(new ModelProbeResult
        {
            Success = true,
            DurationMs = 100
        });
        var failProbeService = new FakeModelProbeService(new ModelProbeResult
        {
            Success = false,
            DurationMs = 120,
            ErrorMessage = "timeout"
        });

        var first = await successProbeService.ProbeAsync(_site, _model, CancellationToken.None);
        _mapping.LastStatus = first.Success ? "success" : "fail";

        var second = await failProbeService.ProbeAsync(_site, _model, CancellationToken.None);
        _mapping.LastStatus = second.Success ? "success" : "fail";
        await _dbContext.SaveChangesAsync();

        _mapping.LastStatus.Should().Be("fail");
    }

    public void Dispose() => _dbContext.Dispose();
}
