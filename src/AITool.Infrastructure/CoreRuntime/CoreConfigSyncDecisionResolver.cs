using AITool.Application.CoreRuntime;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// 根据 Admin 与 Core 当前配置状态推导同步决策。
/// 集中管理握手后的配置对齐判断，涵盖全量同步、增量 patch 和版本不匹配等场景。
/// </summary>
public static class CoreConfigSyncDecisionResolver
{
    /// <summary>
    /// 推导当前应执行的配置对齐动作。
    /// </summary>
    public static string Resolve(CoreAdminHandshakeRequest request, CoreRuntimeConfigSnapshot? current)
    {
        if (current is null)
        {
            return "full-sync-required";
        }

        if (request.CurrentConfigVersion == current.ConfigVersion
            && string.Equals(request.CurrentConfigHash, current.ConfigHash, StringComparison.Ordinal))
        {
            return "noop";
        }

        if (request.CurrentConfigVersion == current.ConfigVersion)
        {
            return "full-sync-required";
        }

        if (request.CurrentConfigVersion > current.ConfigVersion)
        {
            return "full-sync-required";
        }

        return "admin-version-behind";
    }
}
