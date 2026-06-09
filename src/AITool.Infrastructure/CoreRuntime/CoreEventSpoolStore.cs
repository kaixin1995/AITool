using System.Text;
using System.Text.Json;
using AITool.Application.CoreRuntime;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Core 事件 spool 存储。
/// 当前阶段先实现最小能力：
/// - 事件发布时同时追加写入本地 JSONL
/// - Admin ack 后删除不再需要的旧记录
/// 这样即使后续 Admin 临时不在线，Core 也已经具备最基础的磁盘兜底能力。
/// </summary>
public sealed class CoreEventSpoolStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly CoreEventSpoolOptions _options;
    private readonly ILogger<CoreEventSpoolStore> _logger;

    /// <summary>
    /// 初始化 Core 事件 spool 存储。
    /// </summary>
    public CoreEventSpoolStore(CoreEventSpoolOptions options, ILogger<CoreEventSpoolStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 追加保存一条事件到本地 spool 文件。
    /// </summary>
    public async Task AppendAsync(CoreAdminEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            EnsureRootDirectory();
            var filePath = ResolveCurrentFilePath();
            await using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync(JsonSerializer.Serialize(envelope, SerializerOptions));
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// 返回当前 spool 中已知的最新事件序号。
    /// </summary>
    public async Task<long> GetLatestSequenceIdAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            EnsureRootDirectory();
            long latest = 0;
            foreach (var filePath in EnumerateSpoolFiles())
            {
                var envelopes = await ReadAllAsync(filePath, cancellationToken);
                if (envelopes.Count == 0)
                {
                    continue;
                }

                latest = Math.Max(latest, envelopes[^1].SequenceId);
            }

            return latest;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// 根据 ack 序号清理已确认事件，保留尚未确认的数据供后续 replay 使用。
    /// </summary>
    public async Task TrimAckedAsync(long ackedSequenceId, CancellationToken cancellationToken = default)
    {
        if (ackedSequenceId <= 0)
        {
            return;
        }

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            EnsureRootDirectory();
            foreach (var filePath in EnumerateSpoolFiles())
            {
                var envelopes = await ReadAllAsync(filePath, cancellationToken);
                if (envelopes.Count == 0)
                {
                    continue;
                }

                var remaining = envelopes
                    .Where(x => x.SequenceId > ackedSequenceId)
                    .ToList();
                if (remaining.Count == envelopes.Count)
                {
                    continue;
                }

                if (remaining.Count == 0)
                {
                    File.Delete(filePath);
                    continue;
                }

                await RewriteAllAsync(filePath, remaining, cancellationToken);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// 返回从指定序号之后开始的全部积压事件。
    /// 当前阶段先提供简单版读取，为后续 replay 接口做准备。
    /// </summary>
    public async Task<IReadOnlyList<CoreAdminEventEnvelope>> ListAfterAsync(long afterSequenceId, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            EnsureRootDirectory();
            var all = new List<CoreAdminEventEnvelope>();
            foreach (var filePath in EnumerateSpoolFiles())
            {
                var envelopes = await ReadAllAsync(filePath, cancellationToken);
                all.AddRange(envelopes.Where(x => x.SequenceId > afterSequenceId));
            }

            return all.OrderBy(x => x.SequenceId).ToList();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// 当前 spool 是否存在积压文件。
    /// </summary>
    public bool HasBacklog()
    {
        EnsureRootDirectory();
        return EnumerateSpoolFiles().Any();
    }

    /// <summary>
    /// 确保 spool 根目录存在。
    /// </summary>
    private void EnsureRootDirectory()
    {
        if (string.IsNullOrWhiteSpace(_options.RootPath))
        {
            throw new InvalidOperationException("未配置 Core 事件 spool 根目录");
        }

        Directory.CreateDirectory(_options.RootPath);
    }

    /// <summary>
    /// 当前阶段先按本地日期滚动 JSONL，后续再按大小或条数进一步细化。
    /// </summary>
    private string ResolveCurrentFilePath()
    {
        return Path.Combine(_options.RootPath, $"events-{DateTimeOffset.Now:yyyyMMdd}.jsonl");
    }

    /// <summary>
    /// 返回当前全部 spool 文件，按文件名顺序输出便于 replay 时保持时间顺序。
    /// </summary>
    private IEnumerable<string> EnumerateSpoolFiles()
    {
        if (!Directory.Exists(_options.RootPath))
        {
            return [];
        }

        return Directory.EnumerateFiles(_options.RootPath, "events-*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 读取单个 JSONL 文件中的全部事件。
    /// </summary>
    private async Task<List<CoreAdminEventEnvelope>> ReadAllAsync(string filePath, CancellationToken cancellationToken)
    {
        var results = new List<CoreAdminEventEnvelope>();
        if (!File.Exists(filePath))
        {
            return results;
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
                var envelope = JsonSerializer.Deserialize<CoreAdminEventEnvelope>(line, SerializerOptions);
                if (envelope is not null)
                {
                    results.Add(envelope);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析 Core 事件 spool 失败，FilePath={FilePath}", filePath);
            }
        }

        return results;
    }

    /// <summary>
    /// 用剩余事件重写 spool 文件，避免 ack 后长期保留已确认数据。
    /// </summary>
    private static async Task RewriteAllAsync(string filePath, IReadOnlyList<CoreAdminEventEnvelope> envelopes, CancellationToken cancellationToken)
    {
        var tempFilePath = filePath + ".tmp";
        await using (var stream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            foreach (var envelope in envelopes.OrderBy(x => x.SequenceId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(JsonSerializer.Serialize(envelope, SerializerOptions));
            }
        }

        File.Move(tempFilePath, filePath, true);
    }
}
