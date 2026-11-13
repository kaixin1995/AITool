using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

[ApiController]
[Route("api/admin/agent-configuration")]
public sealed class AgentConfigurationApiController(AgentConfigurationService configurations) : ControllerBase
{
    [HttpGet("{agent}")]
    public IActionResult Status(string agent) => agent is "codex" or "claude" ? Ok(configurations.Status(agent)) : BadRequest();
    [HttpPost("preview")]
    public IActionResult Preview(AgentConfigurationRequest request)
    {
        try { return Ok(new { content = configurations.Preview(request), note = request.Agent == "codex" ? "请在启动 Codex 的环境设置 AITOOL_API_KEY。WSL 可将预览写入发行版的 ~/.codex/config.toml。" : "配置将写入服务所在主机的 Claude 配置。" }); }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException or InvalidOperationException) { return BadRequest(new { message = ex.Message }); }
    }
    [HttpPost("apply")]
    public Task<IActionResult> Apply(AgentConfigurationRequest request, CancellationToken ct) => Change(request, false, ct);
    [HttpPost("restore")]
    public Task<IActionResult> Restore(AgentConfigurationRequest request, CancellationToken ct) => Change(request, true, ct);
    private async Task<IActionResult> Change(AgentConfigurationRequest request, bool restore, CancellationToken ct)
    {
        // Remote servers must never modify a different machine's agent configuration.
        if (HttpContext.Connection.RemoteIpAddress is not { } ip || !System.Net.IPAddress.IsLoopback(ip)) return StatusCode(403, new { message = "配置写入仅允许本机管理连接；远程和 WSL 请使用预览导出。" });
        try { return Ok(await configurations.ApplyAsync(request, restore, ct)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Text.Json.JsonException) { return BadRequest(new { message = ex.Message }); }
    }
}
