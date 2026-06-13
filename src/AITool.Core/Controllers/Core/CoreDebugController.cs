using System.Security.Cryptography;
using System.Text;
using AITool.Application.CoreRuntime;
using AITool.Core.Services;
using AITool.Infrastructure.CoreRuntime;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Core.Controllers.Core;

/// <summary>
/// Core 运行时内存调试页。
/// 通过 <c>GET /debug/runtime?key=&lt;保护密钥&gt;</c> 访问，
/// 密钥哈希值在 appsettings.json 的 Debug:KeyHash 中配置。
/// 页面为自包含 HTML，展示 Core 内存中的路由、站点和并发快照，无需外部依赖。
/// </summary>
[ApiExplorerSettings(IgnoreApi = true)]
[Route("debug")]
public sealed class CoreDebugController : ControllerBase
{
    private readonly ICoreRuntimeConfigProvider _configProvider;
    private readonly ModelConcurrencyQueryService _concurrencyQuery;
    private readonly IConfiguration _configuration;

    /// <summary>
    /// 初始化 Core 调试控制器。
    /// </summary>
    public CoreDebugController(
        ICoreRuntimeConfigProvider configProvider,
        ModelConcurrencyQueryService concurrencyQuery,
        IConfiguration configuration)
    {
        _configProvider = configProvider;
        _concurrencyQuery = concurrencyQuery;
        _configuration = configuration;
    }

    /// <summary>
    /// 返回 Core 运行时内存快照调试页（自包含 HTML）。
    /// </summary>
    /// <param name="key">访问保护密钥明文，服务端对其 SHA256 哈希后与配置中的 Debug:KeyHash 比对。</param>
    [HttpGet("runtime")]
    [Produces("text/html")]
    public IActionResult RuntimeSnapshot([FromQuery] string? key)
    {
        // 验证访问密钥
        var configuredHash = _configuration["Debug:KeyHash"];
        if (string.IsNullOrWhiteSpace(configuredHash))
        {
            return Unauthorized(new { message = "调试访问密钥未配置。请在 appsettings.json 的 Debug:KeyHash 中设置密钥的 SHA256 哈希值。" });
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return Unauthorized(new { message = "缺少访问密钥。请在 URL 中提供 ?key= 参数。" });
        }

        var providedHash = ComputeSha256Hex(key);
        if (!string.Equals(providedHash, configuredHash, StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized(new { message = "访问密钥不正确。" });
        }

        var snapshot = _configProvider.GetCurrent();
        var concurrencyEntries = _concurrencyQuery.ListRecent(ModelConcurrencyQueryService.RecentRetention);

        // 构建站点字典（SiteId → Site），供路由和并发表关联站点名称和协议。
        var siteDict = snapshot?.Sites
            .ToDictionary(s => s.Id, s => s)
            ?? new Dictionary<Guid, CoreRuntimeSite>();

        var html = BuildHtml(snapshot, concurrencyEntries, siteDict);
        return Content(html, "text/html", Encoding.UTF8);
    }

    /// <summary>
    /// 计算字符串的 SHA256 十六进制哈希。
    /// </summary>
    private static string ComputeSha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 构建自包含 HTML 调试页。
    /// </summary>
    private static string BuildHtml(
        CoreRuntimeConfigSnapshot? snapshot,
        IReadOnlyList<ActiveModelConcurrencyEntry> concurrencyEntries,
        Dictionary<Guid, CoreRuntimeSite> siteDict)
    {
        var updatedAt = DateTimeOffset.UtcNow;
        var configVersion = snapshot?.ConfigVersion ?? 0L;
        var configHash = snapshot?.ConfigHash ?? "-";

        var routesJson = SerializeRoutes(snapshot, siteDict);
        var sitesJson = SerializeSites(snapshot);
        var concurrencyJson = SerializeConcurrency(concurrencyEntries, siteDict);

        return $@"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Core 运行时内存快照</title>
<style>
    *,*::before,*::after{{box-sizing:border-box;margin:0;padding:0}}
    body{{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background:#f5f6fa;color:#2d3436;line-height:1.5}}
    .page{{max-width:1400px;margin:0 auto;padding:24px 20px}}
    .header{{display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:12px;margin-bottom:24px;padding:20px 24px;background:#fff;border-radius:12px;box-shadow:0 1px 3px rgba(0,0,0,0.06)}}
    .header-left h1{{font-size:20px;font-weight:700;margin-bottom:4px}}
    .header-meta{{display:flex;gap:20px;font-size:13px;color:#636e72}}
    .header-meta span{{display:flex;align-items:center;gap:4px}}
    .header-meta strong{{color:#2d3436}}
    .btn{{display:inline-flex;align-items:center;gap:6px;padding:8px 18px;border:none;border-radius:8px;font-size:14px;font-weight:600;cursor:pointer;transition:background 0.2s,opacity 0.2s}}
    .btn-primary{{background:#0984e3;color:#fff}}
    .btn-primary:hover{{background:#0773c5}}
    .btn:disabled{{opacity:0.5;cursor:not-allowed}}
    .tabs{{display:flex;gap:0;margin-bottom:0;background:#fff;border-radius:12px 12px 0 0;box-shadow:0 1px 3px rgba(0,0,0,0.06);overflow:hidden}}
    .tab-btn{{flex:1;padding:14px 20px;border:none;background:#f8f9fa;font-size:14px;font-weight:600;color:#636e72;cursor:pointer;transition:background 0.15s,color 0.15s;border-bottom:3px solid transparent}}
    .tab-btn:hover{{background:#eef1f5;color:#2d3436}}
    .tab-btn.active{{background:#fff;color:#0984e3;border-bottom-color:#0984e3}}
    .panel{{display:none;background:#fff;border-radius:0 0 12px 12px;box-shadow:0 1px 3px rgba(0,0,0,0.06);overflow:hidden}}
    .panel.active{{display:block}}
    .panel-body{{padding:12px 16px}}
    .info-bar{{display:flex;align-items:center;justify-content:space-between;padding:10px 16px;background:#f8f9fa;border-bottom:1px solid #e9ecef;font-size:13px;color:#636e72}}
    .info-bar strong{{color:#2d3436}}
    table{{width:100%;border-collapse:collapse;font-size:13px}}
    th{{position:sticky;top:0;background:#f8f9fa;text-align:left;padding:10px 12px;font-weight:700;font-size:12px;color:#636e72;text-transform:uppercase;letter-spacing:0.5px;border-bottom:2px solid #e9ecef;white-space:nowrap}}
    td{{padding:9px 12px;border-bottom:1px solid #f1f2f6}}
    tr:hover td{{background:#f8fbff}}
    .badge{{display:inline-block;padding:2px 8px;border-radius:10px;font-size:11px;font-weight:600;white-space:nowrap}}
    .badge-on{{background:#d4edda;color:#155724}}
    .badge-off{{background:#f8d7da;color:#721c24}}
    .badge-openai{{background:#d6eaf8;color:#1a5276}}
    .badge-anthropic{{background:#f5eef8;color:#6c3483}}
    .badge-mixed{{background:#fdebd0;color:#935116}}
    .badge-allday{{background:#e8f8f5;color:#0e6655}}
    .badge-available{{background:#d5f5e3;color:#1e8449}}
    .badge-unavailable{{background:#fadbd8;color:#922b21}}
    .masked{{font-family:'SFMono-Regular',Consolas,monospace;font-size:12px;color:#636e72}}
    .concurrency-active{{color:#e74c3c;font-weight:700}}
    .concurrency-idle{{color:#b2bec3}}
    .empty-state{{text-align:center;padding:48px 20px;color:#b2bec3;font-size:14px}}
    .loading-spin{{display:inline-block;width:14px;height:14px;border:2px solid #dfe6e9;border-top-color:#0984e3;border-radius:50%;animation:spin 0.6s linear infinite;margin-right:6px;vertical-align:middle}}
    @keyframes spin{{to{{transform:rotate(360deg)}}}}
    .table-wrap{{max-height:70vh;overflow-y:auto}}
    .priority-num{{display:inline-block;width:22px;height:22px;line-height:22px;text-align:center;background:#e8f0fe;color:#0d6efd;border-radius:50%;font-size:11px;font-weight:700}}
    .text-mono{{font-family:'SFMono-Regular',Consolas,monospace;font-size:12px}}
    .col-priority{{text-align:center}}
    .col-num{{text-align:center;width:40px}}
</style>
</head>
<body>
<div class=""page"">
<div class=""header"">
    <div class=""header-left"">
        <h1>Core 运行时内存快照</h1>
        <div class=""header-meta"">
            <span>配置版本 <strong id=""configVersion"">{configVersion}</strong></span>
            <span>哈希 <strong class=""text-mono"" id=""configHash"">{EscapeHtml(TruncateHash(configHash))}</strong></span>
            <span>快照时间 <strong id=""snapshotTime"">{updatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}</strong></span>
        </div>
    </div>
    <button class=""btn btn-primary"" id=""refreshBtn"" onclick=""refreshData()"">
        <span id=""refreshIcon"">↻</span> 手动刷新
    </button>
</div>

<div class=""tabs"">
    <button class=""tab-btn active"" data-tab=""routes"" onclick=""switchTab('routes')"">路由规则 <span id=""routeCount""></span></button>
    <button class=""tab-btn"" data-tab=""sites"" onclick=""switchTab('sites')"">站点 <span id=""siteCount""></span></button>
    <button class=""tab-btn"" data-tab=""concurrency"" onclick=""switchTab('concurrency')"">当前并发 <span id=""concurrencyCount""></span></button>
</div>

<div class=""panel active"" id=""panel-routes"">
    <div class=""info-bar"">按优先级排序的运行时路由表，<strong>顺序即为请求时依次尝试的候选实例顺序</strong></div>
    <div class=""table-wrap""><table><thead><tr>
        <th class=""col-num"">#</th><th>外部模型</th><th>上游模型</th><th>站点</th><th>站点模型</th><th>协议</th><th>优先级</th><th>启停</th><th>时间策略</th>
    </tr></thead><tbody id=""routesBody""></tbody></table></div>
</div>

<div class=""panel"" id=""panel-sites"">
    <div class=""info-bar"">Core 内存中当前生效的站点快照</div>
    <div class=""table-wrap""><table><thead><tr>
        <th class=""col-num"">#</th><th>站点名称</th><th>协议</th><th>Base URL</th><th>ApiKey</th><th>路径模式</th><th>启停</th>
    </tr></thead><tbody id=""sitesBody""></tbody></table></div>
</div>

<div class=""panel"" id=""panel-concurrency"">
    <div class=""info-bar"">最近 6 小时内出现过的模型并发记录；<strong class=""concurrency-active"">红色</strong> 表示当前有活跃调用</div>
    <div class=""table-wrap""><table><thead><tr>
        <th class=""col-num"">#</th><th>模型</th><th>站点</th><th>活跃数</th><th>上限</th><th>排队数</th><th>最后活跃</th>
    </tr></thead><tbody id=""concurrencyBody""></tbody></table></div>
</div>
</div>

<script>
var _routes = {routesJson};
var _sites = {sitesJson};
var _concurrency = {concurrencyJson};

function escapeHtml(v){{ return (v||'').replace(/[&<>""']/g,function(c){{return c==='&'?'&amp;':c==='<'?'&lt;':c==='>'?'&gt;':c==='""'?'&quot;':c==='\\''?'&#39;':c}}) }}

function renderRoutes() {{
    var tbody = document.getElementById('routesBody');
    document.getElementById('routeCount').textContent = _routes.length ? ' (' + _routes.length + ')' : '';
    if (!_routes.length) {{ tbody.innerHTML = '<tr><td colspan=""9"" class=""empty-state"">暂无路由规则</td></tr>'; return; }}
    tbody.innerHTML = _routes.map(function(r,i) {{
        var protoBadge = r.protocol === 'OpenAI' ? 'badge-openai' : r.protocol === 'Anthropic' ? 'badge-anthropic' : 'badge-mixed';
        var availBadge = r.availability === 'AllDay' ? 'badge-allday' : r.availability === 'AvailableOnly' ? 'badge-available' : 'badge-unavailable';
        var availLabel = r.availability === 'AllDay' ? '全天' : r.availability === 'AvailableOnly' ? '可用时段' : '不可用时段';
        return '<tr>' +
            '<td class=""col-num""><span class=""priority-num"">' + (i+1) + '</span></td>' +
            '<td><span class=""text-mono"">' + escapeHtml(r.externalModel) + '</span></td>' +
            '<td><span class=""text-mono"">' + escapeHtml(r.upstreamModel) + '</span></td>' +
            '<td>' + escapeHtml(r.siteName) + '</td>' +
            '<td><span class=""text-mono"">' + escapeHtml(r.siteModel) + '</span></td>' +
            '<td><span class=""badge ' + protoBadge + '"">' + escapeHtml(r.protocol) + '</span></td>' +
            '<td class=""col-priority""><span class=""text-mono"">' + r.modelPriority + '/' + r.instancePriority + '/' + r.priority + '</span></td>' +
            '<td>' + (r.isEnabled ? '<span class=""badge badge-on"">启用</span>' : '<span class=""badge badge-off"">禁用</span>') + '</td>' +
            '<td><span class=""badge ' + availBadge + '"">' + availLabel + '</span></td>' +
        '</tr>';
    }}).join('');
}}

function renderSites() {{
    var tbody = document.getElementById('sitesBody');
    document.getElementById('siteCount').textContent = _sites.length ? ' (' + _sites.length + ')' : '';
    if (!_sites.length) {{ tbody.innerHTML = '<tr><td colspan=""7"" class=""empty-state"">暂无站点</td></tr>'; return; }}
    tbody.innerHTML = _sites.map(function(s,i) {{
        var protoBadge = s.protocol === 'OpenAI' ? 'badge-openai' : s.protocol === 'Anthropic' ? 'badge-anthropic' : 'badge-mixed';
        return '<tr>' +
            '<td class=""col-num"">' + (i+1) + '</td>' +
            '<td><strong>' + escapeHtml(s.name) + '</strong></td>' +
            '<td><span class=""badge ' + protoBadge + '"">' + escapeHtml(s.protocol) + '</span></td>' +
            '<td><span class=""text-mono"">' + escapeHtml(s.baseUrl) + '</span></td>' +
            '<td><span class=""masked"">' + escapeHtml(s.apiKeyMasked) + '</span></td>' +
            '<td><span class=""text-mono"">' + escapeHtml(s.endpointPathMode) + '</span></td>' +
            '<td>' + (s.isEnabled ? '<span class=""badge badge-on"">启用</span>' : '<span class=""badge badge-off"">禁用</span>') + '</td>' +
        '</tr>';
    }}).join('');
}}

function renderConcurrency() {{
    var tbody = document.getElementById('concurrencyBody');
    document.getElementById('concurrencyCount').textContent = _concurrency.length ? ' (' + _concurrency.length + ')' : '';
    if (!_concurrency.length) {{ tbody.innerHTML = '<tr><td colspan=""7"" class=""empty-state"">暂无并发记录</td></tr>'; return; }}
    tbody.innerHTML = _concurrency.map(function(c,i) {{
        var activeClass = c.activeCount > 0 ? 'concurrency-active' : 'concurrency-idle';
        var maxText = c.maxConcurrency > 0 ? String(c.maxConcurrency) : '不限';
        return '<tr>' +
            '<td class=""col-num"">' + (i+1) + '</td>' +
            '<td><span class=""text-mono"">' + escapeHtml(c.modelName) + '</span></td>' +
            '<td>' + escapeHtml(c.siteName || '-') + '</td>' +
            '<td class=""' + activeClass + '"">' + c.activeCount + '</td>' +
            '<td>' + maxText + '</td>' +
            '<td>' + c.queueCount + '</td>' +
            '<td><span class=""text-mono"">' + escapeHtml(c.lastSeenAt) + '</span></td>' +
        '</tr>';
    }}).join('');
}}

function switchTab(tab) {{
    document.querySelectorAll('.tab-btn').forEach(function(b){{ b.classList.toggle('active', b.dataset.tab === tab) }});
    document.querySelectorAll('.panel').forEach(function(p){{ p.classList.toggle('active', p.id === 'panel-' + tab) }});
}}

function refreshData() {{
    var btn = document.getElementById('refreshBtn');
    var icon = document.getElementById('refreshIcon');
    btn.disabled = true;
    icon.innerHTML = '<span class=""loading-spin""></span>';
    var url = new URL(window.location.href);
    fetch(url.toString(), {{ headers: {{ 'X-Requested-With': 'XMLHttpRequest' }}, cache: 'no-store' }})
        .then(function(r) {{ if(!r.ok){{ location.reload(); return; }} return r.text() }})
        .then(function(html) {{
            if (!html) return;
            var parser = new DOMParser();
            var doc = parser.parseFromString(html, 'text/html');
            var newRoutes = doc.querySelector('#routesData');
            var newSites = doc.querySelector('#sitesData');
            var newConcurrency = doc.querySelector('#concurrencyData');
            if (newRoutes) {{ _routes = JSON.parse(newRoutes.textContent); renderRoutes(); }}
            if (newSites) {{ _sites = JSON.parse(newSites.textContent); renderSites(); }}
            if (newConcurrency) {{ _concurrency = JSON.parse(newConcurrency.textContent); renderConcurrency(); }}
            var verEl = doc.getElementById('configVersion');
            var hashEl = doc.getElementById('configHash');
            var timeEl = doc.getElementById('snapshotTime');
            if (verEl) document.getElementById('configVersion').textContent = verEl.textContent;
            if (hashEl) document.getElementById('configHash').textContent = hashEl.textContent;
            if (timeEl) document.getElementById('snapshotTime').textContent = timeEl.textContent;
        }})
        .catch(function(){{ location.reload() }})
        .finally(function(){{ btn.disabled = false; icon.textContent = '↻' }});
}}

renderRoutes();
renderSites();
renderConcurrency();
</script>

<script type=""application/json"" id=""routesData"">{routesJson}</script>
<script type=""application/json"" id=""sitesData"">{sitesJson}</script>
<script type=""application/json"" id=""concurrencyData"">{concurrencyJson}</script>
</body>
</html>";
    }

    /// <summary>
    /// 序列化路由数据为 JSON 数组，关联站点名称和协议。
    /// </summary>
    private static string SerializeRoutes(
        CoreRuntimeConfigSnapshot? snapshot,
        Dictionary<Guid, CoreRuntimeSite> siteDict)
    {
        var rules = snapshot?.RouteRules ?? [];
        var items = rules.Select((rule, index) =>
        {
            siteDict.TryGetValue(rule.SiteId, out var site);
            return new
            {
                index,
                externalModel = rule.ExternalModelName,
                upstreamModel = rule.UpstreamModelName,
                siteName = site?.Name ?? $"(未知站点 {rule.SiteId})",
                siteModel = rule.SiteModelName,
                protocol = ResolveProtocol(site),
                priority = rule.Priority,
                modelPriority = rule.ModelPriority,
                instancePriority = rule.InstancePriority,
                isEnabled = rule.IsEnabled,
                availability = string.IsNullOrWhiteSpace(rule.AvailabilityMode) ? "AllDay" : rule.AvailabilityMode
            };
        });

        return System.Text.Json.JsonSerializer.Serialize(items);
    }

    /// <summary>
    /// 序列化站点数据为 JSON 数组，ApiKey 脱敏处理。
    /// </summary>
    private static string SerializeSites(CoreRuntimeConfigSnapshot? snapshot)
    {
        var sites = snapshot?.Sites ?? [];
        var items = sites.Select(site => new
        {
            name = site.Name,
            protocol = ResolveProtocol(site),
            baseUrl = site.BaseUrl,
            apiKeyMasked = MaskSecret(site.ApiKey),
            endpointPathMode = site.EndpointPathMode,
            isEnabled = site.IsEnabled
        });

        return System.Text.Json.JsonSerializer.Serialize(items);
    }

    /// <summary>
    /// 序列化并发数据为 JSON 数组，关联站点名称。
    /// </summary>
    private static string SerializeConcurrency(
        IReadOnlyList<ActiveModelConcurrencyEntry> entries,
        Dictionary<Guid, CoreRuntimeSite> siteDict)
    {
        var items = entries.Select(entry =>
        {
            siteDict.TryGetValue(entry.SiteId, out var site);
            return new
            {
                modelName = entry.SiteModelName,
                siteName = site?.Name ?? "-",
                activeCount = entry.ActiveCount,
                maxConcurrency = entry.MaxConcurrency,
                queueCount = entry.QueueCount,
                lastSeenAt = entry.LastSeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            };
        });

        return System.Text.Json.JsonSerializer.Serialize(items);
    }

    /// <summary>
    /// 根据站点能力解析协议标签。
    /// </summary>
    private static string ResolveProtocol(CoreRuntimeSite? site)
    {
        if (site is null) return "-";
        if (site.SupportsOpenAi && site.SupportsAnthropic) return "OpenAI+Anthropic";
        if (site.SupportsOpenAi) return "OpenAI";
        if (site.SupportsAnthropic) return "Anthropic";
        return site.ProtocolType ?? "-";
    }

    /// <summary>
    /// 对敏感字符串脱敏：保留前 3 个和后 3 个字符，中间用 *** 替代。
    /// 长度不足 7 的字符串整体替换为 ***。
    /// </summary>
    private static string MaskSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        if (value.Length <= 6) return "***";
        return value[..3] + "***" + value[^3..];
    }

    /// <summary>
    /// 截断哈希字符串用于展示。
    /// </summary>
    private static string TruncateHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return "-";
        return hash.Length <= 16 ? hash : hash[..8] + "..." + hash[^8..];
    }

    /// <summary>
    /// HTML 转义。
    /// </summary>
    private static string EscapeHtml(string value)
    {
        return (value ?? "")
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }
}
