using System.Globalization;

namespace AITool.Desktop.Models;

public sealed class RouteFallbackListResponse
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public List<RouteFallbackEvent> Items { get; set; } = new();
    public RouteFallbackSummary Summary { get; set; } = new();
    public int SampleLogLimit { get; set; }
    public bool IsTruncated { get; set; }
    public string? SampleOldestRequestedAt { get; set; }
}

public sealed class RouteFallbackSummary
{
    public int TotalCount { get; set; }
    public int UniqueFromSites { get; set; }
    public int UniqueToSites { get; set; }
    public string? LatestOccurredAt { get; set; }

    public string LatestOccurredAtText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LatestOccurredAt)) return "-";
            return DateTimeOffset.TryParse(LatestOccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
                ? date.ToLocalTime().ToString("yyyy/M/d HH:mm:ss", CultureInfo.InvariantCulture)
                : LatestOccurredAt;
        }
    }
}

public sealed class RouteFallbackEvent
{
    public string RequestId { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string FromSiteId { get; set; } = string.Empty;
    public string FromSiteName { get; set; } = string.Empty;
    public string FromSiteModelName { get; set; } = string.Empty;
    public string ToSiteId { get; set; } = string.Empty;
    public string ToSiteName { get; set; } = string.Empty;
    public string ToSiteModelName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string OccurredAt { get; set; } = string.Empty;

    public string OccurredAtText => FormatDateTime(OccurredAt);
    public string RequestModelText => FirstNonEmpty(RequestModel, "-");
    public string FromSiteNameText => FirstNonEmpty(FromSiteName, "-");
    public string FromSiteModelText => FirstNonEmpty(FromSiteModelName, "-");
    public string ToSiteNameText => FirstNonEmpty(ToSiteName, "-");
    public string ToSiteModelText => FirstNonEmpty(ToSiteModelName, "-");
    public string ReasonText => FirstNonEmpty(Reason, "-");

    private static string FirstNonEmpty(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string FormatDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date.ToLocalTime().ToString("yyyy/M/d HH:mm:ss", CultureInfo.InvariantCulture)
            : value;
    }
}
