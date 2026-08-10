namespace AITool.Web.Contracts;

/// <summary>
/// 客户端模拟器/开发者调试页中的模型展示项。
/// <para>原定义在 Pages/Admin/ClientSimulator/Index.cshtml.cs，Razor Pages 下线后迁移至此独立文件，
/// 供 ProxyRequestMetadataCache.GetDeveloperDebugModelsAsync 和前端 API 复用。</para>
/// </summary>
public sealed class ClientSimulatorModelItemViewModel
{
    /// <summary>
    /// 模型名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// 当前模型可命中的路由数量。
    /// </summary>
    public int RouteCount { get; set; }

    /// <summary>
    /// 模型是否支持 OpenAI 协议。
    /// </summary>
    public bool SupportsOpenAi { get; set; }

    /// <summary>
    /// 模型是否支持 Anthropic 协议。
    /// </summary>
    public bool SupportsAnthropic { get; set; }

    /// <summary>
    /// 模型是否存在支持 OpenAI Responses 原生接口的路由。
    /// </summary>
    public bool SupportsResponses { get; set; }

    /// <summary>
    /// 当前环境下是否允许通过 OpenAI 协议调用。
    /// </summary>
    public bool CanUseOpenAi { get; set; }

    /// <summary>
    /// 当前环境下是否允许通过 Anthropic 协议调用。
    /// </summary>
    public bool CanUseAnthropic { get; set; }
}
