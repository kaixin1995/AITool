using AITool.Application.CoreRuntime;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;

using AITool.Core.Services;
namespace AITool.Core.Controllers.Core;

/// <summary>
/// 开发者工具查询接口。
/// 提供 Admin 宿主所需的开发者调用追踪、模型并发检测、客户端模拟器元数据等运行时数据查询能力。
/// Core 宿主持有所有代理运行时的内存数据，Admin 通过此控制器间接读取。
/// </summary>
[ApiController]
[Route("api/core/developer")]
public sealed class CoreDeveloperQueryController : ControllerBase
{
    /// <summary>
    /// 模型并发只读查询服务。
    /// </summary>
    private readonly ModelConcurrencyQueryService _concurrencyQuery;

    /// <summary>
    /// 后台查询元数据服务，提供默认密钥、模型列表等信息。
    /// </summary>
    private readonly AdminQueryMetadataService _metadataService;

    /// <summary>
    /// 初始化开发者查询控制器。
    /// </summary>
    public CoreDeveloperQueryController(
        ModelConcurrencyQueryService concurrencyQuery,
        AdminQueryMetadataService metadataService)
    {
        _concurrencyQuery = concurrencyQuery;
        _metadataService = metadataService;
    }

    /// <summary>
    /// 查询当前模型并发状态快照。
    /// 返回最近 6 小时内出现过的模型并发记录，包括活跃数、排队数和配置上限。
    /// </summary>
    /// <param name="cancellationToken">请求取消令牌。</param>
    [HttpGet("concurrency")]
    public async Task<IActionResult> Concurrency(CancellationToken cancellationToken = default)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound(new { message = "开发者功能未启用" });
        }

        // 获取配置中的并发上限映射，用于补充 MaxConcurrency 字段。
        var concurrencyLimits = await _metadataService.GetModelConcurrencyLimitsAsync(cancellationToken);

        var entries = _concurrencyQuery.ListRecent(ModelConcurrencyQueryService.RecentRetention);
        var items = entries.Select(e =>
        {
            // MaxConcurrency 在 ActiveModelConcurrencyEntry 中为 0 表示不限制，
            // 在传输 DTO 中使用 null 表示不限制，保持语义一致。
            int? maxConcurrency = e.MaxConcurrency > 0 ? e.MaxConcurrency : null;
            return new CoreDeveloperConcurrencyItem
            {
                ModelName = e.SiteModelName,
                SiteName = string.Empty,
                ActiveCount = e.ActiveCount,
                MaxConcurrency = maxConcurrency,
                QueueCount = e.QueueCount
            };
        }).ToList();

        return Ok(new CoreDeveloperConcurrencyResponse
        {
            RefreshedAt = DateTimeOffset.UtcNow,
            Items = items
        });
    }

    /// <summary>
    /// 查询客户端模拟器所需的元数据。
    /// 返回默认访问密钥、默认模型名称和可用的调试模型列表。
    /// </summary>
    /// <param name="cancellationToken">请求取消令牌。</param>
    [HttpGet("metadata")]
    public async Task<IActionResult> Metadata(CancellationToken cancellationToken = default)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound(new { message = "开发者功能未启用" });
        }

        var accessKey = await _metadataService.GetDeveloperDefaultAccessKeyAsync(cancellationToken);
        var models = await _metadataService.GetDeveloperDebugModelsAsync(cancellationToken);

        // 从模型列表中推断默认 OpenAI 和 Anthropic 模型名称。
        string defaultOpenAiModel = string.Empty;
        string defaultAnthropicModel = string.Empty;
        foreach (var m in models)
        {
            // 优先选择支持原生协议的模型作为默认值
            if (m.SupportsOpenAi && string.IsNullOrEmpty(defaultOpenAiModel))
            {
                defaultOpenAiModel = m.ModelName;
            }
            if (m.SupportsAnthropic && string.IsNullOrEmpty(defaultAnthropicModel))
            {
                defaultAnthropicModel = m.ModelName;
            }
            // 如果都找到了就提前退出
            if (!string.IsNullOrEmpty(defaultOpenAiModel) && !string.IsNullOrEmpty(defaultAnthropicModel))
            {
                break;
            }
        }

        return Ok(new CoreDeveloperMetadataResponse
        {
            DefaultAccessKey = accessKey,
            DefaultOpenAiModel = defaultOpenAiModel,
            DefaultAnthropicModel = defaultAnthropicModel,
            Models = models.Select(m => new CoreDeveloperModelItem
            {
                ModelName = m.ModelName,
                RouteCount = m.RouteCount,
                SupportsOpenAi = m.SupportsOpenAi,
                SupportsAnthropic = m.SupportsAnthropic,
                CanUseOpenAi = m.CanUseOpenAi,
                CanUseAnthropic = m.CanUseAnthropic
            }).ToList()
        });
    }

    /// <summary>
    /// 检查开发者功能是否启用。
    /// 读取运行时设置中的开发者功能开关，未启用时所有开发者端点返回 404。
    /// </summary>
    private async Task<bool> IsDeveloperEnabledAsync(CancellationToken cancellationToken)
    {
        var runtimeSettings = await _metadataService.GetRuntimeSettingsAsync(cancellationToken);
        return runtimeSettings.DeveloperFeaturesEnabled;
    }

}
