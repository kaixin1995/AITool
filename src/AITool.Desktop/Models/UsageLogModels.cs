using System.Globalization;

namespace AITool.Desktop.Models;

public sealed class UsageLogFilters
{
    public List<UsageLogFilterItem> Sites { get; set; } = new();
    public List<UsageLogFilterItem> AccessKeys { get; set; } = new();
}

public sealed class UsageLogFilterItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class UsageLogOption
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class UsageLogListResponse
{
    public List<UsageLogItem> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
}

public sealed class UsageLogSummary
{
    public int TotalRequests { get; set; }
    public int FailedRequests { get; set; }
    public double SuccessRate { get; set; }
    public long TotalTokens { get; set; }
    public int MaxDurationMs { get; set; }

    public string SuccessRateText => $"{(SuccessRate <= 1 ? SuccessRate * 100 : SuccessRate):0.0}%";
    public string TotalTokensText => AnalyticsText.Compact(TotalTokens);
}

public sealed class UsageLogItem
{
    public string Id { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string AttemptedModel { get; set; } = string.Empty;
    public string SiteModelName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string AccessKeyName { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public int AttemptIndex { get; set; }
    public bool IsFinalResult { get; set; }
    public bool FallbackTriggered { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public long InputTokens { get; set; }
    public long CachedTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public bool IsStreaming { get; set; }
    public bool IsStreamInterrupted { get; set; }
    public int FirstTokenLatencyMs { get; set; }
    public int StreamDurationMs { get; set; }
    public int TotalDurationMs { get; set; }
    public string ForwardingMode { get; set; } = string.Empty;
    public string ReasoningEffort { get; set; } = string.Empty;
    public string RequestedAt { get; set; } = string.Empty;
    public string RequestedAtText
    {
        get
        {
            if (!DateTimeOffset.TryParse(RequestedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            {
                return string.IsNullOrWhiteSpace(RequestedAt) ? "-" : RequestedAt;
            }

            return value.ToLocalTime().ToString("yyyy/M/d HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }
    public string InputTokensText => InputTokens.ToString("N0");
    public string CachedTokensText => CachedTokens.ToString("N0");
    public string OutputTokensText => OutputTokens.ToString("N0");
    public string TotalTokensText => TotalTokens.ToString("N0");

    // 详情和列表统一使用实际尝试模型，兼容后端返回空模型的旧日志。
    public string ModelText => FirstNonEmpty(AttemptedModel, RequestModel, "-");
    public string SiteNameText => FirstNonEmpty(SiteName, "-");
    public string SiteModelNameText => FirstNonEmpty(SiteModelName, "-");
    public string AccessKeyNameText => FirstNonEmpty(AccessKeyName, "-");
    public string ForwardingModeText => FirstNonEmpty(ForwardingMode, "-");
    public string ReasoningEffortText => FirstNonEmpty(ReasoningEffort, "-");
    public string ErrorText => FirstNonEmpty(ErrorMessage, "-");
    public string AttemptIndexText => AttemptIndex >= 0 ? $"第 {AttemptIndex + 1} 次尝试" : "尝试序号 -";
    public string TokensText => $"输入 {InputTokensText} / 缓存 {CachedTokensText} / 输出 {OutputTokensText} / 总计 {TotalTokensText}";
    public string StreamDetailsText => $"{StreamingText} / 中断 {(IsStreamInterrupted ? "是" : "否")} / 流式耗时 {(StreamDurationMs > 0 ? $"{StreamDurationMs} ms" : "-")}";
    public string ResultDetailsText => $"最终结果 {(IsFinalResult ? "是" : "否")} / 回退 {(FallbackTriggered ? "是" : "否")}";

    public bool IsSuccessfulStatus => Status is "success" or "ok";
    public bool IsFailedStatus => !IsSuccessfulStatus;
    public string SourceLabel => Source?.Trim().ToLowerInvariant() switch
    {
        "proxy" => "代理",
        "chat" => "对话测试",
        "claude-code" => "Claude Code",
        "codex" => "Codex",
        "open-code" => "Open Code",
        "zcode" => "ZCode",
        "deepseek-harness" => "DeepSeek Harness",
        "detection-manual" => "手动检测",
        "detection-task" => "定时检测",
        _ => SourceText
    };
    public string StatusText => FallbackTriggered && IsSuccessfulStatus ? "回退后成功" : IsStreamInterrupted && IsFailedStatus ? "流中断" : IsSuccessfulStatus ? "成功" : "失败";
    public string DurationText => TotalDurationMs > 0 ? $"{TotalDurationMs} ms" : "-";
    public string FirstTokenText => IsStreaming && FirstTokenLatencyMs > 0 ? $"{FirstTokenLatencyMs} ms" : "-";
    public string StreamingText => IsStreaming ? "流" : "非流";
    public string SourceText => string.IsNullOrWhiteSpace(Source) ? "-" : Source;

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "-";
}

public sealed class UsageLogRequestDetail
{
    public string RequestId { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string RouteEntry { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public string ForwardingMode { get; set; } = string.Empty;
    public string ReasoningEffort { get; set; } = string.Empty;
    public List<UsageLogItem> Attempts { get; set; } = new();

    public string RequestIdText => FirstNonEmpty(RequestId, "-");
    public string RequestModelText => FirstNonEmpty(RequestModel, "-");
    public string RouteEntryText => FirstNonEmpty(RouteEntry, "-");
    public string ProtocolTypeText => FirstNonEmpty(ProtocolType, "-");
    public string ForwardingModeText => FirstNonEmpty(ForwardingMode, "-");
    public string ReasoningEffortText => FirstNonEmpty(ReasoningEffort, "-");
    public string AccessKeyNameText => Attempts
        .Select(attempt => attempt.AccessKeyName)
        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "-";

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "-";
}
