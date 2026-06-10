using AITool.Application.CoreRuntime;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Core.Controllers.Core;

/// <summary>
/// Core 运行时配置状态接口，供后续 Admin 同步与诊断使用。
/// 当前阶段先提供只读状态，后续再补配置下发接口。
/// </summary>
[ApiController]
[Route("api/core/config")]
public sealed class CoreConfigController : ControllerBase
{
    private readonly ICoreRuntimeConfigProvider _configProvider;

    /// <summary>
    /// 初始化 Core 配置控制器。
    /// </summary>
    public CoreConfigController(ICoreRuntimeConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    /// <summary>
    /// 返回当前 Core 已生效的配置状态。
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var current = _configProvider.GetCurrent();
        if (current is null)
        {
            return Ok(new
            {
                ready = false,
                configVersion = 0L,
                configHash = string.Empty,
                generatedAt = (DateTimeOffset?)null,
                hasLastGoodConfig = false
            });
        }

        return Ok(new
        {
            ready = true,
            configVersion = current.ConfigVersion,
            configHash = current.ConfigHash,
            generatedAt = current.GeneratedAt,
            hasLastGoodConfig = true
        });
    }
}
