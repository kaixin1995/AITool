using Microsoft.Extensions.Logging;

namespace AITool.Admin.Services;

/// <summary>
/// Admin 侧事件 ack 状态持久化存储。
/// <para>
/// 将已确认的事件序号写入本地 <c>ack.meta</c> 文件，确保 Admin 进程重启后
/// 能从正确的序号位置继续拉取，避免重复消费已入库的历史事件。
/// </para>
/// <para>
/// 文件格式：纯文本数字，代表 Admin 已确认的最大事件序号。
/// 写入使用"先写临时文件再重命名"策略，确保原子性，避免写入中途崩溃导致文件损坏。
/// </para>
/// </summary>
public sealed class CoreEventAckStateStore
{
    private readonly string _metaFilePath;
    private readonly ILogger<CoreEventAckStateStore> _logger;

    /// <summary>
    /// 初始化 ack 状态存储。
    /// </summary>
    /// <param name="metaFilePath">ack.meta 文件的完整路径，由调用方从配置或默认路径构建。</param>
    /// <param name="logger">日志记录器。</param>
    public CoreEventAckStateStore(string metaFilePath, ILogger<CoreEventAckStateStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metaFilePath);
        _metaFilePath = metaFilePath;
        _logger = logger;

        // 确保文件所在目录存在
        var directory = Path.GetDirectoryName(_metaFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// 尝试从文件恢复已确认的序号。
    /// 文件不存在或解析失败时返回 0，表示需要从头开始消费。
    /// </summary>
    public long LoadAckedSequenceId()
    {
        try
        {
            if (!File.Exists(_metaFilePath))
            {
                return 0;
            }

            var content = File.ReadAllText(_metaFilePath).Trim();
            if (long.TryParse(content, out var value) && value >= 0)
            {
                _logger.LogInformation("已从 ack.meta 恢复确认序号：{SequenceId}", value);
                return value;
            }

            _logger.LogWarning("ack.meta 文件内容无法解析为有效序号：{Content}，将从 0 开始", content);
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 ack.meta 文件失败，将从 0 开始");
            return 0;
        }
    }

    /// <summary>
    /// 将最新确认序号持久化到文件。
    /// 写入失败仅记录警告，不抛出异常，不影响主流程。
    /// </summary>
    public void SaveAckedSequenceId(long sequenceId)
    {
        try
        {
            var tempFile = _metaFilePath + ".tmp";
            File.WriteAllText(tempFile, sequenceId.ToString());
            File.Move(tempFile, _metaFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入 ack.meta 文件失败，确认序号={SequenceId}", sequenceId);
        }
    }
}
