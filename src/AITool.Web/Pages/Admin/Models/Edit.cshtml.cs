using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.Models;

// 模型编辑页模型，加载现有模型数据并提供更新功能
public class EditModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public EditModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [BindProperty]
    public string ModelName { get; set; } = string.Empty;

    [BindProperty]
    public string DisplayName { get; set; } = string.Empty;

    [BindProperty]
    public string ModelType { get; set; } = "chat";

    [BindProperty]
    public bool IsEnabled { get; set; }

    // 状态消息
    public string? StatusMessage { get; set; }
    public bool StatusSuccess { get; set; }

    // 加载模型数据填充表单
    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var model = await _dbContext.ModelLibraryItems.FindAsync([id], cancellationToken);
        if (model is null) return RedirectToPage("./Index");

        ModelName = model.ModelName;
        DisplayName = model.DisplayName;
        ModelType = model.ModelType;
        IsEnabled = model.IsEnabled;

        return Page();
    }

    // 提交模型更新
    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return Page();

        try
        {
            var model = await _dbContext.ModelLibraryItems.FindAsync([id], cancellationToken);
            if (model is null) return RedirectToPage("./Index");

            model.ModelName = ModelName;
            model.DisplayName = DisplayName;
            model.ModelType = ModelType;
            model.IsEnabled = IsEnabled;

            await _dbContext.SaveChangesAsync(cancellationToken);
            StatusMessage = "模型已更新";
            StatusSuccess = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败：{ex.Message}";
            StatusSuccess = false;
        }

        return Page();
    }
}
