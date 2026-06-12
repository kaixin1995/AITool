using AITool.Application.CoreRuntime;
using AITool.Application.Operations;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Admin.Pages.Admin.Developer.Invocations;

/// <summary>
/// 开发者工具页面模型。
/// Admin 侧通过 CoreAdminClient 将所有开发者数据查询（调用追踪、并发检测、客户端模拟器元数据）
/// 代理到 Core 宿主的 /api/core/developer/* 端点，自身不直接访问 Core 运行时内存单例。
/// </summary>
public sealed class IndexModel : PageModel
{
    /// <summary>
    /// 每页记录数，与 Core 侧保持一致。
    /// </summary>
    public const int PageSize = 20;

    /// <summary>
    /// 系统运行时设置服务，用于检查开发者功能开关。
    /// </summary>
    private readonly ISystemRuntimeSettingsService _runtimeSettingsService;
    private readonly CoreAdminClient _coreClient;
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 初始化开发者工具页面模型。
    /// </summary>
    public IndexModel(
        ISystemRuntimeSettingsService runtimeSettingsService,
        CoreAdminClient coreClient,
        AppDbContext dbContext)
    {
        _runtimeSettingsService = runtimeSettingsService;
        _coreClient = coreClient;
        _dbContext = dbContext;
    }

    /// <summary>
    /// 初始总记录数，用于页面首次加载时的摘要展示。
    /// </summary>
    public int InitialTotalCount { get; private set; }

    /// <summary>
    /// 初始失败记录数。
    /// </summary>
    public int InitialFailedCount { get; private set; }

    /// <summary>
    /// 初始等待记录数。
    /// </summary>
    public int InitialPendingCount { get; private set; }

    /// <summary>
    /// 当前激活页签，默认为调用追踪。
    /// </summary>
    public string ActiveTab { get; private set; } = "invocations";

    /// <summary>
    /// 客户端模拟器的默认请求地址。
    /// Admin 侧将其设置为 Core 宿主的代理地址，使模拟请求直接发往 Core。
    /// </summary>
    public string DefaultBaseUrl { get; private set; } = string.Empty;

    /// <summary>
    /// 默认访问密钥，从 Core 的元数据端点获取。
    /// </summary>
    public string DefaultAccessKey { get; private set; } = string.Empty;

    /// <summary>
    /// 默认 OpenAI 模型名称。
    /// </summary>
    public string DefaultOpenAiModel { get; private set; } = string.Empty;

    /// <summary>
    /// 默认 Anthropic 模型名称。
    /// </summary>
    public string DefaultAnthropicModel { get; private set; } = string.Empty;

    /// <summary>
    /// 客户端模拟器可用的模型列表，从 Core 元数据端点获取。
    /// </summary>
    public List<CoreDeveloperModelItem> Models { get; private set; } = [];

    /// <summary>
    /// Core 查询失败时的页面提示。
    /// </summary>
    public string LoadErrorMessage { get; private set; } = string.Empty;

    /// <summary>
    /// 处理页面首次加载请求。
    /// 检查开发者功能开关后，从 Core 获取初始调用追踪摘要和模拟器元数据。
    /// </summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        if (!settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        ActiveTab = "invocations";

        // 并行请求初始列表和模拟器元数据，减少页面加载延迟
        try
        {
            var listTask = _coreClient.GetDeveloperInvocationsAsync(1, PageSize, cancellationToken);
            var metadataTask = _coreClient.GetDeveloperMetadataAsync(cancellationToken);

            await Task.WhenAll(listTask, metadataTask);

            var listResult = listTask.Result;
            InitialTotalCount = listResult.TotalCount;
            InitialFailedCount = listResult.FailedCount;
            InitialPendingCount = listResult.PendingCount;

            var metadata = metadataTask.Result;
            DefaultAccessKey = metadata.DefaultAccessKey;
            DefaultOpenAiModel = metadata.DefaultOpenAiModel;
            DefaultAnthropicModel = metadata.DefaultAnthropicModel;
            Models = metadata.Models;

            if (string.IsNullOrWhiteSpace(DefaultAccessKey))
            {
                DefaultAccessKey = await GetDefaultAccessKeyFromAdminAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            LoadErrorMessage = ex.GetBaseException().Message;
            DefaultAccessKey = await GetDefaultAccessKeyFromAdminAsync(cancellationToken);
        }

        // 从 CoreAdminClient 的 BaseAddress 推导默认请求地址
        DefaultBaseUrl = GetCoreBaseUrl();

        return Page();
    }

    /// <summary>
    /// 返回调用记录列表，供前端 JavaScript 通过 AJAX 调用。
    /// </summary>
    public async Task<IActionResult> OnGetListAsync(int pageNumber = 1, CancellationToken cancellationToken = default)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        if (!settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        try
        {
            var result = await _coreClient.GetDeveloperInvocationsAsync(pageNumber, PageSize, cancellationToken);
            return new JsonResult(result);
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { message = ex.GetBaseException().Message });
        }
    }

    /// <summary>
    /// 返回单条调用记录详情，供前端 JavaScript 展开卡片时 AJAX 加载。
    /// </summary>
    public async Task<IActionResult> OnGetDetailAsync(Guid traceId, CancellationToken cancellationToken = default)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        if (!settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        try
        {
            var result = await _coreClient.GetDeveloperInvocationDetailAsync(traceId, cancellationToken);
            return new JsonResult(result);
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { message = ex.GetBaseException().Message });
        }
    }

    /// <summary>
    /// 返回当前模型并发状态快照，供并发检测页签的自动刷新使用。
    /// </summary>
    public async Task<IActionResult> OnGetConcurrencyAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _runtimeSettingsService.GetOrCreateAsync(cancellationToken);
        if (!settings.DeveloperFeaturesEnabled)
        {
            return NotFound();
        }

        try
        {
            var result = await _coreClient.GetDeveloperConcurrencyAsync(cancellationToken);
            return new JsonResult(result);
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { message = ex.GetBaseException().Message });
        }
    }

    private async Task<string> GetDefaultAccessKeyFromAdminAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.ProxyAccessKeys
            .AsNoTracking()
            .Where(x => x.IsEnabled && !string.IsNullOrWhiteSpace(x.PlainKey))
            .OrderBy(x => x.KeyName)
            .Select(x => x.PlainKey)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
    }

    /// <summary>
    /// 从 CoreAdminClient 的 BaseAddress 推导 Core 的公开请求地址。
    /// 去除末尾斜杠，使客户端模拟器可以直接拼接 /v1/* 路径。
    /// </summary>
    private string GetCoreBaseUrl()
    {
        var baseAddress = _coreClient.BaseAddress;
        if (baseAddress != null)
        {
            var url = baseAddress.ToString().TrimEnd('/');
            // 如果绑定的是 0.0.0.0，替换为 127.0.0.1，使浏览器可以正常访问
            return url.Replace("://0.0.0.0:", "://127.0.0.1:");
        }

        return "http://127.0.0.1:5029";
    }
}
