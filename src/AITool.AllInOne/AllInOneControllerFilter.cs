using Microsoft.AspNetCore.Mvc.ApplicationParts;

namespace AITool.AllInOne;

/// <summary>
/// AllInOne 控制器白名单过滤器：追加 Admin/Core 两宿主程序集后，排除会与
/// 单进程形态冲突的控制器：
/// <list type="bullet">
///   <item>Admin 版 ChatApiController —— 聊天真转发链路由 Core 版提供（含 401 刷新/心跳/可恢复流）</item>
///   <item>V1RelayController —— AllInOne 的 /v1 直接由 Core 代理控制器提供，中继无意义</item>
/// </list>
/// </summary>
public sealed class AllInOneControllerFilter : IApplicationFeatureProvider<Microsoft.AspNetCore.Mvc.Controllers.ControllerFeature>
{
    private static readonly List<Type> ExcludedTypes = new()
    {
        typeof(AITool.Admin.Controllers.Admin.ChatApiController),
        typeof(AITool.Admin.Controllers.Proxy.V1RelayController)
    };

    public void PopulateFeature(IEnumerable<ApplicationPart> parts, Microsoft.AspNetCore.Mvc.Controllers.ControllerFeature feature)
    {
        for (var i = feature.Controllers.Count - 1; i >= 0; i--)
        {
            if (ExcludedTypes.Contains(feature.Controllers[i].AsType()))
            {
                feature.Controllers.RemoveAt(i);
            }
        }
    }
}