using System.Text;
using System.Text.Json;
using AITool.Application.Conversations;
using AITool.Domain.Proxy;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Conversations;

/// <summary>
/// 基于本地 JSONL 文件的对话记录存储。
/// </summary>
public sealed class FileConversationLogStore : IConversationLogStore
{
    private const string FileExtension = ".jsonl";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _storageLock = new(1, 1);
    private readonly ConversationLogFileOptions _options;
    private readonly ILogger<FileConversationLogStore> _logger;

    /// <summary>
    /// 初始化本地文件对话记录存储。
    /// </summary>
    public FileConversationLogStore(
        ConversationLogFileOptions options,
        ILogger<FileConversationLogStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 批量追加对话记录到按天分片的本地 JSONL 文件。
    /// </summary>
    public async Task AppendBatchAsync(IReadOnlyList<ConversationTurnLog> logs, CancellationToken cancellationToken = default)
    {
        if (logs.Count == 0)
        {
            return;
        }

        await _storageLock.WaitAsync(cancellationToken);
        try
        {
            EnsureRootDirectory();
            await AppendBatchUnlockedAsync(logs, cancellationToken);
        }
        finally
        {
            _storageLock.Release();
        }
    }

    /// <summary>
    /// 按时间范围与筛选条件读取本地对话记录。
    /// 从最新分片文件倒序读取，<b>过滤与计数在读取行内同步进行</b>，达到
    /// <see cref="ConversationLogStoragePolicy.MaxQueryTurns"/> 上限立即停止读取后续行与文件，
    /// 不会把整个文件全量反序列化进内存。
    /// </summary>
    public async Task<IReadOnlyList<ConversationTurnLog>> QueryAsync(ConversationLogQuery query, CancellationToken cancellationToken = default)
    {
        await _storageLock.WaitAsync(cancellationToken);
        try
        {
            EnsureRootDirectory();
            // 候选文件倒序排列（最新优先），达到上限时跳过更旧的文件。
            var filePaths = ResolveCandidateFilePaths(query.StartTime, query.EndTime)
                .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var results = new List<ConversationTurnLog>(Math.Min(ConversationLogStoragePolicy.MaxQueryTurns, 256));
            var matcher = new ConversationLogMatcher(query);
            foreach (var filePath in filePaths)
            {
                if (results.Count >= ConversationLogStoragePolicy.MaxQueryTurns)
                {
                    break;
                }

                // 返回 true 表示已达到上限，外层立即停止读后续文件。
                if (await ReadAndFilterAsync(filePath, matcher, ConversationLogStoragePolicy.MaxQueryTurns, results, cancellationToken))
                {
                    break;
                }
            }

            // 返回前按时间升序排列，保持前端展示顺序（最旧在前）。
            results.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
            return results;
        }
        finally
        {
            _storageLock.Release();
        }
    }

    /// <summary>
    /// 按条件流式聚合查询会话摘要（不保留整条记录，避免把全部正文一次性物化进内存）。
    /// 单次扫描候选文件，逐行更新 <see cref="ConversationSessionSummary"/> 聚合结果。
    /// </summary>
    public async Task<IReadOnlyList<ConversationSessionSummary>> QuerySessionSummariesAsync(ConversationLogQuery query, CancellationToken cancellationToken = default)
    {
        await _storageLock.WaitAsync(cancellationToken);
        try
        {
            EnsureRootDirectory();
            var filePaths = ResolveCandidateFilePaths(query.StartTime, query.EndTime)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var summaries = new Dictionary<string, ConversationSessionSummary>(StringComparer.Ordinal);
            var matcher = new ConversationLogMatcher(query);
            foreach (var filePath in filePaths)
            {
                await AggregateSessionSummariesAsync(filePath, matcher, summaries, cancellationToken);
            }

            // 列表按最近活动时间倒序，与历史行为一致。
            return summaries.Values
                .OrderByDescending(x => x.LastActivityAt)
                .ToList();
        }
        finally
        {
            _storageLock.Release();
        }
    }

    /// <summary>
    /// 删除某个会话的全部本地记录。
    /// </summary>
    public async Task<int> DeleteSessionAsync(string groupKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(groupKey))
        {
            return 0;
        }

        await _storageLock.WaitAsync(cancellationToken);
        try
        {
            EnsureRootDirectory();
            var totalRemoved = 0;
            foreach (var filePath in EnumerateAllLogFiles())
            {
                var records = await ReadRecordsFromFileUnlockedAsync(filePath, cancellationToken);
                if (records.Count == 0)
                {
                    continue;
                }

                var keptRecords = records
                    .Where(x => !string.Equals(x.ConversationGroupKey, groupKey, StringComparison.Ordinal))
                    .ToList();
                var removedCount = records.Count - keptRecords.Count;
                if (removedCount <= 0)
                {
                    continue;
                }

                totalRemoved += removedCount;
                await RewriteFileUnlockedAsync(filePath, keptRecords, cancellationToken);
            }

            return totalRemoved;
        }
        finally
        {
            _storageLock.Release();
        }
    }

    /// <summary>
    /// 更新某个会话的自定义标题。
    /// </summary>
    public async Task<int> UpdateSessionTitleAsync(string groupKey, string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(groupKey))
        {
            return 0;
        }

        await _storageLock.WaitAsync(cancellationToken);
        try
        {
            EnsureRootDirectory();
            var updatedCount = 0;
            foreach (var filePath in EnumerateAllLogFiles())
            {
                var records = await ReadRecordsFromFileUnlockedAsync(filePath, cancellationToken);
                if (records.Count == 0)
                {
                    continue;
                }

                var changed = false;
                foreach (var record in records.Where(x => string.Equals(x.ConversationGroupKey, groupKey, StringComparison.Ordinal)))
                {
                    if (string.Equals(record.ConversationTitle, title, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    record.ConversationTitle = title;
                    updatedCount++;
                    changed = true;
                }

                if (changed)
                {
                    await RewriteFileUnlockedAsync(filePath, records, cancellationToken);
                }
            }

            return updatedCount;
        }
        finally
        {
            _storageLock.Release();
        }
    }

    /// <summary>
    /// 清理本地对话记录中过期的数据。
    /// </summary>
    public async Task PruneExpiredAsync(CancellationToken cancellationToken = default)
    {
        await _storageLock.WaitAsync(cancellationToken);
        try
        {
            EnsureRootDirectory();
            var cutoff = DateTimeOffset.Now.AddDays(-ConversationLogStoragePolicy.RetentionDays);
            var cutoffLocalDate = cutoff.ToLocalTime().Date;
            foreach (var filePath in EnumerateAllLogFiles())
            {
                var fileDate = ResolveFileDate(filePath);
                if (fileDate.HasValue && fileDate.Value < cutoffLocalDate)
                {
                    File.Delete(filePath);
                    continue;
                }

                var records = await ReadRecordsFromFileUnlockedAsync(filePath, cancellationToken);
                if (records.Count == 0)
                {
                    continue;
                }

                var keptRecords = records
                    .Where(x => x.CreatedAt >= cutoff)
                    .ToList();
                if (keptRecords.Count == records.Count)
                {
                    continue;
                }

                await RewriteFileUnlockedAsync(filePath, keptRecords, cancellationToken);
            }
        }
        finally
        {
            _storageLock.Release();
        }
    }

    /// <summary>
    /// 追加写入时按本地日期分片，减少单文件增长速度。
    /// </summary>
    private async Task AppendBatchUnlockedAsync(IReadOnlyList<ConversationTurnLog> logs, CancellationToken cancellationToken)
    {
        foreach (var group in logs.GroupBy(x => BuildDayFilePath(x.CreatedAt)))
        {
            await using var stream = new FileStream(group.Key, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (var log in group.OrderBy(x => x.CreatedAt))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(JsonSerializer.Serialize(log, SerializerOptions));
            }
        }
    }

    /// <summary>
    /// 逐行读取单个 JSONL 文件全量记录（用于删除 / 改名 / 过期清理这类需要完整数据集的场景）。
    /// </summary>
    private async Task<List<ConversationTurnLog>> ReadRecordsFromFileUnlockedAsync(string filePath, CancellationToken cancellationToken)
    {
        var records = new List<ConversationTurnLog>();
        if (!File.Exists(filePath))
        {
            return records;
        }

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<ConversationTurnLog>(line, SerializerOptions);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析本地对话记录失败，文件={FilePath}", filePath);
            }
        }

        return records;
    }

    /// <summary>
    /// 流式读取单个 JSONL 文件，逐行反序列化并按 <paramref name="matcher"/> 过滤；
    /// 命中的记录加入 <paramref name="results"/>，达到 <paramref name="maxResults"/> 上限立即停止读取，
    /// 不满足过滤条件的记录立即丢弃、绝不保留，避免把整个文件物化进内存。
    /// </summary>
    /// <returns>已达到 <paramref name="maxResults"/> 上限返回 true，调用方据此停止读后续文件。</returns>
    private async Task<bool> ReadAndFilterAsync(
        string filePath,
        ConversationLogMatcher matcher,
        int maxResults,
        List<ConversationTurnLog> results,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ConversationTurnLog? record;
            try
            {
                record = JsonSerializer.Deserialize<ConversationTurnLog>(line, SerializerOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析本地对话记录失败，文件={FilePath}", filePath);
                continue;
            }

            if (record is null || !matcher.Matches(record))
            {
                // 不命中条件立即丢弃，不进入 results，不占内存。
                continue;
            }

            results.Add(record);
            if (results.Count >= maxResults)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 流式读取单个 JSONL 文件，逐行聚合到 <paramref name="summaries"/>（按 GroupKey 分组）。
    /// 只更新聚合字段，不保留整条 <see cref="ConversationTurnLog"/>，内存占用与会话数成正比而非记录数。
    /// </summary>
    private async Task AggregateSessionSummariesAsync(
        string filePath,
        ConversationLogMatcher matcher,
        Dictionary<string, ConversationSessionSummary> summaries,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ConversationTurnLog? record;
            try
            {
                record = JsonSerializer.Deserialize<ConversationTurnLog>(line, SerializerOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析本地对话记录失败，文件={FilePath}", filePath);
                continue;
            }

            if (record is null || !matcher.Matches(record))
            {
                continue;
            }

            AggregateRecord(summaries, record);
        }
    }

    /// <summary>
    /// 把单条记录聚合进会话摘要字典。每会话只保留列表展示所需字段，不物化记录正文。
    /// </summary>
    private static void AggregateRecord(Dictionary<string, ConversationSessionSummary> summaries, ConversationTurnLog record)
    {
        var groupKey = record.ConversationGroupKey;
        if (string.IsNullOrWhiteSpace(groupKey))
        {
            return;
        }

        if (!summaries.TryGetValue(groupKey, out var summary))
        {
            summary = new ConversationSessionSummary { GroupKey = groupKey };
            summaries[groupKey] = summary;
        }

        summary.TurnCount++;
        summary.TotalTokens += record.InputTokens + record.CachedTokens + record.OutputTokens;

        // 最近一条记录：取 CreatedAt 最大者，并同步其来源 / 会话标识。
        if (record.CreatedAt > summary.LastActivityAt)
        {
            summary.LastActivityAt = record.CreatedAt;
            summary.SourceTool = record.SourceTool;
            summary.SessionId = record.SessionId;
        }

        // 首个非空自定义标题。
        if (string.IsNullOrEmpty(summary.ConversationTitle) && !string.IsNullOrWhiteSpace(record.ConversationTitle))
        {
            summary.ConversationTitle = record.ConversationTitle;
        }

        // 首条非空用户输入的压缩原文（保留压缩态，由调用方按需解压取标题预览）。
        if (string.IsNullOrEmpty(summary.FirstUserInputTextCompressed) && !string.IsNullOrWhiteSpace(record.UserInputText))
        {
            summary.FirstUserInputTextCompressed = record.UserInputText;
        }
    }

    /// <summary>
    /// 通过临时文件原子替换分片内容，避免更新标题或删除会话时留下半写入文件。
    /// </summary>
    private static async Task RewriteFileUnlockedAsync(string filePath, IReadOnlyList<ConversationTurnLog> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            return;
        }

        var tempFilePath = filePath + ".tmp";
        await using (var stream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            foreach (var record in records.OrderBy(x => x.CreatedAt))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(JsonSerializer.Serialize(record, SerializerOptions));
            }
        }

        File.Move(tempFilePath, filePath, true);
    }

    /// <summary>
    /// 根据查询时间范围推导需要读取的本地日期分片。
    /// </summary>
    private IReadOnlyList<string> ResolveCandidateFilePaths(DateTimeOffset startTime, DateTimeOffset endTime)
    {
        var startDate = startTime.ToLocalTime().Date;
        var endDate = (endTime <= startTime ? startTime : endTime.AddTicks(-1)).ToLocalTime().Date;
        var filePaths = new List<string>();
        for (var current = startDate; current <= endDate; current = current.AddDays(1))
        {
            filePaths.Add(Path.Combine(_options.RootPath, current.ToString("yyyyMMdd") + FileExtension));
        }

        return filePaths;
    }

    /// <summary>
    /// 枚举当前本地根目录下全部分片文件。
    /// </summary>
    private IEnumerable<string> EnumerateAllLogFiles()
    {
        if (!Directory.Exists(_options.RootPath))
        {
            return [];
        }

        return Directory.EnumerateFiles(_options.RootPath, "*" + FileExtension, SearchOption.TopDirectoryOnly)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 生成某条记录所属的按天分片文件路径。
    /// </summary>
    private string BuildDayFilePath(DateTimeOffset createdAt)
    {
        return Path.Combine(_options.RootPath, createdAt.ToLocalTime().ToString("yyyyMMdd") + FileExtension);
    }

    /// <summary>
    /// 从文件名中解析对应的本地日期。
    /// </summary>
    private static DateTime? ResolveFileDate(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        return DateTime.TryParseExact(fileName, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date)
            ? date.Date
            : null;
    }

    /// <summary>
    /// 确保本地根目录存在。
    /// </summary>
    private void EnsureRootDirectory()
    {
        if (string.IsNullOrWhiteSpace(_options.RootPath))
        {
            throw new InvalidOperationException("未配置对话记录本地目录");
        }

        Directory.CreateDirectory(_options.RootPath);
    }
}

/// <summary>
/// 封装对话记录过滤条件，供流式读取逐行判断命中时复用，避免重复解析查询参数。
/// </summary>
internal sealed class ConversationLogMatcher
{
    private readonly DateTimeOffset _startTime;
    private readonly DateTimeOffset _endTime;
    private readonly string _sourceTool;
    private readonly bool _hasSourceTool;
    private readonly string _requestModel;
    private readonly bool _hasRequestModel;
    private readonly string _sessionKeyword;
    private readonly bool _hasSessionKeyword;
    private readonly string _groupKey;
    private readonly bool _hasGroupKey;

    public ConversationLogMatcher(ConversationLogQuery query)
    {
        _startTime = query.StartTime;
        _endTime = query.EndTime;
        _sourceTool = query.SourceTool ?? string.Empty;
        _hasSourceTool = !string.IsNullOrWhiteSpace(_sourceTool);
        _requestModel = query.RequestModel ?? string.Empty;
        _hasRequestModel = !string.IsNullOrWhiteSpace(_requestModel);
        _sessionKeyword = query.SessionKeyword ?? string.Empty;
        _hasSessionKeyword = !string.IsNullOrWhiteSpace(_sessionKeyword);
        _groupKey = query.GroupKey ?? string.Empty;
        _hasGroupKey = !string.IsNullOrWhiteSpace(_groupKey);
    }

    /// <summary>
    /// 判断单条记录是否满足全部查询条件。
    /// </summary>
    public bool Matches(ConversationTurnLog record)
    {
        if (record.CreatedAt < _startTime || record.CreatedAt >= _endTime)
        {
            return false;
        }

        if (_hasSourceTool && !string.Equals(record.SourceTool, _sourceTool, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_hasRequestModel && !string.Equals(record.RequestModel, _requestModel, StringComparison.Ordinal))
        {
            return false;
        }

        if (_hasSessionKeyword && !record.SessionId.Contains(_sessionKeyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_hasGroupKey && !string.Equals(record.ConversationGroupKey, _groupKey, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }
}
