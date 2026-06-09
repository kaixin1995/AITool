using System.Text;
using System.Text.Json;
using AITool.Application.CoreRuntime;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Core 当前生效配置快照的内存持有器，并负责把最近一次成功配置持久化到本地文件。
/// 这样即使 Admin 暂时未恢复，Core 也能在重启后用上次成功配置继续提供代理能力。
/// </summary>
public sealed class CoreRuntimeConfigProvider : ICoreRuntimeConfigProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private CoreRuntimeConfigSnapshot? _current;
    private readonly CoreRuntimeConfigFileOptions _options;
    private readonly ILogger<CoreRuntimeConfigProvider> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    /// <summary>
    /// 初始化 Core 配置持有器。
    /// </summary>
    public CoreRuntimeConfigProvider(
        CoreRuntimeConfigFileOptions options,
        ILogger<CoreRuntimeConfigProvider> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 当前是否已有可用配置。
    /// </summary>
    public bool IsReady => _current is not null;

    /// <summary>
    /// 读取当前配置快照。
    /// </summary>
    public CoreRuntimeConfigSnapshot? GetCurrent()
    {
        return Volatile.Read(ref _current);
    }

    /// <summary>
    /// 原子替换当前生效配置快照，并将其写入本地 last-good-config 文件。
    /// </summary>
    public void SetCurrent(CoreRuntimeConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Interlocked.Exchange(ref _current, snapshot);
        _ = PersistSnapshotSafelyAsync(snapshot);
    }

    /// <summary>
    /// 尝试从本地文件恢复最后一次成功配置。
    /// </summary>
    public async Task<bool> TryLoadFromFileAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.FilePath) || !File.Exists(_options.FilePath))
        {
            return false;
        }

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var json = await File.ReadAllTextAsync(_options.FilePath, Encoding.UTF8, cancellationToken);
            var snapshot = JsonSerializer.Deserialize<CoreRuntimeConfigSnapshot>(json, SerializerOptions);
            if (snapshot is null)
            {
                return false;
            }

            Interlocked.Exchange(ref _current, snapshot);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 Core 本地配置快照失败，FilePath={FilePath}", _options.FilePath);
            return false;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// 异步持久化最近一次成功配置，避免让请求线程等待磁盘 IO。
    /// </summary>
    private async Task PersistSnapshotSafelyAsync(CoreRuntimeConfigSnapshot snapshot)
    {
        try
        {
            await PersistSnapshotAsync(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入 Core 本地配置快照失败，FilePath={FilePath}", _options.FilePath);
        }
    }

    /// <summary>
    /// 通过临时文件替换的方式落盘，避免写到一半留下损坏文件。
    /// </summary>
    private async Task PersistSnapshotAsync(CoreRuntimeConfigSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(_options.FilePath))
        {
            return;
        }

        await _fileLock.WaitAsync();
        try
        {
            var directory = Path.GetDirectoryName(_options.FilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempFilePath = _options.FilePath + ".tmp";
            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            await File.WriteAllTextAsync(tempFilePath, json, Encoding.UTF8);
            File.Move(tempFilePath, _options.FilePath, true);
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
