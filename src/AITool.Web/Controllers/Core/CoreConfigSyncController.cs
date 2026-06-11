using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Core;

/// <summary>
/// Core 全量配置同步接口。
/// 当前阶段先只提供全量同步闭环，确保 Admin 能把一份完整快照下发给 Core。
/// 增量 patch、事件流和补传会在后续阶段逐步接入。
/// </summary>
[ApiController]
[Route("api/core/config")]
public sealed class CoreConfigSyncController : ControllerBase
{
    private readonly ICoreRuntimeConfigProvider _configProvider;
    private readonly CoreConfigAppliedEventPublisher _configAppliedPublisher;

    /// <summary>
    /// 初始化 Core 配置同步控制器。
    /// </summary>
    public CoreConfigSyncController(
        ICoreRuntimeConfigProvider configProvider,
        CoreConfigAppliedEventPublisher configAppliedPublisher)
    {
        _configProvider = configProvider;
        _configAppliedPublisher = configAppliedPublisher;
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

        _configProvider.SetCurrent(snapshot);

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
}
