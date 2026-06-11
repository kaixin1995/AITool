using AITool.Application.CoreRuntime;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Core 事件序号提供器（文件持久化版）。
/// <para>
/// 在 spool 根目录下维护 <c>sequence.meta</c> 文件，每次递增后立即写盘，
/// 确保进程重启后序号不回退，避免与 Admin 已消费的旧事件序号冲突。
/// </para>
/// <para>
/// 写盘策略：写穿（write-through），递增即写。写盘失败仅记录警告、不阻塞调用方。
/// 因为 spool 文件中已经持久化了完整信封（含序号），即使 meta 文件偶尔写失败，
/// 下次启动时也可以从 spool 文件恢复到最新序号。
/// </para>
/// </summary>
public sealed class CoreEventSequenceProvider
{
    private readonly string _metaFilePath;
    private readonly ILogger<CoreEventSequenceProvider> _logger;
    private long _current;

    /// <summary>
    /// 初始化序号提供器，从 meta 文件恢复上次的序号。
    /// 如果 meta 文件不存在或无法解析，尝试从 spool 文件中恢复最新序号。
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

        // 尝试从 meta 文件恢复
        var restored = TryReadFromMetaFile();
        if (restored.HasValue)
        {
            _current = restored.Value;
            logger.LogInformation("已从 meta 文件恢复事件序号：{SequenceId}", _current);
            return;
        }

        // meta 文件不可用，从 spool 文件中扫描恢复
        var spoolLatest = spoolStore.GetLatestSequenceIdAsync().GetAwaiter().GetResult();
        if (spoolLatest > 0)
        {
            _current = spoolLatest;
            logger.LogInformation("已从 spool 文件恢复事件序号：{SequenceId}", _current);
            // 回写 meta 文件，后续启动就不需要重新扫描
            TryWriteToMetaFile(_current);
        }
        else
        {
            logger.LogInformation("未找到已有事件序号，从 0 开始");
        }
    }

    /// <summary>
    /// 生成下一个事件序号并持久化到磁盘。
    /// </summary>
    public long Next()
    {
        var next = Interlocked.Increment(ref _current);
        TryWriteToMetaFile(next);
        return next;
    }

    /// <summary>
    /// 返回当前已分配到的最新事件序号。
    /// </summary>
    public long Current => Interlocked.Read(ref _current);

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
}
