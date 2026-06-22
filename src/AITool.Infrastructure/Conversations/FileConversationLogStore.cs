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
    /// <summary>
    /// 单次查询最多返回的记录数。从最新文件倒序读取，达到上限立即停止，
    /// 避免保留窗口内全部记录（含数十 KB 的对话正文）一次性加载到内存导致内存暴涨。
    /// </summary>
    private const int MaxQueryResults = 1000;
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
    /// 从最新分片文件倒序读取，逐行过滤并收集，达到 <see cref="MaxQueryResults"/> 上限立即停止，
    /// 避免保留窗口内全部记录（含数十 KB 的对话正文）一次性加载到内存。
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

            var results = new List<ConversationTurnLog>(Math.Min(MaxQueryResults, 256));
            foreach (var filePath in filePaths)
            {
                if (results.Count >= MaxQueryResults)
                {
                    break;
                }

                var fileRecords = await ReadRecordsFromFileUnlockedAsync(filePath, cancellationToken);
                foreach (var record in fileRecords)
                {
                    if (results.Count >= MaxQueryResults)
                    {
                        break;
                    }

                    if (record.CreatedAt < query.StartTime || record.CreatedAt >= query.EndTime)
                    {
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(query.SourceTool)
                        && !string.Equals(record.SourceTool, query.SourceTool, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(query.RequestModel)
                        && !string.Equals(record.RequestModel, query.RequestModel, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(query.SessionKeyword)
                        && !record.SessionId.Contains(query.SessionKeyword, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(query.GroupKey)
                        && !string.Equals(record.ConversationGroupKey, query.GroupKey, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    results.Add(record);
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
    /// 从多个候选分片文件中读取并反序列化记录。
    /// </summary>
    private async Task<List<ConversationTurnLog>> ReadRecordsFromFilesUnlockedAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken)
    {
        var records = new List<ConversationTurnLog>();
        foreach (var filePath in filePaths)
        {
            records.AddRange(await ReadRecordsFromFileUnlockedAsync(filePath, cancellationToken));
        }

        return records;
    }

    /// <summary>
    /// 逐行读取单个 JSONL 文件，避免一次性把整文件文本读入内存。
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
