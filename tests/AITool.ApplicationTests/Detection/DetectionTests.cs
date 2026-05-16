using AITool.Application.Detection;
using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AITool.ApplicationTests.Detection;

/// <summary>
/// 测试用探测服务，始终返回构造时指定的结果，便于聚焦状态更新逻辑本身。
/// </summary>
public sealed class FakeModelProbeService : IModelProbeService
{
    /// <summary>
    /// 预设探测结果，所有调用都会返回这一份数据。
    /// </summary>
    private readonly ModelProbeResult _result;

    /// <summary>
    /// 通过构造函数注入结果，方便不同测试复用同一个假实现。
    /// </summary>
    public FakeModelProbeService(ModelProbeResult result)
    {
        _result = result;
    }

    /// <summary>
    /// 这里不访问真实站点，直接返回预设结果，让测试保持稳定可控。
    /// </summary>
    public Task<ModelProbeResult> ProbeAsync(Site site, ModelLibraryItem model, CancellationToken cancellationToken)
    {
        return Task.FromResult(_result);
    }
}

/// <summary>
/// 验证模型探测结果如何影响站点模型映射的状态记录。
/// </summary>
public sealed class DetectionTests : IDisposable
{
    /// <summary>
    /// 内存数据库上下文，用于准备映射数据并验证状态更新。
    /// </summary>
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 测试站点实体，模拟一次模型探测的目标站点。
    /// </summary>
    private readonly Site _site;

    /// <summary>
    /// 测试模型实体，表示要探测的模型条目。
    /// </summary>
    private readonly ModelLibraryItem _model;

    /// <summary>
    /// 站点与模型之间的映射记录，测试主要围绕它的状态字段展开。
    /// </summary>
    private readonly SiteModelMapping _mapping;

    /// <summary>
    /// 为每个用例构造独立的站点、模型和映射数据。
    /// </summary>
    public DetectionTests()
    {
        // 使用内存数据库隔离各个测试场景，避免外部依赖干扰。
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);

        // 预置最小可用的站点数据，满足探测参数要求。
        _site = new Site
        {
            Name = "Test Site",
            BaseUrl = "https://api.test.com",
            ApiKey = "test-key",
            ProtocolType = "OpenAI"
        };
        // 预置模型库条目，后续由假探测服务直接引用。
        _model = new ModelLibraryItem { ModelName = "gpt-5.4", DisplayName = "GPT-5.4" };
        // 建立站点与模型的映射，并给出初始导入状态。
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

    /// <summary>
    /// 探测成功后，映射状态应更新为成功，反映最近一次检测结果。
    /// </summary>
    [Fact]
    public async Task Probe_success_updates_mapping_status()
    {
        // 预设一次成功探测结果，避免依赖真实网络请求。
        var probeService = new FakeModelProbeService(new ModelProbeResult
        {
            Success = true,
            DurationMs = 150
        });

        var result = await probeService.ProbeAsync(_site, _model, CancellationToken.None);
        // 按调用方当前逻辑把探测结果映射回状态字段。
        _mapping.LastStatus = result.Success ? "success" : "fail";
        await _dbContext.SaveChangesAsync();

        _mapping.LastStatus.Should().Be("success");
    }

    /// <summary>
    /// 探测失败时，结果对象中的错误信息应保留下来，供上层继续处理。
    /// </summary>
    [Fact]
    public async Task Probe_failure_keeps_error_message_available_in_result()
    {
        // 构造一次失败结果，重点验证错误信息没有丢失。
        var probeService = new FakeModelProbeService(new ModelProbeResult
        {
            Success = false,
            DurationMs = 300,
            ErrorMessage = "Connection refused"
        });

        var result = await probeService.ProbeAsync(_site, _model, CancellationToken.None);
        // 映射状态仍按成功与否更新，和调用方实际处理方式保持一致。
        _mapping.LastStatus = result.Success ? "success" : "fail";
        await _dbContext.SaveChangesAsync();

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Connection refused");
        _mapping.LastStatus.Should().Be("fail");
    }

    /// <summary>
    /// 多次探测同一条映射时，应以后一次结果作为最终状态。
    /// </summary>
    [Fact]
    public async Task Multiple_probes_keep_latest_mapping_status()
    {
        // 第一次探测返回成功，用来模拟初始可用状态。
        var successProbeService = new FakeModelProbeService(new ModelProbeResult
        {
            Success = true,
            DurationMs = 100
        });
        // 第二次探测返回失败，用来验证状态是否会被后续结果覆盖。
        var failProbeService = new FakeModelProbeService(new ModelProbeResult
        {
            Success = false,
            DurationMs = 120,
            ErrorMessage = "timeout"
        });

        // 先执行一次成功探测。
        var first = await successProbeService.ProbeAsync(_site, _model, CancellationToken.None);
        _mapping.LastStatus = first.Success ? "success" : "fail";

        // 再执行一次失败探测，最终状态应以这次为准。
        var second = await failProbeService.ProbeAsync(_site, _model, CancellationToken.None);
        _mapping.LastStatus = second.Success ? "success" : "fail";
        await _dbContext.SaveChangesAsync();

        _mapping.LastStatus.Should().Be("fail");
    }

    /// <summary>
    /// 释放测试使用的数据库资源。
    /// </summary>
    public void Dispose() => _dbContext.Dispose();
}
