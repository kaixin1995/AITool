using System.Security.Cryptography;
using System.Text;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.AccessKeys;

// 访问密钥视图模型
public class AccessKeyViewModel
{
    public Guid KeyId { get; set; }
    public string KeyName { get; set; } = string.Empty;
    public string MaskedValue { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
}

// 访问密钥管理页面模型
public class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 密钥列表
    public List<AccessKeyViewModel> Keys { get; set; } = [];

    // 操作结果提示
    public string? StatusMessage { get; set; }
    public bool StatusSuccess { get; set; }

    // 加载密钥列表
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Keys = await _dbContext.ProxyAccessKeys
            .OrderBy(k => k.KeyName)
            .Select(k => new AccessKeyViewModel
            {
                KeyId = k.Id,
                KeyName = k.KeyName,
                MaskedValue = k.MaskedValue,
                IsEnabled = k.IsEnabled
            })
            .ToListAsync(cancellationToken);
    }

    // 创建新的访问密钥，存储哈希值并生成掩码显示
    public async Task<IActionResult> OnPostCreateAsync(string keyName, string accessKey, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(keyName) || string.IsNullOrWhiteSpace(accessKey))
            {
                StatusMessage = "密钥名称和密钥值不能为空";
                StatusSuccess = false;
                await OnGetAsync(cancellationToken);
                return Page();
            }

            // 对密钥进行 SHA256 哈希
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(accessKey));
            var hash = Convert.ToHexString(hashBytes);

            // 生成掩码显示值，保留前4位和后4位
            var masked = accessKey.Length > 8
                ? $"{accessKey[..4]}...{accessKey[^4..]}"
                : "****";

            var key = new ProxyAccessKey
            {
                KeyName = keyName,
                AccessKeyHash = hash,
                MaskedValue = masked,
                IsEnabled = true
            };
            _dbContext.ProxyAccessKeys.Add(key);
            await _dbContext.SaveChangesAsync(cancellationToken);

            StatusMessage = $"密钥 \"{keyName}\" 创建成功（请妥善保管原始密钥，系统仅存储哈希值）";
            StatusSuccess = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败：{ex.Message}";
            StatusSuccess = false;
        }
        await OnGetAsync(cancellationToken);
        return Page();
    }

    // 切换密钥启用/禁用状态
    public async Task<IActionResult> OnPostToggleAsync(Guid keyId, CancellationToken cancellationToken)
    {
        try
        {
            var key = await _dbContext.ProxyAccessKeys.FindAsync([keyId], cancellationToken);
            if (key is null) return RedirectToPage();

            key.IsEnabled = !key.IsEnabled;
            await _dbContext.SaveChangesAsync(cancellationToken);
            StatusMessage = "密钥状态已切换";
            StatusSuccess = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败：{ex.Message}";
            StatusSuccess = false;
        }
        await OnGetAsync(cancellationToken);
        return Page();
    }

    // 删除密钥
    public async Task<IActionResult> OnPostDeleteAsync(Guid keyId, CancellationToken cancellationToken)
    {
        try
        {
            var key = await _dbContext.ProxyAccessKeys.FindAsync([keyId], cancellationToken);
            if (key is null) return RedirectToPage();

            _dbContext.ProxyAccessKeys.Remove(key);
            await _dbContext.SaveChangesAsync(cancellationToken);

            StatusMessage = "密钥已删除";
            StatusSuccess = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败：{ex.Message}";
            StatusSuccess = false;
        }
        await OnGetAsync(cancellationToken);
        return Page();
    }
}
