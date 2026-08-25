namespace AITool.Domain.Sites;

/// <summary>
/// 客户端特征模拟预设常量与定义。
/// 用于伪装官方 CLI / IDE 扩展特征，以接入免密/特权通道并规避风控封禁。
/// </summary>
public static class ClientEmulationConstants
{
    public const string None = "None";
    public const string OpenCode = "OpenCode";
    public const string ClaudeCode = "ClaudeCode";
    public const string CodexCli = "CodexCli";
    public const string Antigravity = "Antigravity";
    public const string GeminiCli = "GeminiCli";
    public const string Custom = "Custom";

    /// <summary>
    /// 标准化预设名称（大小写不敏感），未知值回退为 None。
    /// </summary>
    public static string Normalize(string? emulation)
    {
        if (string.IsNullOrWhiteSpace(emulation))
        {
            return None;
        }

        var cleaned = emulation.Trim().Replace("-", string.Empty).Replace("_", string.Empty);
        if (string.Equals(cleaned, OpenCode, StringComparison.OrdinalIgnoreCase)) return OpenCode;
        if (string.Equals(cleaned, ClaudeCode, StringComparison.OrdinalIgnoreCase) || string.Equals(cleaned, "claude", StringComparison.OrdinalIgnoreCase)) return ClaudeCode;
        if (string.Equals(cleaned, CodexCli, StringComparison.OrdinalIgnoreCase) || string.Equals(cleaned, "codex", StringComparison.OrdinalIgnoreCase)) return CodexCli;
        if (string.Equals(cleaned, Antigravity, StringComparison.OrdinalIgnoreCase)) return Antigravity;
        if (string.Equals(cleaned, GeminiCli, StringComparison.OrdinalIgnoreCase) || string.Equals(cleaned, "gemini", StringComparison.OrdinalIgnoreCase)) return GeminiCli;
        if (string.Equals(cleaned, Custom, StringComparison.OrdinalIgnoreCase)) return Custom;

        return None;
    }
}
