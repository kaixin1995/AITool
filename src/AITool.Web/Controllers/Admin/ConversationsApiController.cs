using AITool.Application.Operations;
using AITool.Infrastructure.Conversations;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 对话记录查询接口。
/// </summary>
[ApiController]
[Route("api/admin/conversations")]
public sealed class ConversationsApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ConversationExtractionService _conversationExtractionService;
    private readonly ISystemRuntimeSettingsService _systemRuntimeSettingsService;

    public ConversationsApiController(
        AppDbContext dbContext,
        ConversationExtractionService conversationExtractionService,
        ISystemRuntimeSettingsService systemRuntimeSettingsService)
    {
        _dbContext = dbContext;
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

        var (rangeStart, rangeEnd) = ResolveTimeRange(rangeType, startTime, endTime);

        var query = _dbContext.ConversationTurnLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(sourceTool))
        {
            query = query.Where(x => x.SourceTool == sourceTool);
        }

        if (!string.IsNullOrWhiteSpace(requestModel))
        {
            query = query.Where(x => x.RequestModel == requestModel);
        }

        if (!string.IsNullOrWhiteSpace(sessionKeyword))
        {
            query = query.Where(x => x.SessionId.Contains(sessionKeyword));
        }

        // SQLite 对 DateTimeOffset 的 Where 比较无法翻译为 SQL，先拉到内存再过滤时间范围。
        var items = await query.ToListAsync(cancellationToken);
        items = items.Where(x => x.CreatedAt >= rangeStart && x.CreatedAt < rangeEnd).ToList();

        var sessions = items
            .GroupBy(x => x.ConversationGroupKey)
            .Select(group =>
            {
                var latest = group.OrderByDescending(x => x.CreatedAt).First();
                var preview = group
                    .Select(x => _conversationExtractionService.NormalizeConversationText(GzipTextCompression.Decompress(x.UserInputText)))
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
                var totalTokens = group.Sum(x => x.InputTokens + x.CachedTokens + x.OutputTokens);
                var customTitle = group
                    .Select(x => x.ConversationTitle)
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
                var defaultTitle = ResolveSessionTitle(latest.SourceTool, string.Empty, preview, latest.SessionId);
                var title = ResolveSessionTitle(latest.SourceTool, customTitle, preview, latest.SessionId);
                return new
                {
                    GroupKey = group.Key,
                    SourceTool = latest.SourceTool,
                    SourceToolText = GetSourceToolText(latest.SourceTool),
                    SessionIdShort = string.IsNullOrWhiteSpace(latest.SessionId) ? "无会话" : latest.SessionId[..Math.Min(8, latest.SessionId.Length)],
                    LastActivityAt = latest.CreatedAt,
                    LastActivityAtText = latest.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    TurnCount = group.Count(),
                    TotalTokens = totalTokens,
                    TotalTokensText = FormatTokenCount(totalTokens),
                    Preview = preview.Length > 60 ? preview[..60] : preview,
                    Title = title,
                    DefaultTitle = defaultTitle,
                    IsCustomTitle = !string.IsNullOrWhiteSpace(customTitle)
                };
            })
            .OrderByDescending(x => x.LastActivityAt)
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

        var logs = await _dbContext.ConversationTurnLogs
            .Where(x => x.ConversationGroupKey == groupKey)
            .ToListAsync(cancellationToken);

        if (logs.Count == 0)
        {
            return NotFound(new { message = "会话不存在或已删除" });
        }

        _dbContext.ConversationTurnLogs.RemoveRange(logs);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { deletedCount = logs.Count });
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

        var logs = await _dbContext.ConversationTurnLogs
            .Where(x => x.ConversationGroupKey == request.GroupKey)
            .ToListAsync(cancellationToken);
        if (logs.Count == 0)
        {
            return NotFound(new { message = "会话不存在" });
        }

        var normalizedTitle = (request.Title ?? string.Empty).Trim();
        if (normalizedTitle.Length > 200)
        {
            normalizedTitle = normalizedTitle[..200];
        }

        foreach (var log in logs)
        {
            log.ConversationTitle = normalizedTitle;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
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

        var (rangeStart, rangeEnd) = ResolveTimeRange(rangeType, startTime, endTime);
        var logs = await _dbContext.ConversationTurnLogs
            .AsNoTracking()
            .Where(x => x.ConversationGroupKey == groupKey)
            .ToListAsync(cancellationToken);

        logs = logs
            .Where(x => x.CreatedAt >= rangeStart && x.CreatedAt < rangeEnd)
            .ToList();

        var items = logs
            .OrderBy(x => x.CreatedAt)
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

        return Ok(new { items });
    }

    /// <summary>
    /// 根据 rangeType 解析出实际的起止时间。
    /// day = 当天，week = 本周一到今天，month = 本月1号到今天，custom = 使用传入的 startTime/endTime。
    /// </summary>
    private static (DateTimeOffset Start, DateTimeOffset End) ResolveTimeRange(string? rangeType, DateTimeOffset? startTime, DateTimeOffset? endTime)
    {
        var now = DateTimeOffset.Now;
        var normalized = string.IsNullOrWhiteSpace(rangeType) ? "day" : rangeType.Trim().ToLowerInvariant();

        if (normalized == "custom")
        {
            var customStart = startTime ?? StartOfDay(now);
            var customEnd = endTime.HasValue ? endTime.Value.AddMinutes(1) : now;
            if (customEnd <= customStart)
            {
                customEnd = customStart.AddMinutes(1);
            }

            return (customStart, customEnd);
        }

        var endOfToday = StartOfDay(now).AddDays(1);

        return normalized switch
        {
            "week" => (StartOfDay(now).AddDays(-((7 + (int)now.DayOfWeek - (int)DayOfWeek.Monday) % 7)), endOfToday),
            "month" => (new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset), endOfToday),
            "all" => (DateTimeOffset.MinValue, now),
            _ => (StartOfDay(now), endOfToday) // day 为默认
        };
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
