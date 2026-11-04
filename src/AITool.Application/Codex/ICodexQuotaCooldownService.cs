namespace AITool.Application.Codex;

/// <summary>
/// Codex 额度被动冷却与重置服务。
/// <para>
/// 被动冷却：在转发错误处理分支解析上游 usage_limit_reached / 429，标记账号冷却（临时禁用）。
/// 重置：清冷却、刷新 token、按账号启用状态恢复 Site（前端二次确认后调用）。
/// </para>
/// </summary>
public interface ICodexQuotaCooldownService
{
    /// <summary>
    /// 判定上游错误响应是否为 Codex usage limit，若是则对该账号应用冷却（标记 + 禁用 Site + 失效缓存）。
    /// 非 Codex Site 或非 usage_limit 错误返回 false（零副作用）。
    /// </summary>
    Task<bool> TryApplyCooldownFromErrorAsync(int httpStatus, string? responseBody, Guid linkedSiteId, CancellationToken cancellationToken);

    /// <summary>
    /// 重置指定账号：刷新 token、清冷却、按账号启用状态恢复 Site、失效缓存。
    /// 注意：重置不能凭空增加上游真实额度，仅清除本地冷却状态。若上游额度仍未恢复会再次触发冷却。
    /// Completed=false 表示清冷却或站点恢复至少一步未确认，账号可能仍在冷却或站点仍禁用，必须提示重试。
    /// </summary>
    Task<CodexQuotaResetResult> ResetAsync(Guid codexAccountId, CancellationToken cancellationToken);
}
