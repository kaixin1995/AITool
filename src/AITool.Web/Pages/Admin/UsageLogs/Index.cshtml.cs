using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.UsageLogs;

// 使用日志视图模型
public class UsageLogViewModel
{
    public Guid Id { get; set; }
    public string ProtocolType { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
}

// 调用日志查询页面模型
public class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 使用日志列表
    public List<UsageLogViewModel> Logs { get; set; } = [];

    // 加载最近的使用日志
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Logs = await _dbContext.ProxyUsageLogs
            .OrderByDescending(l => l.RequestedAt)
            .Take(200)
            .Select(l => new UsageLogViewModel
            {
                Id = l.Id,
                ProtocolType = l.ProtocolType,
                RequestModel = l.RequestModel,
                Status = l.Status,
                InputTokens = l.InputTokens,
                OutputTokens = l.OutputTokens,
                TotalTokens = l.TotalTokens,
                RequestedAt = l.RequestedAt
            })
            .ToListAsync(cancellationToken);
    }
}
