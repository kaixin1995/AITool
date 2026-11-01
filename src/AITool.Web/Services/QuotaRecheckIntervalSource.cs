namespace AITool.Web.Services;

/// <summary>Independent jitter draws, inclusive of both endpoints.</summary>
public sealed class QuotaRecheckIntervalSource : IQuotaRecheckIntervalSource
{
    public TimeSpan NextInterval() => TimeSpan.FromSeconds(Random.Shared.Next(2400, 3601));

    public TimeSpan NextCatchUpDelay() => TimeSpan.FromSeconds(Random.Shared.Next(0, 61));
}
