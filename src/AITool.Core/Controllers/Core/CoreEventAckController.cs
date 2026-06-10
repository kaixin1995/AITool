using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Core.Controllers.Core;

/// <summary>
/// Core 事件确认与 replay 接口。
/// 当前阶段先提供最小 ack 能力与 replay 读取能力，后续再接入长连接实时消费端。
/// </summary>
[ApiController]
[Route("api/core/events")]
public sealed class CoreEventAckController : ControllerBase
{
    private readonly CoreEventSpoolStore _spoolStore;

    /// <summary>
    /// 初始化 Core 事件确认控制器。
    /// </summary>
    public CoreEventAckController(CoreEventSpoolStore spoolStore)
    {
        _spoolStore = spoolStore;
    }

    /// <summary>
    /// 提交一条连续 ack，清理对应已确认事件。
    /// </summary>
    [HttpPost("ack")]
    public async Task<IActionResult> Ack([FromBody] CoreAdminAckRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { message = "确认请求不能为空" });
        }

        if (request.AckedSequenceId < 0)
        {
            return BadRequest(new { message = "AckedSequenceId 不能小于 0" });
        }

        await _spoolStore.TrimAckedAsync(request.AckedSequenceId, cancellationToken);
        return Ok(new
        {
            ackedSequenceId = request.AckedSequenceId,
            ackedAt = request.AckedAt
        });
    }

    /// <summary>
    /// 返回指定序号之后的积压事件，供后续 Admin 重连补传使用。
    /// 当前阶段先提供最小 GET 读取能力，后续再升级为专门的 replay 协议。
    /// </summary>
    [HttpGet("replay")]
    public async Task<ActionResult<IReadOnlyList<CoreAdminEventEnvelope>>> Replay([FromQuery] long afterSequenceId, CancellationToken cancellationToken)
    {
        if (afterSequenceId < 0)
        {
            return BadRequest(new { message = "afterSequenceId 不能小于 0" });
        }

        var events = await _spoolStore.ListAfterAsync(afterSequenceId, cancellationToken);
        return Ok(events);
    }
}
