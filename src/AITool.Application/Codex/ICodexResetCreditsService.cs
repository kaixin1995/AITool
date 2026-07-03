using AITool.Domain.Codex;

namespace AITool.Application.Codex;

/// <summary>
/// Codex 手动重置额度 credits 服务接口。
/// </summary>
public interface ICodexResetCreditsService
{
    /// <summary>
    /// 查询账号的手动重置 credits 信息（剩余次数 + 每张过期时间）。
    /// </summary>
    Task<CodexResetCreditsInfo> QueryResetCreditsAsync(CodexAccount account, CancellationToken ct);

    /// <summary>
    /// 消耗一张 reset credit，执行真实额度重置。
    /// </summary>
    /// <param name="account">账号</param>
    /// <param name="redeemRequestId">幂等请求 ID（UUID）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>执行结果（成功/失败/错误信息）</returns>
    Task<(bool Success, string? Error)> ConsumeResetCreditAsync(CodexAccount account, string redeemRequestId, CancellationToken ct);
}
