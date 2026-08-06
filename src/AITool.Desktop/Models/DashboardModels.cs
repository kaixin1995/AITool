namespace AITool.Desktop.Models;

public sealed class DashboardStats
{
    public int SiteCount { get; set; }
    public int ModelCount { get; set; }
    public int MappingCount { get; set; }
    public int RouteCount { get; set; }
    public int AccessKeyCount { get; set; }
    public int DetectionTaskCount { get; set; }
    public string? CoreBaseUrl { get; set; }
    public string? CoreStatusText { get; set; }
    public string? CoreSyncStatusText { get; set; }
    public string? CoreSyncDetailText { get; set; }
}
