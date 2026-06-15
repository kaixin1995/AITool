using AITool.Application.CoreRuntime;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Core 事件序号提供器（文件持久化版）。
/// <para>
/// 在 spool 根目录下维护 <c>sequence.meta</c> 文件，记录当前已分配序号，
/// 确保进程重启后序号不回退，避免与 Admin 已消费的旧事件序号冲突。
/// </para>
/// <para>
/// 写盘策略：定时批量落盘（默认每秒一次），而非每次递增即写。
/// <see cref="Next"/> 仅做内存 <see cref="Interlocked.Increment"/>，零磁盘 IO，
/// 由后台定时器把当前序号异步刷到 meta 文件。即使进程崩溃丢失未落盘的增量，
/// spool 文件中已持久化完整信封（含序号），下次启动时构造函数会用
/// <c>Math.Max(meta, spool最新序号)</c> 修正到正确值，可靠性不变。
/// </para>
/// </summary>
public sealed class CoreEventSequenceProvider : IDisposable
{
    /// <summary>
    /// 后台定时落盘间隔（秒）。
    /// </summary>
    private const int FlushIntervalSeconds = 1;

    private readonly string _metaFilePath;
    private readonly ILogger<CoreEventSequenceProvider> _logger;
    private long _current;
    private long _lastFlushed;
    private readonly Timer _flushTimer;
    private bool _disposed;

    /// <summary>
    /// 初始化序号提供器，从 meta 文件和 spool 文件恢复上次的序号。
    /// 取两者较大值，确保恢复值不落后（即使 meta 文件因崩溃落后于实际分配值）。
    /// </summary>
    public CoreEventSequenceProvider(
        CoreEventSpoolOptions spoolOptions,
        CoreEventSpoolStore spoolStore,
        ILogger<CoreEventSequenceProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(spoolOptions);
        ArgumentNullException.ThrowIfNull(spoolStore);
        _logger = logger;

        // 确保 spool 根目录路径可用
        if (string.IsNullOrWhiteSpace(spoolOptions.RootPath))
        {
            throw new InvalidOperationException("未配置 Core 事件 spool 根目录，无法初始化序号持久化");
        }

        Directory.CreateDirectory(spoolOptions.RootPath);
        _metaFilePath = Path.Combine(spoolOptions.RootPath, "sequence.meta");

        // 始终用 Math.Max(meta, spool) 恢复，确保不落后。
        // meta 可能在崩溃时落后（最多 FlushIntervalSeconds 的增量），spool 文件始终是最权威的。
        var metaValue = TryReadFromMetaFile() ?? 0;
        var spoolLatest = 0L;
        try
        {
            spoolLatest = spoolStore.GetLatestSequenceIdAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // spool 扫描失败不致命，meta 仍是可用的恢复源。
            _logger.LogWarning(ex, "启动时扫描 spool 文件失败，仅依赖 meta 文件恢复序号");
        }

        _current = Math.Max(metaValue, spoolLatest);
        _lastFlushed = _current;

        if (_current > 0)
        {
            logger.LogInformation("已恢复事件序号：meta={Meta}, spool={Spool}, 最终={Final}", metaValue, spoolLatest, _current);
            // 确保启动时 meta 文件就反映正确值。
            TryWriteToMetaFile(_current);
        }
        else
        {
            logger.LogInformation("未找到已有事件序号，从 0 开始");
        }

        // 后台定时落盘，每秒检查并刷写当前序号（仅在发生变化时才写）。
        _flushTimer = new Timer(
            _ => FlushCurrentIfNeeded(),
            null,
            TimeSpan.FromSeconds(FlushIntervalSeconds),
            TimeSpan.FromSeconds(FlushIntervalSeconds));
    }

    /// <summary>
    /// 生成下一个事件序号（纯内存操作，不写盘）。
    /// </summary>
    public long Next()
    {
        return Interlocked.Increment(ref _current);
    }

    /// <summary>
    /// 返回当前已分配到的最新事件序号。
    /// </summary>
    public long Current => Interlocked.Read(ref _current);

    /// <summary>
    /// 如果当前序号超过上次落盘值，把最新序号写入 meta 文件。
    /// </summary>
    private void FlushCurrentIfNeeded()
    {
        var current = Interlocked.Read(ref _current);
        var lastFlushed = Interlocked.Read(ref _lastFlushed);
        if (current <= lastFlushed)
        {
            return;
        }

        TryWriteToMetaFile(current);
        Interlocked.Exchange(ref _lastFlushed, current);
    }

    /// <summary>
    /// 尝试从 meta 文件读取序号。返回 null 表示文件不存在或解析失败。
    /// </summary>
    private long? TryReadFromMetaFile()
    {
        try
        {
            if (!File.Exists(_metaFilePath))
            {
                return null;
            }

            var content = File.ReadAllText(_metaFilePath).Trim();
            if (long.TryParse(content, out var value) && value >= 0)
            {
                return value;
            }

            _logger.LogWarning("meta 文件内容无法解析为有效序号：{Content}", content);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 sequence.meta 文件失败");
            return null;
        }
    }

    /// <summary>
    /// 将序号写入 meta 文件。失败时仅记录警告，不抛出异常。
    /// </summary>
    private void TryWriteToMetaFile(long sequenceId)
    {
        try
        {
            var tempFile = _metaFilePath + ".tmp";
            File.WriteAllText(tempFile, sequenceId.ToString());
            File.Move(tempFile, _metaFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 写盘失败不阻塞调用方。事件已经进入内存通道，spool 写入器会把它持久化到 JSONL，
            // 下次启动时可以从 spool 文件恢复。这里只是丢失了一个快速恢复路径。
            _logger.LogWarning(ex, "写入 sequence.meta 文件失败，序号={SequenceId}", sequenceId);
        }
    }

    /// <summary>
    /// 释放资源，做最后一次序号落盘。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _flushTimer?.Dispose();
        // 进程退出前强制最后一次落盘，最大程度保证 meta 文件不落后。
        FlushCurrentIfNeeded();
    }
}
