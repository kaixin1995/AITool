using System.Text.Json;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 兼容规则集管理控制器：对 <see cref="CompatibilityProfile"/> 做 CRUD。
/// 规则集合随路由目标一起缓存，任何写操作后都需失效缓存。
/// </summary>
[ApiController]
[Route("api/admin/compatibility-profiles")]
public sealed class CompatibilityProfilesApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;

    public CompatibilityProfilesApiController(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 列出所有规则集（按创建时间倒序），返回摘要信息（含规则数）。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var profiles = await _dbContext.CompatibilityProfiles
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);

        return Ok(profiles.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            description = p.Description,
            isEnabled = p.IsEnabled,
            ruleCount = CountRules(p.RulesJson),
            createdAt = p.CreatedAt,
            updatedAt = p.UpdatedAt
        }));
    }

    /// <summary>
    /// 取单条规则集详情（含 RulesJson 原文，供编辑回填）。
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var p = await _dbContext.CompatibilityProfiles.InSingleAsync(id);
        if (p is null) return NotFound(new { message = "规则集不存在" });

        return Ok(new
        {
            id = p.Id,
            name = p.Name,
            description = p.Description,
            rulesJson = p.RulesJson,
            isEnabled = p.IsEnabled
        });
    }

    /// <summary>
    /// 新建规则集。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ProfilePayload payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload?.Name))
        {
            return BadRequest(new { message = "名称不能为空" });
        }

        if (!TryNormalizeRulesJson(payload.RulesJson, out var rulesJson))
        {
            return BadRequest(new { message = "规则必须是有效的 JSON 数组" });
        }

        var profile = new CompatibilityProfile
        {
            Name = payload.Name.Trim(),
            Description = payload.Description?.Trim() ?? string.Empty,
            RulesJson = rulesJson,
            IsEnabled = payload.IsEnabled
        };
        await _dbContext.InsertAsync(profile, cancellationToken);
        _metadataCache.InvalidateCompatibilityProfiles();

        return Ok(new { id = profile.Id });
    }

    /// <summary>
    /// 更新规则集。
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ProfilePayload payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload?.Name))
        {
            return BadRequest(new { message = "名称不能为空" });
        }

        var profile = await _dbContext.CompatibilityProfiles.InSingleAsync(id);
        if (profile is null) return NotFound(new { message = "规则集不存在" });
        if (!TryNormalizeRulesJson(payload.RulesJson, out var rulesJson))
        {
            return BadRequest(new { message = "规则必须是有效的 JSON 数组" });
        }

        profile.Name = payload.Name.Trim();
        profile.Description = payload.Description?.Trim() ?? string.Empty;
        profile.RulesJson = rulesJson;
        profile.IsEnabled = payload.IsEnabled;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.UpdateAsync(profile, cancellationToken);
        _metadataCache.InvalidateCompatibilityProfiles();

        return Ok(new { id = profile.Id });
    }

    /// <summary>
    /// 切换启用状态。
    /// </summary>
    [HttpPost("{id:guid}/toggle")]
    public async Task<IActionResult> Toggle(Guid id, CancellationToken cancellationToken)
    {
        var profile = await _dbContext.CompatibilityProfiles.InSingleAsync(id);
        if (profile is null) return NotFound(new { message = "规则集不存在" });

        profile.IsEnabled = !profile.IsEnabled;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.UpdateAsync(profile, cancellationToken);
        _metadataCache.InvalidateCompatibilityProfiles();

        return Ok(new { id = profile.Id, isEnabled = profile.IsEnabled });
    }

    /// <summary>
    /// 删除规则集。引用了它的模型会自动变成不应用规则集（CompatibilityProfileId 为空等同不应用）。
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var profile = await _dbContext.CompatibilityProfiles.InSingleAsync(id);
        if (profile is null) return NotFound(new { message = "规则集不存在" });

        await _dbContext.DeleteAsync(profile, cancellationToken);
        _metadataCache.InvalidateCompatibilityProfiles();

        return Ok(new { id });
    }

    /// <summary>
    /// 校验并规范化 RulesJson；无效 JSON 或非数组输入由接口明确拒绝。
    /// </summary>
    private static bool TryNormalizeRulesJson(string? raw, out string normalized)
    {
        normalized = "[]";
        if (string.IsNullOrWhiteSpace(raw)) return true;
        try
        {
            var rules = JsonSerializer.Deserialize<List<CompatibilityRule>>(raw);
            if (rules is null) return false;
            normalized = JsonSerializer.Serialize(rules);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 统计规则数（解析失败返回 0）。
    /// </summary>
    private static int CountRules(string? rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson)) return 0;
        try
        {
            var rules = JsonSerializer.Deserialize<List<CompatibilityRule>>(rulesJson);
            return rules?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>
/// 规则集增改请求体。
/// </summary>
public sealed class ProfilePayload
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>规则数组 JSON，由前端结构化表单序列化而来。</summary>
    public string RulesJson { get; set; } = "[]";
    public bool IsEnabled { get; set; } = true;
}
