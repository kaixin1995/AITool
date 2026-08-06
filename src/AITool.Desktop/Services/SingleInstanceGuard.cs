namespace AITool.Desktop.Services;

/// <summary>
/// 使用操作系统文件锁实现跨平台单实例保护。
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly FileStream _lockStream;
    private bool _disposed;

    private SingleInstanceGuard(FileStream lockStream)
    {
        _lockStream = lockStream;
    }

    public static SingleInstanceGuard? TryAcquire(string applicationId)
    {
        FileStream? lockStream = null;
        try
        {
            var lockDirectory = GetLockDirectory();
            Directory.CreateDirectory(lockDirectory);

            var lockPath = Path.Combine(lockDirectory, $"{applicationId}.instance.lock");
            lockStream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                options: FileOptions.WriteThrough);

            // FileShare.None 由各平台的文件系统负责互斥，进程结束后系统会自动释放占用。
            return new SingleInstanceGuard(lockStream);
        }
        catch (IOException)
        {
            lockStream?.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            lockStream?.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lockStream.Dispose();
    }

    private static string GetLockDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            return Path.Combine(localApplicationData, "AI Tool");
        }

        // 极少数精简运行环境可能没有标准用户目录，回退到临时目录确保仍能互斥。
        return Path.Combine(Path.GetTempPath(), "AI Tool");
    }
}
