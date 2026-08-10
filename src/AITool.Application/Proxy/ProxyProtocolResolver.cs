namespace AITool.Application.Proxy;

/// <summary>
/// 统一处理站点协议能力推导和客户端协议到上游协议的选择。
/// </summary>
public static class ProxyProtocolResolver
{
    /// <summary>
    /// OpenAI Chat Completions 协议名称。
    /// </summary>
    public const string OpenAi = "OpenAI";

    /// <summary>
    /// Anthropic Messages 协议名称。
    /// </summary>
    public const string Anthropic = "Anthropic";

    /// <summary>
    /// OpenAI Responses 协议名称。
    /// </summary>
    public const string Responses = "Responses";

    /// <summary>
    /// 根据站点能力推导其原生上游协议。
    /// </summary>
    public static string ResolveSiteProtocolType(
        bool supportsOpenAi,
        bool supportsAnthropic,
        bool supportsResponses = false,
        string? legacyProtocolType = null)
    {
        // 兼容旧数据：历史站点可能只保存 ProtocolType=Responses，尚未回填 SupportsResponses。
        if (supportsResponses
            || string.Equals(legacyProtocolType, Responses, StringComparison.OrdinalIgnoreCase)
            || (!supportsOpenAi && !supportsAnthropic))
        {
            return Responses;
        }

        return supportsOpenAi || !supportsAnthropic ? OpenAi : Anthropic;
    }

    /// <summary>
    /// 判断站点是否具备原生 Responses 能力。
    /// </summary>
    public static bool SupportsResponses(
        bool supportsOpenAi,
        bool supportsAnthropic,
        bool supportsResponses,
        string? legacyProtocolType = null)
    {
        return string.Equals(
            ResolveSiteProtocolType(
                supportsOpenAi,
                supportsAnthropic,
                supportsResponses,
                legacyProtocolType),
            Responses,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断站点是否能够直接处理指定协议。
    /// </summary>
    public static bool SupportsProtocol(
        string? protocolType,
        bool supportsOpenAi,
        bool supportsAnthropic,
        bool supportsResponses,
        string? legacyProtocolType = null)
    {
        if (string.Equals(protocolType, Responses, StringComparison.OrdinalIgnoreCase))
        {
            return SupportsResponses(
                supportsOpenAi,
                supportsAnthropic,
                supportsResponses,
                legacyProtocolType);
        }

        if (string.Equals(protocolType, Anthropic, StringComparison.OrdinalIgnoreCase))
        {
            return supportsAnthropic;
        }

        // 保持历史行为：未知协议按 OpenAI 能力处理，调用方传入的标准协议仍会走上面的明确分支。
        return supportsOpenAi;
    }

    /// <summary>
    /// 根据客户端协议选择上游原生协议；不匹配时由调用方执行兼容转换。
    /// </summary>
    public static string ResolveProtocolForClient(
        string clientProtocol,
        string? protocolType,
        bool supportsOpenAi,
        bool supportsAnthropic,
        bool supportsResponses,
        string? legacyProtocolType = null)
    {
        if (SupportsProtocol(
                clientProtocol,
                supportsOpenAi,
                supportsAnthropic,
                supportsResponses,
                legacyProtocolType))
        {
            return NormalizeProtocol(clientProtocol);
        }

        if (string.Equals(clientProtocol, Responses, StringComparison.OrdinalIgnoreCase))
        {
            if (supportsOpenAi)
            {
                return OpenAi;
            }

            if (supportsAnthropic)
            {
                return Anthropic;
            }

            return Responses;
        }

        if ((string.Equals(clientProtocol, OpenAi, StringComparison.OrdinalIgnoreCase)
                || string.Equals(clientProtocol, Anthropic, StringComparison.OrdinalIgnoreCase))
            && SupportsProtocol(
                Responses,
                supportsOpenAi,
                supportsAnthropic,
                supportsResponses,
                legacyProtocolType))
        {
            return Responses;
        }

        return string.Equals(clientProtocol, Anthropic, StringComparison.OrdinalIgnoreCase)
            ? OpenAi
            : Anthropic;
    }

    /// <summary>
    /// 将协议名称归一化为系统内部使用的标准名称。
    /// </summary>
    public static string NormalizeProtocol(string protocolType)
    {
        if (string.Equals(protocolType, Responses, StringComparison.OrdinalIgnoreCase))
        {
            return Responses;
        }

        if (string.Equals(protocolType, Anthropic, StringComparison.OrdinalIgnoreCase))
        {
            return Anthropic;
        }

        return OpenAi;
    }
}
