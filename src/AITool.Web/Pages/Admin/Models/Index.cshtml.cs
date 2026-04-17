using AITool.Application.Models;
using AITool.Domain.Models;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.Models;

// 模型库列表页模型
public class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 模型库列表数据
    public List<ModelLibraryItem> Models { get; set; } = [];

    // 加载模型列表
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Models = await _dbContext.ModelLibraryItems
            .OrderBy(x => x.ModelName)
            .ToListAsync(cancellationToken);
    }
}

// 模型库创建页模型
public class CreateModelModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public CreateModelModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [BindProperty]
    public CreateModelLibraryItemCommand Command { get; set; } = new();

    // 显示创建表单
    public void OnGet() { }

    // 提交模型创建
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return Page();

        // 将命令转为实体并保存
        _dbContext.ModelLibraryItems.Add(new ModelLibraryItem
        {
            ModelName = Command.ModelName,
            DisplayName = Command.DisplayName,
            ModelType = Command.ModelType,
            IsEnabled = Command.IsEnabled
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return RedirectToPage("./Index");
    }
}
