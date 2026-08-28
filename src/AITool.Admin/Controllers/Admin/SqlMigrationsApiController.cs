using AITool.Application.Operations;
using AITool.Application.Common;
using AITool.Admin.Services;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// SQL 迁移脚本执行 API：列出服务器 sql-migrations 目录下的 .sql 脚本并触发执行（含试运行）。
/// <para>
/// 安全约束：接口不接收 SQL 文本，只接受目录内已存在的文件名；每次执行必须重新校验管理员密码；
/// 受 /api/admin/* JWT 中间件与开发者功能开关双重保护；执行全程事务并写入审计表。
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/sql-migrations")]
public sealed class SqlMigrationsApiController : ControllerBase
{
    private readonly SqlMigrationRunnerService _runner;
    private readonly ISystemRuntimeSettingsService _runtimeSettingsService;

    public SqlMigrationsApiController(
        SqlMigrationRunnerService runner,
        ISystemRuntimeSettingsService runtimeSettingsService)
    {
        _runner = runner;
        _runtimeSettingsService = runtimeSettingsService;
    }

    /// <summary>
    /// 列出脚本目录下的全部 .sql 文件及其执行历史汇总。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        var scripts = await _runner.ListScriptsAsync(cancellationToken);
        return Ok(ApiResponse.Ok(new
        {
            directory = _runner.ScriptsDirectory,
            directoryExists = System.IO.Directory.Exists(_runner.ScriptsDirectory),
            scripts
        }));
    }

    /// <summary>
    /// 执行（或试运行）指定脚本。请求体只含管理员密码与是否试运行，不含任何 SQL 文本。
    /// </summary>
    /// <param name="fileName">脚本文件名，必须与目录枚举结果精确匹配。</param>
    /// <param name="request">管理员密码 + dryRun 标记。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("{fileName}/execute")]
    public async Task<IActionResult> Execute(string fileName, [FromBody] ExecuteSqlMigrationRequest? request, CancellationToken cancellationToken)
    {
        if (!await IsDeveloperEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(ApiResponse.Fail("请输入管理员密码", "password_required"));
        }

        // 请求体缺省 dryRun 字段时默认试运行：危险端点上"少传一个字段就真实执行"不可接受。
        var dryRun = request.DryRun ?? true;

        try
        {
            var result = await _runner.ExecuteAsync(
                fileName,
                request.Password,
                dryRun,
                ResolveClientIp(),
                cancellationToken);
            return Ok(ApiResponse.Ok(result, dryRun ? "试运行完成（已回滚）" : "执行完成"));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(ApiResponse.Fail(ex.Message, "script_not_found"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message, "execution_rejected"));
        }
    }

    /// <summary>
    /// 获取客户端真实 IP：仅当直连 IP 是回环地址（反向代理场景）时信任 X-Forwarded-For，
    /// 与登录接口取值逻辑一致，保证审计与限流按真实来源统计。
    /// </summary>
    private string? ResolveClientIp()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (remoteIp is null) return null;

        if (System.Net.IPAddress.IsLoopback(remoteIp))
        {
            var forwarded = Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                return forwarded.Split(',')[0].Trim();
            }
        }

        return remoteIp.ToString();
    }

    /// <summary>
    /// 开发者功能关闭时接口整体隐藏。
    /// </summary>
    private async Task<bool> IsDeveloperEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        return settings is not null && settings.DeveloperFeaturesEnabled;
    }
}

/// <summary>
/// 执行脚本请求体。
/// </summary>
public sealed record ExecuteSqlMigrationRequest(string? Password, bool? DryRun = null);
