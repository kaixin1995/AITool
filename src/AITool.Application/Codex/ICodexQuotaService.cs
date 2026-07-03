using AITool.Domain.Codex;

namespace AITool.Application.Codex;

/// <summary>
/// Codex 额度主动查询服务：调上游 usage 接口，展示剩余额度，并在剩余额度低于阈值时自动禁用账号。
/// <para>
/// 注意：上游「剩余额度数字」端点不确定（CPA 不主动查额度；new-api 有 /codex/usage 但结构需实测）。
/// 本服务提供完整框架，端点确认后补充解析；端点不可用时降级为仅返回状态，不影响账号可用性。
/// </para>
/// </summary>
public interface ICodexQuotaService
{
    /// <summary>
    /// 查询指定账号额度。forceRefresh=false 时走 30s 结果缓存防抖；true 时穿透缓存。
    /// 查询成功会持久化 LastQuotaRawJson/LastQuotaCheckedAt，并在剩余额度低于阈值时自动禁用。
    /// </summary>
    Task<CodexQuotaInfo> QueryAsync(CodexAccount account, bool forceRefresh, CancellationToken cancellationToken);
}
