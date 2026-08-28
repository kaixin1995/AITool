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
    public const string CodexVsCode = "CodexVsCode";
    public const string ZCode = "ZCode";
    public const string Antigravity = "Antigravity";
    public const string Kimi = "Kimi";
    public const string Custom = "Custom";

    /// <summary>
    /// 标准化预设名称（大小写不敏感）。
    /// 内置预设归一化为规范 PascalCase 名称；
    /// 空、空白或 "None" 归一化为 "None"；
    /// 自定义 HeaderProfile 键原样保留首尾修剪后的字符串。
    /// </summary>
    public static string Normalize(string? emulation)
    {
        if (string.IsNullOrWhiteSpace(emulation))
        {
            return None;
        }

        var trimmed = emulation.Trim();
        if (string.Equals(trimmed, None, StringComparison.OrdinalIgnoreCase))
        {
            return None;
        }

        var cleaned = trimmed.Replace("-", string.Empty).Replace("_", string.Empty);
        if (string.Equals(cleaned, OpenCode, StringComparison.OrdinalIgnoreCase)) return OpenCode;
        if (string.Equals(cleaned, ClaudeCode, StringComparison.OrdinalIgnoreCase) || string.Equals(cleaned, "claude", StringComparison.OrdinalIgnoreCase)) return ClaudeCode;
        if (string.Equals(cleaned, CodexCli, StringComparison.OrdinalIgnoreCase) || string.Equals(cleaned, "codex", StringComparison.OrdinalIgnoreCase) || string.Equals(cleaned, "codexdesktop", StringComparison.OrdinalIgnoreCase)) return CodexCli;
        if (string.Equals(cleaned, CodexVsCode, StringComparison.OrdinalIgnoreCase) || string.Equals(cleaned, "codexvscode", StringComparison.OrdinalIgnoreCase)) return CodexVsCode;
        if (string.Equals(cleaned, ZCode, StringComparison.OrdinalIgnoreCase)) return ZCode;
        if (string.Equals(cleaned, Antigravity, StringComparison.OrdinalIgnoreCase) || string.Equals(cleaned, "gemini", StringComparison.OrdinalIgnoreCase) || string.Equals(cleaned, "geminicli", StringComparison.OrdinalIgnoreCase)) return Antigravity;
        if (string.Equals(cleaned, Kimi, StringComparison.OrdinalIgnoreCase) || string.Equals(cleaned, "kimicli", StringComparison.OrdinalIgnoreCase) || string.Equals(cleaned, "kimicode", StringComparison.OrdinalIgnoreCase)) return Kimi;
        if (string.Equals(cleaned, Custom, StringComparison.OrdinalIgnoreCase)) return Custom;

        return trimmed;
    }
}
