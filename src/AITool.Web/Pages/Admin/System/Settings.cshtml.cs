using AITool.Application.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages.Admin.System;

// 系统设置页面模型，负责加载和保存运行时配置
public class SettingsModel : PageModel
{
    private readonly ISystemRuntimeSettingsService _systemRuntimeSettingsService;

    public SettingsModel(ISystemRuntimeSettingsService systemRuntimeSettingsService)
    {
        _systemRuntimeSettingsService = systemRuntimeSettingsService;
    }

    [BindProperty]
    public UpdateSystemRuntimeSettingsRequest Input { get; set; } = new();

    public int LastUsageLogPrunedCount { get; set; }

    public int LastDetectionLogPrunedCount { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await _systemRuntimeSettingsService.GetOrCreateAsync(cancellationToken);
        Input = new UpdateSystemRuntimeSettingsRequest
        {
            ProxyRequestTimeoutSeconds = settings.ProxyRequestTimeoutSeconds,
            ProxyRetryCount = settings.ProxyRetryCount,
            UsageLogRetentionDays = settings.UsageLogRetentionDays,
            DetectionLogRetentionDays = settings.DetectionLogRetentionDays
        };
        LastUsageLogPrunedCount = settings.LastUsageLogPrunedCount;
        LastDetectionLogPrunedCount = settings.LastDetectionLogPrunedCount;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            var settings = await _systemRuntimeSettingsService.GetOrCreateAsync(cancellationToken);
            LastUsageLogPrunedCount = settings.LastUsageLogPrunedCount;
            LastDetectionLogPrunedCount = settings.LastDetectionLogPrunedCount;
            return Page();
        }

        await _systemRuntimeSettingsService.UpdateAsync(Input, cancellationToken);
        return RedirectToPage();
    }
}
