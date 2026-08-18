namespace AITool.Infrastructure.Common;

/// <summary>
/// 按字符串键串行化异步操作，并在没有引用时安全回收键对应的信号量。
/// </summary>
public sealed class KeyedAsyncLock
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, LockEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// 获取指定键的异步锁租约。
    /// </summary>
    public async Task<IDisposable> WaitAsync(string key, CancellationToken cancellationToken)
    {
        LockEntry entry;
        lock (_syncRoot)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new LockEntry();
                _entries[key] = entry;
            }

            entry.ReferenceCount++;
        }

        try
        {
            await entry.Gate.WaitAsync(cancellationToken);
            return new Lease(this, key, entry);
        }
        catch
        {
            ReleaseReference(key, entry, acquired: false);
            throw;
        }
    }

    private void ReleaseReference(string key, LockEntry entry, bool acquired)
    {
        if (acquired)
        {
            entry.Gate.Release();
        }

        var shouldDispose = false;
        lock (_syncRoot)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0
                && _entries.TryGetValue(key, out var current)
                && ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
                shouldDispose = true;
            }
        }

        if (shouldDispose)
        {
            entry.Gate.Dispose();
        }
    }

    private sealed class LockEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

    private sealed class Lease : IDisposable
    {
        private readonly KeyedAsyncLock _owner;
        private readonly string _key;
        private readonly LockEntry _entry;
        private int _disposed;

        public Lease(KeyedAsyncLock owner, string key, LockEntry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.ReleaseReference(_key, _entry, acquired: true);
            }
        }
    }
}
