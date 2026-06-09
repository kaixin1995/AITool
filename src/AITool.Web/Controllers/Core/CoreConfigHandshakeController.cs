using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Core;

/// <summary>
/// Core 与 Admin 建立控制通道时的握手接口。
/// 当前阶段先提供最小决策能力，让 Admin 可以判断是否需要重新发送完整配置。
/// </summary>
[ApiController]
[Route("api/core/config")]
public sealed class CoreConfigHandshakeController : ControllerBase
{
    private static readonly string CoreInstanceId = $"core-{Environment.ProcessId}";
    private static readonly DateTimeOffset CoreStartedAt = DateTimeOffset.UtcNow;
    private readonly ICoreRuntimeConfigProvider _configProvider;

    /// <summary>
    /// 初始化握手控制器。
    /// </summary>
    public CoreConfigHandshakeController(ICoreRuntimeConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    /// <summary>
    /// 返回 Core 当前配置状态与建议的同步动作。
    /// </summary>
    [HttpPost("handshake")]
    public ActionResult<CoreAdminHandshakeResponse> Handshake([FromBody] CoreAdminHandshakeRequest request)
    {
        if (request is null)
        {
            return BadRequest(new { message = "握手请求不能为空" });
        }

        var current = _configProvider.GetCurrent();
        var decision = CoreConfigSyncDecisionResolver.Resolve(request, current);
        return Ok(new CoreAdminHandshakeResponse
        {
            CoreInstanceId = CoreInstanceId,
            CoreStartedAt = CoreStartedAt,
            AppliedConfigVersion = current?.ConfigVersion ?? 0L,
            AppliedConfigHash = current?.ConfigHash ?? string.Empty,
            Ready = _configProvider.IsReady,
            LatestSequenceId = 0L,
            ActiveRequestCount = 0,
            ConfigSyncDecision = decision
        });
    }
}
