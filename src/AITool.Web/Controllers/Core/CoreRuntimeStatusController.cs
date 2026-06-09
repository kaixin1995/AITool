using AITool.Application.CoreRuntime;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Core;

/// <summary>
/// Core 运行时健康与就绪接口。
/// </summary>
[ApiController]
[Route("api/core")]
public sealed class CoreRuntimeStatusController : ControllerBase
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
    private readonly ICoreRuntimeConfigProvider _configProvider;

    /// <summary>
    /// 初始化 Core 运行时状态控制器。
    /// </summary>
    public CoreRuntimeStatusController(ICoreRuntimeConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    /// <summary>
    /// 健康检查，只要进程正常即可返回 ok。
    /// </summary>
    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { status = "ok" });
    }

    /// <summary>
    /// 就绪检查，只有配置快照已加载时才视为 ready。
    /// </summary>
    [HttpGet("ready")]
    public IActionResult Ready()
    {
        return Ok(new
        {
            ready = _configProvider.IsReady,
            reason = _configProvider.IsReady ? string.Empty : "No runtime config snapshot loaded"
        });
    }

    /// <summary>
    /// 返回 Core 当前运行时状态，供后续 Admin 握手和诊断使用。
    /// </summary>
    [HttpGet("runtime/status")]
    public IActionResult RuntimeStatus()
    {
        var current = _configProvider.GetCurrent();
        return Ok(new
        {
            state = _configProvider.IsReady ? "ready" : "not-ready",
            coreStartedAt = StartedAt,
            activeRequestCount = 0,
            latestSequenceId = 0L,
            appliedConfigVersion = current?.ConfigVersion ?? 0L,
            appliedConfigHash = current?.ConfigHash ?? string.Empty
        });
    }
}
