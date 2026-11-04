namespace AITool.Application.Codex;

/// <summary>
/// 本地额度重置结果。超时后真实状态可能未知，调用方必须提示重试，不得当作成功。
/// </summary>
public sealed record CodexQuotaResetResult(bool Completed, bool CoolingCleared, string? UnconfirmedStep)
{
    public static CodexQuotaResetResult Success() => new(true, true, null);

    public static CodexQuotaResetResult Unconfirmed(bool coolingCleared, string step) => new(false, coolingCleared, step);
}
