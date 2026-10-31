namespace AITool.Web.Services;

/// <summary>Serializes complete route graph reads/checks/writes across mutable configuration paths.</summary>
public static class RouteGraphMutationGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Acquire before opening a mutation transaction; dispose after commit and cache invalidation.</summary>
    public static async ValueTask<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Gate.Release();
        }
    }
}
