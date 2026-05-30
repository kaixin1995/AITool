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

    public ConversationsApiController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 查询会话列表。
    /// 支持 rangeType（day/week/month/custom）时间范围过滤，默认当天。
    /// </summary>
    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions(
        [FromQuery] string? sourceTool,
        [FromQuery] string? sessionKeyword,
        [FromQuery] string? contentKeyword,
        [FromQuery] string? rangeType,
        [FromQuery] DateTimeOffset? startTime,
        [FromQuery] DateTimeOffset? endTime,
        CancellationToken cancellationToken)
    {
        var (rangeStart, rangeEnd) = ResolveTimeRange(rangeType, startTime, endTime);

        var query = _dbContext.ConversationTurnLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(sourceTool))
        {
            query = query.Where(x => x.SourceTool == sourceTool);
        }

        if (!string.IsNullOrWhiteSpace(sessionKeyword))
        {
            query = query.Where(x => x.SessionId.Contains(sessionKeyword));
        }

        // 内容关键字搜索不在数据库层做，因为 UserInputText 是压缩存储的，SQLite 无法直接匹配。
        // 拉到内存后统一解压再过滤。

        // SQLite 对 DateTimeOffset 的 Where 比较无法翻译为 SQL，先拉到内存再过滤时间范围。
        var items = await query.ToListAsync(cancellationToken);
        items = items.Where(x => x.CreatedAt >= rangeStart && x.CreatedAt < rangeEnd).ToList();

        // 内容搜索在内存中做，因为 UserInputText 可能是压缩存储的。
        if (!string.IsNullOrWhiteSpace(contentKeyword))
        {
            items = items.Where(x =>
                GzipTextCompression.Decompress(x.UserInputText).Contains(contentKeyword, StringComparison.OrdinalIgnoreCase)
                || x.AssistantOutputPlainText.Contains(contentKeyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var sessions = items
            .GroupBy(x => x.ConversationGroupKey)
            .Select(group =>
            {
                var latest = group.OrderByDescending(x => x.CreatedAt).First();
                var preview = group.Select(x => GzipTextCompression.Decompress(x.UserInputText)).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
                var totalTokens = group.Sum(x => x.InputTokens + x.CachedTokens + x.OutputTokens);
                return new
                {
                    GroupKey = group.Key,
                    SourceTool = latest.SourceTool,
                    SessionIdShort = string.IsNullOrWhiteSpace(latest.SessionId) ? "无会话" : latest.SessionId[..Math.Min(8, latest.SessionId.Length)],
                    LastActivityAt = latest.CreatedAt,
                    LastActivityAtText = latest.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    TurnCount = group.Count(),
                    TotalTokens = totalTokens,
                    Preview = preview.Length > 60 ? preview[..60] : preview
                };
            })
            .OrderByDescending(x => x.LastActivityAt)
            .ToList();

        return Ok(new { items = sessions });
    }

    /// <summary>
    /// 查询某个会话下的对话记录。
    /// </summary>
    [HttpGet("turns")]
    public async Task<IActionResult> GetTurns([FromQuery] string groupKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(groupKey))
        {
            return BadRequest(new { message = "groupKey 不能为空" });
        }

        var logs = await _dbContext.ConversationTurnLogs
            .AsNoTracking()
            .Where(x => x.ConversationGroupKey == groupKey)
            .ToListAsync(cancellationToken);

        var items = logs
            .OrderBy(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.CreatedAt,
                CreatedAtText = x.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                x.RequestModel,
                UserInputText = GzipTextCompression.Decompress(x.UserInputText),
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
}
