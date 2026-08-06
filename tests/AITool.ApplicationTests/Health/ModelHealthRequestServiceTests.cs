using AITool.Application.Proxy;
using AITool.Application.UsageLogs;
using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Health;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Sites;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.ApplicationTests.Health;

/// <summary>
/// 验证真实请求式检测会把上游 HTTP 状态码传递到使用日志。
/// </summary>
public sealed class ModelHealthRequestServiceTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly AppDbContext _dbContext;
    private readonly CapturingUsageLogService _usageLogService;
    private readonly FakeProxyForwardService _forwardService;
    private readonly Guid _mappingId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public ModelHealthRequestServiceTests()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"aitool-health-test-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddSqlSugar($"Data Source={databasePath}");
        _serviceProvider = services.BuildServiceProvider();
        _dbContext = _serviceProvider.GetRequiredService<AppDbContext>();
        SqlSugarSetup.InitializeDatabase(_serviceProvider.GetRequiredService<SqlSugar.ISqlSugarClient>());
        _usageLogService = new CapturingUsageLogService();
        _forwardService = new FakeProxyForwardService { Result = new ProxyForwardResult { Success = false, StatusCode = 429, ErrorMessage = "rate limited" } };

        SeedAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 检测失败时应保留转发结果中的 HTTP 状态码，而不是只记录错误文本。
    /// </summary>
    [Fact]
    public async Task ProbeMappingAsync_copies_forward_http_status_code_to_usage_log()
    {
        var service = new ModelHealthRequestService(_dbContext, _forwardService, _usageLogService, new SiteKeySelector(_dbContext));

        var result = await service.ProbeMappingAsync(_mappingId, "detection-test", CancellationToken.None);

        result.Status.Should().Be("fail");
        _usageLogService.Entry.Should().NotBeNull();
        _usageLogService.Entry!.HttpStatusCode.Should().Be(429);
    }

    private async Task SeedAsync()
    {
        var siteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var modelId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        _dbContext.Sites.Add(new Site
        {
            Id = siteId,
            Name = "Health Test Site",
            BaseUrl = "https://health-test.example.com",
            ApiKey = "test-key",
            SupportsOpenAi = true,
            SupportsAnthropic = false,
            IsEnabled = true
        });
        _dbContext.ModelLibraryItems.Add(new ModelLibraryItem
        {
            Id = modelId,
            ModelName = "health-model",
            DisplayName = "Health Model",
            IsEnabled = true
        });
        _dbContext.SiteModelMappings.Add(new SiteModelMapping
        {
            Id = _mappingId,
            SiteId = siteId,
            ModelLibraryItemId = modelId,
            RemoteModelName = "health-model-remote",
            IsEnabled = true
        });
        _dbContext.SystemRuntimeSettings.Add(new AITool.Domain.Operations.SystemRuntimeSettings
        {
            Id = 1,
            DetectionRequestTimeoutSeconds = 10,
            DetectionRetryCount = 0
        });
        await _dbContext.SaveChangesAsync();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }

    private sealed class CapturingUsageLogService : IUsageLogService
    {
        public UsageLogEntry? Entry { get; private set; }

        public Task LogAsync(UsageLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entry = entry;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProxyForwardService : IProxyForwardService
    {
        public ProxyForwardResult Result { get; init; } = new();

        public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(Result);

        public Task<ProxyForwardResult> ForwardStreamingAsync(
            ProxyForwardRequest request,
            Func<string, CancellationToken, Task> onSseDataAsync,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result);
    }
}
