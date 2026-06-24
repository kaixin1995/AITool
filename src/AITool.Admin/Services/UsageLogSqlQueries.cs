using System.Data;
using System.Data.Common;
using System.Text;
using Dapper;

namespace AITool.Admin.Services;

/// <summary>
/// 使用 Dapper 手写 SQL 的代理使用日志查询辅助类。
/// <para>
/// 用于绕过 EF Core 的 Microsoft.Data.Sqlite provider 对 DateTimeOffset 的 WHERE/ORDER BY 翻译限制。
/// 时间过滤、排序、分页全部在 SQLite 引擎端完成（用 datetime() 函数解析 TEXT 列），
/// 避免全表加载到内存后客户端过滤。
/// </para>
/// </summary>
public static class UsageLogSqlQueries
{
    static UsageLogSqlQueries()
    {
        // 注册 Guid 的 TypeHandler：EF Core 的 SQLite provider 把 Guid 存为 TEXT（大写 hex），
        // Dapper 默认不识别，需要手动注册转换。
        SqlMapper.AddTypeHandler(new GuidTypeHandler());
        SqlMapper.RemoveTypeMap(typeof(Guid?));
        SqlMapper.AddTypeHandler(typeof(Guid?), new GuidTypeHandler());
    }
    /// <summary>
    /// 查询使用日志列表（分页 + 筛选全部在 DB 端）。
    /// </summary>
    public static async Task<(List<UsageLogRow> Items, int TotalCount)> QueryListAsync(
        DbConnection connection,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        Guid? siteId,
        Guid? accessKeyId,
        string? source,
        string? status,
        string? modelKeyword,
        int page,
        int pageSize)
    {
        // 动态构建 WHERE 子句（参数化防注入）。
        var where = BuildWhereClause(rangeStart, rangeEnd, siteId, accessKeyId, source, status, modelKeyword,
            out var parameters);

        // 1. 查总数（COUNT 下推到 DB）
        var countSql = $"SELECT COUNT(*) FROM ProxyUsageLogs {where}";
        var totalCount = await connection.ExecuteScalarAsync<int>(countSql, parameters);

        // 2. 查分页数据（ORDER BY + LIMIT/OFFSET 下推到 DB）
        // datetime() 函数把 TEXT 列解析为时间值，支持范围比较和排序。
        var offset = (page - 1) * pageSize;
        var listSql = $@"
SELECT Id, RequestId, AccessKeyId, ProtocolType, RequestModel, AttemptedModel,
       TargetSiteId, Status, Source, RetryCount, AttemptIndex, IsFinalResult,
       FallbackTriggered, InputTokens, CachedTokens, OutputTokens, TotalTokens,
       IsStreaming, IsStreamInterrupted, FirstTokenLatencyMs, StreamDurationMs,
       TotalDurationMs, RequestedAt
FROM ProxyUsageLogs {where}
ORDER BY julianday(RequestedAt) DESC
LIMIT @PageSize OFFSET @Offset";

        parameters.Add("PageSize", pageSize);
        parameters.Add("Offset", offset);

        var items = (await connection.QueryAsync<UsageLogRow>(listSql, parameters)).AsList();
        return (items, totalCount);
    }

    /// <summary>
    /// 查询使用日志汇总（聚合全部在 DB 端）。
    /// </summary>
    public static async Task<UsageLogSummaryRow> QuerySummaryAsync(
        DbConnection connection,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        Guid? siteId,
        Guid? accessKeyId,
        string? source,
        string? status,
        string? modelKeyword)
    {
        var where = BuildWhereClause(rangeStart, rangeEnd, siteId, accessKeyId, source, status, modelKeyword,
            out var parameters);

        var sql = $@"
SELECT
  COUNT(*) AS TotalRequests,
  SUM(CASE WHEN Status = 'fail' THEN 1 ELSE 0 END) AS FailedRequests,
  SUM(CASE WHEN Status = 'success' THEN 1 ELSE 0 END) AS SuccessRequests,
  COALESCE(SUM(TotalTokens), 0) AS TotalTokens,
  COALESCE(MAX(TotalDurationMs), 0) AS MaxDurationMs
FROM ProxyUsageLogs {where}";

        var row = await connection.QueryFirstOrDefaultAsync<UsageLogSummaryRow>(sql, parameters);
        return row ?? new UsageLogSummaryRow();
    }

    /// <summary>
    /// 构建参数化的 WHERE 子句。datetime() 函数解析 TEXT 列为时间值。
    /// </summary>
    private static string BuildWhereClause(
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        Guid? siteId,
        Guid? accessKeyId,
        string? source,
        string? status,
        string? modelKeyword,
        out DynamicParameters parameters)
    {
        parameters = new DynamicParameters();
        var conditions = new List<string>();

        // 时间范围：用 julianday() 把 TEXT 列解析为浮点儒略日用于比较。
        // julianday() 能解析 EF Core 存储的带时区偏移的 ISO 8601 格式（datetime() 不支持时区）。
        // 对 MinValue/MaxValue 边界跳过条件，避免 julianday 对极端日期返回异常值。
        if (rangeStart > DateTimeOffset.MinValue)
        {
            conditions.Add("julianday(RequestedAt) >= julianday(@RangeStart)");
            // 参数用 ISO 8601 格式，julianday() 能解析（含时区偏移）。
            parameters.Add("RangeStart", rangeStart.ToUniversalTime().ToString("O"));
        }
        if (rangeEnd < DateTimeOffset.MaxValue)
        {
            conditions.Add("julianday(RequestedAt) < julianday(@RangeEnd)");
            parameters.Add("RangeEnd", rangeEnd.ToUniversalTime().ToString("O"));
        }

        if (siteId.HasValue)
        {
            conditions.Add("TargetSiteId = @SiteId");
            parameters.Add("SiteId", siteId.Value);
        }
        if (accessKeyId.HasValue)
        {
            conditions.Add("AccessKeyId = @AccessKeyId");
            parameters.Add("AccessKeyId", accessKeyId.Value);
        }
        if (!string.IsNullOrWhiteSpace(source))
        {
            conditions.Add("Source = @Source");
            parameters.Add("Source", source);
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            conditions.Add("Status = @Status");
            parameters.Add("Status", status);
        }
        if (!string.IsNullOrWhiteSpace(modelKeyword))
        {
            // 对齐原 IsModelMatched：匹配 RequestModel 或 AttemptedModel（包含关键字）。
            conditions.Add("(RequestModel LIKE @ModelPattern OR AttemptedModel LIKE @ModelPattern)");
            parameters.Add("ModelPattern", $"%{EscapeLike(modelKeyword)}%");
        }

        if (conditions.Count == 0)
        {
            return string.Empty;
        }
        return "WHERE " + string.Join(" AND ", conditions);
    }

    /// <summary>
    /// 查询 ModelHealth 页面所需的近期使用日志（时间过滤下推到 DB）。
    /// 返回完整 ProxyUsageLog 实体（ModelHealth 的聚合逻辑需要全部字段）。
    /// </summary>
    public static async Task<List<UsageLogRow>> QueryRecentForModelHealthAsync(
        DbConnection connection, DateTimeOffset sinceCutoff)
    {
        var sql = @"
SELECT Id, RequestId, AccessKeyId, ProtocolType, RequestModel, AttemptedModel,
       TargetSiteId, Status, Source, RetryCount, AttemptIndex, IsFinalResult,
       FallbackTriggered, InputTokens, CachedTokens, OutputTokens, TotalTokens,
       IsStreaming, IsStreamInterrupted, FirstTokenLatencyMs, StreamDurationMs,
       TotalDurationMs, RequestedAt
FROM ProxyUsageLogs
WHERE julianday(RequestedAt) >= julianday(@SinceCutoff)";

        return (await connection.QueryAsync<UsageLogRow>(sql, new
        {
            SinceCutoff = sinceCutoff.ToUniversalTime().ToString("O")
        })).AsList();
    }

    /// <summary>转义 LIKE 特殊字符。</summary>
    private static string EscapeLike(string keyword) =>
        keyword.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>
    /// 查询 Analytics 看板所需的使用日志（时间过滤 + 非时间筛选全部下推到 DB）。
    /// <para>
    /// 对 rangeType=all，先查 MIN(RequestedAt) 确定实际起始时间，避免从公元元年扫描。
    /// 返回的行用于后续内存聚合（趋势/分布），但避免了全表加载。
    /// </para>
    /// </summary>
    public static async Task<(List<UsageLogRow> Logs, DateTimeOffset ActualStartTime)> QueryForAnalyticsAsync(
        DbConnection connection,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        string? protocolType,
        string? modelName,
        Guid? accessKeyId,
        Guid? siteId,
        bool isAllRange)
    {
        // 非时间筛选条件（与 EF Core 版本一致）。
        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(protocolType)
            && !string.Equals(protocolType, "all", StringComparison.OrdinalIgnoreCase))
        {
            conditions.Add("ProtocolType = @ProtocolType");
            parameters.Add("ProtocolType", protocolType);
        }
        if (!string.IsNullOrWhiteSpace(modelName)
            && !string.Equals(modelName, "all", StringComparison.OrdinalIgnoreCase))
        {
            conditions.Add("AttemptedModel = @ModelName");
            parameters.Add("ModelName", modelName);
        }
        if (accessKeyId.HasValue)
        {
            conditions.Add("AccessKeyId = @AccessKeyId");
            parameters.Add("AccessKeyId", accessKeyId.Value);
        }
        if (siteId.HasValue)
        {
            // 站点筛选按"命中过该站点的尝试"统计。
            conditions.Add("TargetSiteId = @SiteId");
            parameters.Add("SiteId", siteId.Value);
        }

        // 对 all 范围，先查实际最小时间确定起始边界。
        if (isAllRange)
        {
            // all 范围不加下界条件（startTime 初始是 MinValue，julianday(MinValue) 会异常），
            // 只靠上界 endTime 过滤。actualStartTime 由 MIN 查询确定，用于桶类型解析。
            var minWhere = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
            var minSql = $"SELECT MIN(RequestedAt) FROM ProxyUsageLogs {minWhere}";
            var minDateStr = await connection.QueryFirstOrDefaultAsync<string?>(minSql, parameters);
            if (!string.IsNullOrEmpty(minDateStr))
            {
                startTime = new DateTimeOffset(
                    DateTime.SpecifyKind(DateTime.Parse(minDateStr, null, System.Globalization.DateTimeStyles.RoundtripKind), DateTimeKind.Utc),
                    TimeSpan.Zero);
            }
            else
            {
                // 无数据，返回空。
                return (new List<UsageLogRow>(), startTime);
            }

            // all 范围：只加上界，不加下界（MIN 之前的边界已被 MIN 涵盖）。
            conditions.Add("julianday(RequestedAt) < julianday(@EndTime)");
            parameters.Add("EndTime", endTime.ToUniversalTime().ToString("O"));
        }
        else
        {
            // 普通范围：上下界都加。
            conditions.Add("julianday(RequestedAt) >= julianday(@StartTime)");
            conditions.Add("julianday(RequestedAt) < julianday(@EndTime)");
            parameters.Add("StartTime", startTime.ToUniversalTime().ToString("O"));
            parameters.Add("EndTime", endTime.ToUniversalTime().ToString("O"));
        }

        // conditions 至少含两条时间条件，不会为空。
        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        var sql = $@"
SELECT Id, RequestId, AccessKeyId, ProtocolType, RequestModel, AttemptedModel,
       TargetSiteId, Status, Source, RetryCount, AttemptIndex, IsFinalResult,
       FallbackTriggered, InputTokens, CachedTokens, OutputTokens, TotalTokens,
       IsStreaming, IsStreamInterrupted, FirstTokenLatencyMs, StreamDurationMs,
       TotalDurationMs, RequestedAt
FROM ProxyUsageLogs {where}";

        var items = (await connection.QueryAsync<UsageLogRow>(sql, parameters)).AsList();
        return (items, startTime);
    }

    /// <summary>
    /// 查询每 (TargetSiteId, RequestModel) 组的最新一条最终结果日志（用于 Detection 页面）。
    /// 只 SELECT 展示所需字段，避免加载 ErrorMessage 等大字段。
    /// </summary>
    public static async Task<List<DetectionLatestLogRow>> QueryLatestFinalLogsAsync(DbConnection connection)
    {
        // 用 ROW_NUMBER() 窗口函数取每组最新一条（julianday 排序支持带时区的 ISO 8601）。SQLite 3.25+ 支持窗口函数。
        var sql = @"
SELECT TargetSiteId, RequestModel, Status, RequestedAt, TotalDurationMs
FROM (
    SELECT TargetSiteId, RequestModel, Status, RequestedAt, TotalDurationMs,
           ROW_NUMBER() OVER (PARTITION BY TargetSiteId, RequestModel ORDER BY julianday(RequestedAt) DESC) AS rn
    FROM ProxyUsageLogs
    WHERE IsFinalResult = 1
) t
WHERE rn = 1";

        return (await connection.QueryAsync<DetectionLatestLogRow>(sql)).AsList();
    }
}

/// <summary>
/// Dapper 映射的使用日志行（字段类型对齐 SQLite 存储类型）。
/// bool 字段用 long 接收（SQLite 存 integer），DTO 映射时转 bool。
/// RequestedAt 用 string 接收（EF Core 存为带时区偏移的 ISO 8601 TEXT），手动解析避免格式问题。
/// </summary>
public sealed class UsageLogRow
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid AccessKeyId { get; set; }
    public string ProtocolType { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string AttemptedModel { get; set; } = string.Empty;
    public Guid TargetSiteId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = "proxy";
    public int RetryCount { get; set; }
    public int AttemptIndex { get; set; }
    public long IsFinalResult { get; set; }
    public long FallbackTriggered { get; set; }
    public int InputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public long IsStreaming { get; set; }
    public long IsStreamInterrupted { get; set; }
    public int FirstTokenLatencyMs { get; set; }
    public int StreamDurationMs { get; set; }
    public int TotalDurationMs { get; set; }
    public string RequestedAt { get; set; } = string.Empty;

    /// <summary>
    /// <summary>
    /// 解析 RequestedAt 字符串为 DateTime（UTC）。
    /// 用 SpecifyKind 确保 Kind=Utc，避免 new DateTimeOffset(dateTime, Zero) 偏移不匹配。
    /// 解析失败时回退为 DateTime.MinValue，避免脏数据导致整页 500。
    /// </summary>
    public DateTime RequestedAtDateTime
    {
        get
        {
            try
            {
                return DateTime.SpecifyKind(
                    DateTime.Parse(RequestedAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
                    DateTimeKind.Utc);
            }
            catch
            {
                return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
            }
        }
    }
}

/// <summary>
/// Dapper 映射的使用日志汇总行。
/// </summary>
public sealed class UsageLogSummaryRow
{
    public long TotalRequests { get; set; }
    public long? FailedRequests { get; set; }
    public long? SuccessRequests { get; set; }
    public long? TotalTokens { get; set; }
    public long? MaxDurationMs { get; set; }
}

/// <summary>
/// Dapper 映射的检测页最新日志行（轻量投影，仅展示所需字段）。
/// </summary>
public sealed class DetectionLatestLogRow
{
    public Guid TargetSiteId { get; set; }
    public string RequestModel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string RequestedAt { get; set; } = string.Empty;
    public int TotalDurationMs { get; set; }

    /// <summary>
    /// <summary>
    /// 解析 RequestedAt 字符串为 DateTime（UTC）。
    /// 用 SpecifyKind 确保 Kind=Utc，避免 new DateTimeOffset(dateTime, Zero) 偏移不匹配。
    /// 解析失败时回退为 DateTime.MinValue，避免脏数据导致整页 500。
    /// </summary>
    public DateTime RequestedAtDateTime
    {
        get
        {
            try
            {
                return DateTime.SpecifyKind(
                    DateTime.Parse(RequestedAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
                    DateTimeKind.Utc);
            }
            catch
            {
                return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
            }
        }
    }
}

/// <summary>
/// Dapper 的 Guid 类型处理器：把 EF Core SQLite 存储的 TEXT 格式 Guid 转换为 System.Guid。
/// </summary>
public sealed class GuidTypeHandler : SqlMapper.TypeHandler<Guid>
{
    public override void SetValue(IDbDataParameter parameter, Guid value)
    {
        parameter.Value = value.ToString().ToUpperInvariant();
    }

    public override Guid Parse(object value)
    {
        return value switch
        {
            Guid g => g,
            string s => Guid.Parse(s),
            byte[] b when b.Length == 16 => new Guid(b),
            _ => Guid.Parse(value.ToString()!)
        };
    }
}
