namespace AITool.Admin.Services;

public sealed class CoreSyncStatusStore
{
    private readonly object _lock = new();

    public CoreSyncStatusSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new CoreSyncStatusSnapshot
            {
                LastAttemptAt = LastAttemptAt,
                LastSuccessAt = LastSuccessAt,
                LastFailureAt = LastFailureAt,
                LastStatus = LastStatus,
                LastError = LastError
            };
        }
    }

    public void MarkSuccess(DateTimeOffset attemptedAt, string status)
    {
        lock (_lock)
        {
            LastAttemptAt = attemptedAt;
            LastSuccessAt = attemptedAt;
            LastStatus = status;
            LastError = string.Empty;
        }
    }

    public void MarkFailure(DateTimeOffset attemptedAt, string status, string error)
    {
        lock (_lock)
        {
            LastAttemptAt = attemptedAt;
            LastFailureAt = attemptedAt;
            LastStatus = status;
            LastError = error;
        }
    }

    public DateTimeOffset? LastAttemptAt { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public DateTimeOffset? LastFailureAt { get; private set; }
    public string LastStatus { get; private set; } = "未同步";
    public string LastError { get; private set; } = string.Empty;
}

public sealed class CoreSyncStatusSnapshot
{
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public DateTimeOffset? LastFailureAt { get; init; }
    public string LastStatus { get; init; } = "未同步";
    public string LastError { get; init; } = string.Empty;
}
