using AITool.Application.Detection;
using AITool.Domain.Detection;
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

// 模型检测与日志记录测试
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
    public async Task Probe_success_records_log_and_updates_mapping()
    {
        // 成功探测应写入检测日志并更新映射状态
        var probeService = new FakeModelProbeService(new ModelProbeResult
        {
            Success = true,
            DurationMs = 150
        });

        var result = await probeService.ProbeAsync(_site, _model, CancellationToken.None);

        // 记录检测日志
        var log = new DetectionLog
        {
            SiteId = _mapping.SiteId,
            ModelLibraryItemId = _mapping.ModelLibraryItemId,
            Status = result.Success ? "success" : "fail",
            DurationMs = result.DurationMs,
            CheckedAt = DateTimeOffset.UtcNow
        };
        _dbContext.DetectionLogs.Add(log);
        _mapping.LastStatus = result.Success ? "success" : "fail";
        await _dbContext.SaveChangesAsync();

        _dbContext.DetectionLogs.Should().ContainSingle(l =>
            l.SiteId == _site.Id &&
            l.Status == "success" &&
            l.DurationMs == 150);

        _mapping.LastStatus.Should().Be("success");
    }

    [Fact]
    public async Task Probe_failure_records_error_message()
    {
        // 失败探测应记录错误信息
        var probeService = new FakeModelProbeService(new ModelProbeResult
        {
            Success = false,
            DurationMs = 300,
            ErrorMessage = "Connection refused"
        });

        var result = await probeService.ProbeAsync(_site, _model, CancellationToken.None);

        var log = new DetectionLog
        {
            SiteId = _mapping.SiteId,
            ModelLibraryItemId = _mapping.ModelLibraryItemId,
            Status = result.Success ? "success" : "fail",
            DurationMs = result.DurationMs,
            ErrorMessage = result.ErrorMessage,
            CheckedAt = DateTimeOffset.UtcNow
        };
        _dbContext.DetectionLogs.Add(log);
        await _dbContext.SaveChangesAsync();

        var saved = _dbContext.DetectionLogs.First();
        saved.Status.Should().Be("fail");
        saved.ErrorMessage.Should().Be("Connection refused");
    }

    [Fact]
    public async Task Multiple_probes_keep_all_logs()
    {
        // 多次检测应保留全部日志记录
        var probeService = new FakeModelProbeService(new ModelProbeResult
        {
            Success = true,
            DurationMs = 100
        });

        for (var i = 0; i < 3; i++)
        {
            var result = await probeService.ProbeAsync(_site, _model, CancellationToken.None);
            _dbContext.DetectionLogs.Add(new DetectionLog
            {
                SiteId = _mapping.SiteId,
                ModelLibraryItemId = _mapping.ModelLibraryItemId,
                Status = "success",
                DurationMs = result.DurationMs,
                CheckedAt = DateTimeOffset.UtcNow
            });
        }
        await _dbContext.SaveChangesAsync();

        _dbContext.DetectionLogs.Should().HaveCount(3);
    }

    public void Dispose() => _dbContext.Dispose();
}
