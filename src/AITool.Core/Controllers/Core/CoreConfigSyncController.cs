using AITool.Application.CoreRuntime;
using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Core.Controllers.Core;

/// <summary>
/// Core 配置同步接口。
/// 提供全量同步（full-sync）和增量同步（patch-sync）两种模式：
/// <list type="bullet">
///   <item>全量同步：Admin 下发完整快照，Core 整体替换并刷新所有缓存。</item>
///   <item>增量同步：Admin 仅下发变更的实体类别，Core 只替换对应集合并定向失效缓存。</item>
/// </list>
/// 启动阶段和首次连接始终走全量同步；运行期单类别变更走增量同步以减少传输开销。
/// </summary>
[ApiController]
[Route("api/core/config")]
public sealed class CoreConfigSyncController : ControllerBase
{
    /// <summary>
    /// 已知实体类别名称，用于校验 Patch 请求中的 Categories 字段。
    /// </summary>
    private static readonly HashSet<string> KnownCategories = new(StringComparer.Ordinal)
    {
        "Sites", "Models", "SiteModelMappings", "RouteEntries", "RouteRules", "AccessKeys", "RuntimeSettings"
    };

    /// <summary>
    /// Core 运行时配置提供器，持有当前生效的完整配置快照。
    /// </summary>
    private readonly ICoreRuntimeConfigProvider _configProvider;

    /// <summary>
    /// 代理请求元数据缓存，同步后需要失效以触发从新快照重建。
    /// </summary>
    private readonly ProxyRequestMetadataCache _metadataCache;

    /// <summary>
    /// 路由熔断状态存储，同步后需要根据新设置更新熔断阈值和恢复时长。
    /// </summary>
    private readonly RouteCircuitStateStore _circuitStore;

    /// <summary>
    /// 配置变更应用事件发布器，配置成功应用后向事件总线发送确认通知。
    /// </summary>
    private readonly CoreConfigAppliedEventPublisher _configAppliedPublisher;

    /// <summary>
    /// 日志记录器。
    /// </summary>
    private readonly ILogger<CoreConfigSyncController> _logger;

    /// <summary>
    /// 初始化 Core 配置同步控制器。
    /// </summary>
    public CoreConfigSyncController(
        ICoreRuntimeConfigProvider configProvider,
        ProxyRequestMetadataCache metadataCache,
        RouteCircuitStateStore circuitStore,
        CoreConfigAppliedEventPublisher configAppliedPublisher,
        ILogger<CoreConfigSyncController> logger)
    {
        _configProvider = configProvider;
        _metadataCache = metadataCache;
        _circuitStore = circuitStore;
        _configAppliedPublisher = configAppliedPublisher;
        _logger = logger;
    }

    /// <summary>
    /// 接收一份完整的 Core 运行时配置快照并使其生效。
    /// 如果版本和哈希都没有变化，则直接返回 ignored，避免 Admin 重启时重复切换内存状态。
    /// </summary>
    [HttpPost("full-sync")]
    public IActionResult FullSync([FromBody] CoreRuntimeConfigSnapshot snapshot)
    {
        if (snapshot is null)
        {
            return BadRequest(new { message = "配置快照不能为空" });
        }

        if (snapshot.ConfigVersion <= 0)
        {
            return BadRequest(new { message = "配置版本号必须大于 0" });
        }

        var computedHash = CoreRuntimeConfigSnapshotBuilder.ComputeHash(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.ConfigHash)
            || !string.Equals(snapshot.ConfigHash, computedHash, StringComparison.Ordinal))
        {
            return BadRequest(new { message = "配置哈希校验失败" });
        }

        var current = _configProvider.GetCurrent();
        if (current is not null
            && current.ConfigVersion == snapshot.ConfigVersion
            && string.Equals(current.ConfigHash, snapshot.ConfigHash, StringComparison.Ordinal))
        {
            return Ok(new
            {
                applied = false,
                ignored = true,
                configVersion = current.ConfigVersion,
                configHash = current.ConfigHash
            });
        }

        // 当前阶段先做最小安全校验，确保 Core 不会吃进明显不完整的主配置。
        if (snapshot.Sites.Count == 0 || snapshot.AccessKeys.Count == 0)
        {
            return BadRequest(new { message = "配置快照缺少必要的站点或访问密钥数据" });
        }

        // 先更新配置快照，再清除代理运行时的所有缓存条目。
        // 缓存失效确保后续请求从新快照重新构建缓存数据。
        _configProvider.SetCurrent(snapshot);
        _metadataCache.InvalidateAccessKeys();
        _metadataCache.InvalidateRuntimeSettings();
        _metadataCache.InvalidateRuntimeRouteTargets();

        // 将新快照中的熔断参数推送到 RouteCircuitStateStore，
        // 使后续请求立即按新的阈值和恢复时长执行熔断判定。
        ApplyCircuitBreakerSettings(snapshot.RuntimeSettings);

        _logger.LogDebug("全量同步完成。Version={Version}, Hash={Hash}", snapshot.ConfigVersion, snapshot.ConfigHash);

        // 配置成功应用后，向事件总线发布确认通知
        var previousVersion = current?.ConfigVersion ?? 0;
        var previousHash = current?.ConfigHash ?? string.Empty;
        _ = _configAppliedPublisher.PublishAsync(
            "full", snapshot.ConfigVersion, snapshot.ConfigHash,
            previousVersion, previousHash,
            cancellationToken: HttpContext.RequestAborted);

        return Ok(new
        {
            applied = true,
            ignored = false,
            configVersion = snapshot.ConfigVersion,
            configHash = snapshot.ConfigHash
        });
    }

    /// <summary>
    /// 接收增量配置变更并合并到当前快照。
    /// 仅替换 Patch 中携带的实体类别集合，未携带的类别保持当前值不变，
    /// 然后根据变更类别定向失效 Core 侧相关缓存。
    /// </summary>
    [HttpPost("patch-sync")]
    public IActionResult PatchSync([FromBody] ConfigPatchPayload patch)
    {
        if (patch is null)
        {
            return BadRequest(new { message = "Patch 载荷不能为空" });
        }

        if (patch.ConfigVersion <= 0)
        {
            return BadRequest(new { message = "配置版本号必须大于 0" });
        }

        if (patch.Categories is null || patch.Categories.Count == 0)
        {
            return BadRequest(new { message = "Patch 必须指定至少一个变更类别" });
        }

        // 校验所有类别名称是否已知
        foreach (var category in patch.Categories)
        {
            if (!KnownCategories.Contains(category))
            {
                return BadRequest(new { message = $"未知的实体类别: {category}" });
            }
        }

        var current = _configProvider.GetCurrent();
        if (current is null)
        {
            // 尚未收到过全量快照时，不接受增量同步，要求 Admin 先做 full-sync
            return BadRequest(new { message = "Core 尚未初始化，请先执行全量同步" });
        }

        // 版本号必须大于当前才能应用
        if (patch.ConfigVersion <= current.ConfigVersion)
        {
            return Ok(new CorePatchSyncResult
            {
                Applied = false,
                Ignored = true,
                ConfigVersion = current.ConfigVersion,
                ConfigHash = current.ConfigHash
            });
        }

        // 将 Patch 数据合并到当前快照的副本中
        var merged = MergePatch(current, patch);

        // 重新计算全量哈希
        merged.ConfigHash = CoreRuntimeConfigSnapshotBuilder.ComputeHash(merged);

        // 如果合并后的哈希和当前完全一致，说明这次 Patch 实际没有带来变化
        if (string.Equals(current.ConfigHash, merged.ConfigHash, StringComparison.Ordinal))
        {
            _logger.LogDebug("Patch 数据与当前快照一致，忽略。Version={Version}", patch.ConfigVersion);
            return Ok(new CorePatchSyncResult
            {
                Applied = false,
                Ignored = true,
                ConfigVersion = current.ConfigVersion,
                ConfigHash = current.ConfigHash
            });
        }

        // 原子替换快照
        _configProvider.SetCurrent(merged);

        // 根据变更类别定向失效缓存
        InvalidateCacheForCategories(patch.Categories);

        // 如果运行时设置发生了变化，同步更新熔断参数
        if (patch.Categories.Contains("RuntimeSettings", StringComparer.Ordinal))
        {
            ApplyCircuitBreakerSettings(merged.RuntimeSettings);
        }

        _logger.LogDebug(
            "增量同步完成。Version={Version}, Categories=[{Categories}], Hash={Hash}",
            merged.ConfigVersion,
            string.Join(", ", patch.Categories),
            merged.ConfigHash);

        // 配置成功应用后，向事件总线发布确认通知
        _ = _configAppliedPublisher.PublishAsync(
            "patch", merged.ConfigVersion, merged.ConfigHash,
            current.ConfigVersion, current.ConfigHash,
            patch.Categories,
            HttpContext.RequestAborted);

        return Ok(new CorePatchSyncResult
        {
            Applied = true,
            Ignored = false,
            ConfigVersion = merged.ConfigVersion,
            ConfigHash = merged.ConfigHash
        });
    }

    /// <summary>
    /// 将 Patch 中的变更类别合并到当前快照的深拷贝中。
    /// 只替换 Patch 携带的类别集合，其他保持原值。
    /// </summary>
    private static CoreRuntimeConfigSnapshot MergePatch(CoreRuntimeConfigSnapshot current, ConfigPatchPayload patch)
    {
        // 构建新的快照，从当前快照复制所有数据
        var merged = new CoreRuntimeConfigSnapshot
        {
            ConfigVersion = patch.ConfigVersion,
            GeneratedAt = DateTimeOffset.UtcNow,
            // 以下集合：如果 Patch 携带了新值则用新值，否则用当前快照的引用
            Sites = patch.Sites ?? current.Sites,
            Models = patch.Models ?? current.Models,
            SiteModelMappings = patch.SiteModelMappings ?? current.SiteModelMappings,
            RouteEntries = patch.RouteEntries ?? current.RouteEntries,
            RouteRules = patch.RouteRules ?? current.RouteRules,
            AccessKeys = patch.AccessKeys ?? current.AccessKeys,
            RuntimeSettings = patch.RuntimeSettings ?? current.RuntimeSettings
        };

        return merged;
    }

    /// <summary>
    /// 根据变更类别定向失效 Core 侧缓存。
    /// 仅失效受影响类别对应的缓存区域，避免无关缓存被无谓清除。
    /// </summary>
    private void InvalidateCacheForCategories(List<string> categories)
    {
        var categorySet = new HashSet<string>(categories, StringComparer.Ordinal);

        // 访问密钥变更 → 失效密钥缓存
        if (categorySet.Contains("AccessKeys"))
        {
            _metadataCache.InvalidateAccessKeys();
        }

        // 运行时设置变更 → 失效设置缓存
        if (categorySet.Contains("RuntimeSettings"))
        {
            _metadataCache.InvalidateRuntimeSettings();
        }

        // 站点、路由规则、路由主入口变更都会影响路由选择
        if (categorySet.Contains("Sites")
            || categorySet.Contains("RouteRules")
            || categorySet.Contains("RouteEntries"))
        {
            _metadataCache.InvalidateRuntimeRouteTargets();
        }

        // 模型或站点模型映射变更 → 影响兜底映射和已启用模型
        if (categorySet.Contains("Models") || categorySet.Contains("SiteModelMappings"))
        {
            _metadataCache.InvalidateRuntimeRouteTargets();
        }
    }

    /// <summary>
    /// 从配置快照的 RuntimeSettings 中提取熔断参数并应用到熔断状态存储。
    /// </summary>
    private void ApplyCircuitBreakerSettings(CoreRuntimeSettings? settings)
    {
        if (settings is null)
        {
            return;
        }

        _circuitStore.UpdateOptions(
            TimeSpan.FromMinutes(settings.CircuitBreakerRecoveryMinutes),
            settings.CircuitBreakerFailureThreshold);
    }
}
