using AITool.Application.Conversations;
using AITool.Application.Operations;
using AITool.Infrastructure.Conversations;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 对话记录查询接口。
/// </summary>
[ApiController]
[Route("api/admin/conversations")]
public sealed class ConversationsApiController : ControllerBase
{
    private readonly IConversationLogStore _conversationLogStore;
    private readonly ConversationExtractionService _conversationExtractionService;
    private readonly ISystemRuntimeSettingsService _systemRuntimeSettingsService;

    public ConversationsApiController(
        IConversationLogStore conversationLogStore,
        ConversationExtractionService conversationExtractionService,
        ISystemRuntimeSettingsService systemRuntimeSettingsService)
    {
        _conversationLogStore = conversationLogStore;
        _conversationExtractionService = conversationExtractionService;
        _systemRuntimeSettingsService = systemRuntimeSettingsService;
    }

    /// <summary>
    /// 查询会话列表。
    /// 支持 rangeType（day/week/month/custom）时间范围过滤，默认当天。
    /// </summary>
    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions(
        [FromQuery] string? sourceTool,
        [FromQuery] string? requestModel,
        [FromQuery] string? sessionKeyword,
        [FromQuery] string? rangeType,
        [FromQuery] DateTimeOffset? startTime,
        [FromQuery] DateTimeOffset? endTime,
        CancellationToken cancellationToken)
    {
        if (!await IsConversationLogEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        if (!TryResolveTimeRange(rangeType, startTime, endTime, out var range, out var errorMessage))
        {
            return BadRequest(new { message = errorMessage });
        }

        var summaries = await _conversationLogStore.QuerySessionSummariesAsync(new ConversationLogQuery
        {
            StartTime = range.Start,
            EndTime = range.End,
            SourceTool = sourceTool ?? string.Empty,
            RequestModel = requestModel ?? string.Empty,
            SessionKeyword = sessionKeyword ?? string.Empty
        }, cancellationToken);

        var sessions = summaries
            .Select(summary =>
            {
                // 每个会话只解压一次压缩原文，取标题预览（替代历史实现里对分组内每条都解压再 FirstOrDefault）。
                var preview = _conversationExtractionService.NormalizeConversationText(
                    GzipTextCompression.Decompress(summary.FirstUserInputTextCompressed));
                var defaultTitle = ResolveSessionTitle(summary.SourceTool, string.Empty, preview, summary.SessionId);
                var title = ResolveSessionTitle(summary.SourceTool, summary.ConversationTitle, preview, summary.SessionId);
                return new
                {
                    GroupKey = summary.GroupKey,
                    SourceTool = summary.SourceTool,
                    SourceToolText = GetSourceToolText(summary.SourceTool),
                    SessionIdShort = string.IsNullOrWhiteSpace(summary.SessionId) ? "无会话" : summary.SessionId[..Math.Min(8, summary.SessionId.Length)],
                    LastActivityAt = summary.LastActivityAt,
                    LastActivityAtText = summary.LastActivityAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    TurnCount = summary.TurnCount,
                    TotalTokens = summary.TotalTokens,
                    TotalTokensText = FormatTokenCount(summary.TotalTokens),
                    Preview = preview.Length > 60 ? preview[..60] : preview,
                    Title = title,
                    DefaultTitle = defaultTitle,
                    IsCustomTitle = !string.IsNullOrWhiteSpace(summary.ConversationTitle)
                };
            })
            .ToList();

        return Ok(new { items = sessions });
    }

    /// <summary>
    /// 删除某个会话下的全部对话记录。
    /// </summary>
    [HttpDelete("sessions")]
    public async Task<IActionResult> DeleteSession([FromQuery] string groupKey, CancellationToken cancellationToken)
    {
        if (!await IsConversationLogEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(groupKey))
        {
            return BadRequest(new { message = "groupKey 不能为空" });
        }

        var deletedCount = await _conversationLogStore.DeleteSessionAsync(groupKey, cancellationToken);
        if (deletedCount == 0)
        {
            return NotFound(new { message = "会话不存在或已删除" });
        }

        return Ok(new { deletedCount });
    }

    /// <summary>
    /// 更新会话标题；传空值时回退为默认标题。
    /// </summary>
    [HttpPost("sessions/title")]
    public async Task<IActionResult> UpdateSessionTitle([FromBody] UpdateConversationTitleRequest request, CancellationToken cancellationToken)
    {
        if (!await IsConversationLogEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.GroupKey))
        {
            return BadRequest(new { message = "groupKey 不能为空" });
        }

        var normalizedTitle = (request.Title ?? string.Empty).Trim();
        if (normalizedTitle.Length > 200)
        {
            normalizedTitle = normalizedTitle[..200];
        }

        var updatedCount = await _conversationLogStore.UpdateSessionTitleAsync(request.GroupKey, normalizedTitle, cancellationToken);
        if (updatedCount == 0)
        {
            return NotFound(new { message = "会话不存在" });
        }

        return Ok(new { title = normalizedTitle });
    }

    /// <summary>
    /// 查询某个会话下的对话记录。
    /// </summary>
    [HttpGet("turns")]
    public async Task<IActionResult> GetTurns(
        [FromQuery] string groupKey,
        [FromQuery] string? rangeType,
        [FromQuery] DateTimeOffset? startTime,
        [FromQuery] DateTimeOffset? endTime,
        CancellationToken cancellationToken)
    {
        if (!await IsConversationLogEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(groupKey))
        {
            return BadRequest(new { message = "groupKey 不能为空" });
        }

        if (!TryResolveTimeRange(rangeType, startTime, endTime, out var range, out var errorMessage))
        {
            return BadRequest(new { message = errorMessage });
        }

        var logs = await _conversationLogStore.QueryAsync(new ConversationLogQuery
        {
            StartTime = range.Start,
            EndTime = range.End,
            GroupKey = groupKey
        }, cancellationToken);

        // QueryAsync 从最新分片倒序读取、已带 MaxQueryTurns 上限；这里再按展示顺序（升序）收紧单会话上限，
        // 超出部分提示用户缩小时间范围，避免前端一次性渲染海量轮次导致卡死。
        var orderedLogs = logs.OrderBy(x => x.CreatedAt).ToList();
        var truncated = orderedLogs.Count > ConversationLogStoragePolicy.MaxTurnsPerSession;
        if (truncated)
        {
            orderedLogs = orderedLogs
                .Skip(orderedLogs.Count - ConversationLogStoragePolicy.MaxTurnsPerSession)
                .ToList();
        }

        var items = orderedLogs
            .Select(x => new
            {
                x.Id,
                x.CreatedAt,
                x.UserCreatedAt,
                CreatedAtText = x.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                UserCreatedAtText = (x.UserCreatedAt ?? x.CreatedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                x.RequestModel,
                UserInputText = _conversationExtractionService.NormalizeConversationText(GzipTextCompression.Decompress(x.UserInputText)),
                AssistantOutputMarkdown = GzipTextCompression.Decompress(x.AssistantOutputMarkdown),
                x.InputTokens,
                x.CachedTokens,
                x.OutputTokens
            })
            .ToList();

        return Ok(new { items, truncated });
    }

    /// <summary>
    /// 根据 rangeType 解析出实际的起止时间。
    /// day = 当天，week = 本周一到今天，month = 本月1号到今天，custom = 使用传入的 startTime/endTime。
    /// </summary>
    private static bool TryResolveTimeRange(
        string? rangeType,
        DateTimeOffset? startTime,
        DateTimeOffset? endTime,
        out (DateTimeOffset Start, DateTimeOffset End) range,
        out string errorMessage)
    {
        var now = DateTimeOffset.Now;
        var normalized = string.IsNullOrWhiteSpace(rangeType) ? "day" : rangeType.Trim().ToLowerInvariant();
        errorMessage = string.Empty;

        if (normalized == "all")
        {
            range = default;
            errorMessage = $"对话记录单次最多只允许查询 {ConversationLogStoragePolicy.MaxQueryDays} 天，请改用指定范围分段查看。";
            return false;
        }

        if (normalized == "custom")
        {
            var customStart = startTime ?? StartOfDay(now);
            var customEnd = endTime.HasValue ? endTime.Value.AddMinutes(1) : now;
            if (customEnd <= customStart)
            {
                customEnd = customStart.AddMinutes(1);
            }

            if (customEnd - customStart > TimeSpan.FromDays(ConversationLogStoragePolicy.MaxQueryDays))
            {
                range = default;
                errorMessage = $"对话记录单次最多只允许查询 {ConversationLogStoragePolicy.MaxQueryDays} 天。";
                return false;
            }

            range = (customStart, customEnd);
            return true;
        }

        var endOfToday = StartOfDay(now).AddDays(1);
        range = normalized switch
        {
            "week" => (StartOfDay(now).AddDays(-((7 + (int)now.DayOfWeek - (int)DayOfWeek.Monday) % 7)), endOfToday),
            "month" => (new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset), endOfToday),
            _ => (StartOfDay(now), endOfToday)
        };

        if (range.End - range.Start > TimeSpan.FromDays(ConversationLogStoragePolicy.MaxQueryDays))
        {
            range = default;
            errorMessage = $"对话记录单次最多只允许查询 {ConversationLogStoragePolicy.MaxQueryDays} 天。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 取某个时间点的当天零点。
    /// </summary>
    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, value.Offset);
    }

    /// <summary>
    /// 判断对话记录功能是否启用。
    /// </summary>
    private async Task<bool> IsConversationLogEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await _systemRuntimeSettingsService.GetOrCreateAsync(cancellationToken);
        return settings.ConversationLogEnabled;
    }

    /// <summary>
    /// 根据来源、标题覆盖和内容推导会话列表标题。
    /// </summary>
    private static string ResolveSessionTitle(string sourceTool, string customTitle, string preview, string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(customTitle))
        {
            return customTitle;
        }

        if (string.Equals(sourceTool, "chat", StringComparison.OrdinalIgnoreCase))
        {
            return "对话测试";
        }

        if (string.Equals(sourceTool, "proxy", StringComparison.OrdinalIgnoreCase))
        {
            return "代理";
        }

        if (string.Equals(sourceTool, "codex", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceTool, "claude-code", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceTool, "open-code", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(preview))
            {
                return preview.Length > 40 ? preview[..40] : preview;
            }
        }

        if (!string.IsNullOrWhiteSpace(preview))
        {
            return preview.Length > 40 ? preview[..40] : preview;
        }

        return string.IsNullOrWhiteSpace(sessionId) ? "未命名会话" : sessionId[..Math.Min(8, sessionId.Length)];
    }

    /// <summary>
    /// 将来源标识转为界面展示文案。
    /// </summary>
    private static string GetSourceToolText(string sourceTool)
    {
        return sourceTool switch
        {
            "chat" => "对话测试",
            "proxy" => "代理",
            "claude-code" => "Claude Code",
            "codex" => "Codex",
            "open-code" => "OpenCode",
            _ => sourceTool
        };
    }

    /// <summary>
    /// 将大 token 数量格式化成更紧凑的缩写文本。
    /// </summary>
    private static string FormatTokenCount(int value)
    {
        if (value >= 1_000_000_000)
        {
            return $"{value / 1_000_000_000d:0.#}G";
        }

        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}M";
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000d:0.#}K";
        }

        return value.ToString();
    }
}

/// <summary>
/// 会话标题更新请求。
/// </summary>
public sealed class UpdateConversationTitleRequest
{
    /// <summary>
    /// 会话分组键。
    /// </summary>
    public string GroupKey { get; set; } = string.Empty;

    /// <summary>
    /// 自定义标题；传空值时清空自定义标题并回退为默认标题。
    /// </summary>
    public string? Title { get; set; }
}
