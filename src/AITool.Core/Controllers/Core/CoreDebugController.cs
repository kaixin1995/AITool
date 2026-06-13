using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    public CoreDebugController(
        ICoreRuntimeConfigProvider configProvider,
        ModelConcurrencyQueryService concurrencyQuery,
        IConfiguration configuration)
    {
        _configProvider = configProvider;
        _concurrencyQuery = concurrencyQuery;
        _configuration = configuration;
    }

    [HttpGet("runtime")]
    [Produces("text/html")]
    public IActionResult RuntimeSnapshot([FromQuery] string? key)
    {
        var configuredHash = _configuration["Debug:KeyHash"];
        if (string.IsNullOrWhiteSpace(configuredHash))
        {
            return Content("<h1>未配置密钥</h1><p>请在 appsettings.json 的 Debug:KeyHash 中设置密钥的 SHA256 哈希值。</p>", "text/html", Encoding.UTF8);
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return Content("<h1>缺少密钥</h1><p>请在 URL 中提供 ?key= 参数。</p>", "text/html", Encoding.UTF8);
        }

        var providedHash = ComputeSha256Hex(key);
        if (!string.Equals(providedHash, configuredHash, StringComparison.OrdinalIgnoreCase))
        {
            return Content("<h1>密钥错误</h1>", "text/html", Encoding.UTF8);
        }

        var snapshot = _configProvider.GetCurrent();
        var concurrencyEntries = _concurrencyQuery.ListRecent(ModelConcurrencyQueryService.RecentRetention);

        var siteDict = snapshot?.Sites
            .ToDictionary(s => s.Id, s => s)
            ?? new Dictionary<Guid, CoreRuntimeSite>();

        var html = BuildHtml(snapshot, concurrencyEntries, siteDict);
        return Content(html, "text/html", Encoding.UTF8);
    }

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

    private static string BuildHtml(
        CoreRuntimeConfigSnapshot? snapshot,
        IReadOnlyList<ActiveModelConcurrencyEntry> concurrencyEntries,
        Dictionary<Guid, CoreRuntimeSite> siteDict)
    {
        var updatedAt = DateTimeOffset.UtcNow;
        var configVersion = snapshot?.ConfigVersion ?? 0L;
        var configHash = snapshot?.ConfigHash ?? "-";
        var truncatedHash = TruncateHash(configHash);
        var timeText = updatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        var routesJson = SerializeRoutes(snapshot, siteDict);
        var sitesJson = SerializeSites(snapshot);
        var concurrencyJson = SerializeConcurrency(concurrencyEntries, siteDict);

        var sb = new StringBuilder();
        sb.Append(HtmlHead(configVersion, truncatedHash, timeText));
        sb.Append(HtmlBody(configVersion, truncatedHash, timeText));
        sb.Append(HtmlScript(routesJson, sitesJson, concurrencyJson));
        return sb.ToString();
    }

    private static string HtmlHead(long configVersion, string configHash, string timeText)
    {
        return @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Core 运行时内存快照</title>
<style>
*,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background:#f5f6fa;color:#2d3436;line-height:1.5}
.page{max-width:1400px;margin:0 auto;padding:24px 20px}
.header{display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:12px;margin-bottom:24px;padding:20px 24px;background:#fff;border-radius:12px;box-shadow:0 1px 3px rgba(0,0,0,0.06)}
.header-left h1{font-size:20px;font-weight:700;margin-bottom:4px}
.header-meta{display:flex;gap:20px;font-size:13px;color:#636e72}
.header-meta span{display:flex;align-items:center;gap:4px}
.header-meta strong{color:#2d3436}
.btn{display:inline-flex;align-items:center;gap:6px;padding:8px 18px;border:none;border-radius:8px;font-size:14px;font-weight:600;cursor:pointer;transition:background 0.2s,opacity 0.2s}
.btn-primary{background:#0984e3;color:#fff}
.btn-primary:hover{background:#0773c5}
.btn:disabled{opacity:0.5;cursor:not-allowed}
.tabs{display:flex;gap:0;margin-bottom:0;background:#fff;border-radius:12px 12px 0 0;box-shadow:0 1px 3px rgba(0,0,0,0.06);overflow:hidden}
.tab-btn{flex:1;padding:14px 20px;border:none;background:#f8f9fa;font-size:14px;font-weight:600;color:#636e72;cursor:pointer;transition:background 0.15s,color 0.15s;border-bottom:3px solid transparent}
.tab-btn:hover{background:#eef1f5;color:#2d3436}
.tab-btn.active{background:#fff;color:#0984e3;border-bottom-color:#0984e3}
.panel{display:none;background:#fff;border-radius:0 0 12px 12px;box-shadow:0 1px 3px rgba(0,0,0,0.06);overflow:hidden}
.panel.active{display:block}
.info-bar{display:flex;align-items:center;justify-content:space-between;padding:10px 16px;background:#f8f9fa;border-bottom:1px solid #e9ecef;font-size:13px;color:#636e72}
.info-bar strong{color:#2d3436}
table{width:100%;border-collapse:collapse;font-size:13px}
th{position:sticky;top:0;background:#f8f9fa;text-align:left;padding:10px 12px;font-weight:700;font-size:12px;color:#636e72;text-transform:uppercase;letter-spacing:0.5px;border-bottom:2px solid #e9ecef;white-space:nowrap}
td{padding:9px 12px;border-bottom:1px solid #f1f2f6}
tr:hover td{background:#f8fbff}
.badge{display:inline-block;padding:2px 8px;border-radius:10px;font-size:11px;font-weight:600;white-space:nowrap}
.badge-on{background:#d4edda;color:#155724}
.badge-off{background:#f8d7da;color:#721c24}
.badge-openai{background:#d6eaf8;color:#1a5276}
.badge-anthropic{background:#f5eef8;color:#6c3483}
.badge-mixed{background:#fdebd0;color:#935116}
.badge-allday{background:#e8f8f5;color:#0e6655}
.badge-available{background:#d5f5e3;color:#1e8449}
.badge-unavailable{background:#fadbd8;color:#922b21}
.masked{font-family:'SFMono-Regular',Consolas,monospace;font-size:12px;color:#636e72}
.concurrency-active{color:#e74c3c;font-weight:700}
.concurrency-idle{color:#b2bec3}
.empty-state{text-align:center;padding:48px 20px;color:#b2bec3;font-size:14px}
.spin{display:inline-block;width:14px;height:14px;border:2px solid #dfe6e9;border-top-color:#0984e3;border-radius:50%;animation:spin 0.6s linear infinite;vertical-align:middle}
@keyframes spin{to{transform:rotate(360deg)}}
.table-wrap{max-height:70vh;overflow-y:auto}
.pri-num{display:inline-block;width:22px;height:22px;line-height:22px;text-align:center;background:#e8f0fe;color:#0d6efd;border-radius:50%;font-size:11px;font-weight:700}
.mono{font-family:'SFMono-Regular',Consolas,monospace;font-size:12px}
.col-pri{text-align:center}
.col-num{text-align:center;width:40px}
</style>
</head>";
    }

    private static string HtmlBody(long configVersion, string configHash, string timeText)
    {
        return $@"<body>
<div class=""page"">
<div class=""header"">
 <div class=""header-left"">
  <h1>Core 运行时内存快照</h1>
  <div class=""header-meta"">
   <span>配置版本 <strong id=""configVersion"">{configVersion}</strong></span>
   <span>哈希 <strong class=""mono"" id=""configHash"">{EscapeHtmlAttr(configHash)}</strong></span>
   <span>快照时间 <strong id=""snapshotTime"">{timeText}</strong></span>
  </div>
 </div>
 <button class=""btn btn-primary"" id=""refreshBtn"">↻ 手动刷新</button>
</div>

<div class=""tabs"">
 <button class=""tab-btn active"" data-tab=""routes"">路由规则 <span id=""routeCount""></span></button>
 <button class=""tab-btn"" data-tab=""sites"">站点 <span id=""siteCount""></span></button>
 <button class=""tab-btn"" data-tab=""concurrency"">当前并发 <span id=""concurrencyCount""></span></button>
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
</div>";
    }

    /// <summary>
    /// 使用 StringBuilder 拼接 HTML 正文和脚本部分，JSON 数据用占位替换避免 C# 插值字符串转义问题。
    /// 脚本内使用普通 @"" 字符串，零转义负担。
    /// </summary>
    private static string HtmlScript(string routesJson, string sitesJson, string concurrencyJson)
    {
        // HTML-escape JSON for embedding in <script> tag body.
        // The only dangerous sequences inside <script> are "</" (which would close the script tag)
        // and "<!--" (which triggers comment mode in legacy parsers).
        var safeRoutes = EscapeScriptData(routesJson);
        var safeSites = EscapeScriptData(sitesJson);
        var safeConcurrency = EscapeScriptData(concurrencyJson);

        return @"<script>
var R=" + safeRoutes + @";
var S=" + safeSites + @";
var C=" + safeConcurrency + @";

(function(){
var E=document.getElementById,I=document.querySelectorAll;

function es(s){var d=document.createElement('span');d.textContent=s||'';return d.innerHTML}

function renderRoutes(){
 var t=E('routesBody');
 E('routeCount').textContent=R.length?' ('+R.length+')':'';
 if(!R.length){t.innerHTML='<tr><td colspan=""9"" class=""empty-state"">暂无路由规则</td></tr>';return}
 t.innerHTML=R.map(function(r,i){
  var p=r.protocol==='OpenAI'?'badge-openai':r.protocol==='Anthropic'?'badge-anthropic':'badge-mixed';
  var ab=r.availability==='AllDay'?'badge-allday':r.availability==='AvailableOnly'?'badge-available':'badge-unavailable';
  var al=r.availability==='AllDay'?'全天':r.availability==='AvailableOnly'?'可用时段':'不可用时段';
  return '<tr>'+
   '<td class=""col-num""><span class=""pri-num"">'+(i+1)+'</span></td>'+
   '<td><span class=""mono"">'+es(r.externalModel)+'</span></td>'+
   '<td><span class=""mono"">'+es(r.upstreamModel)+'</span></td>'+
   '<td>'+es(r.siteName)+'</td>'+
   '<td><span class=""mono"">'+es(r.siteModel)+'</span></td>'+
   '<td><span class=""badge '+p+'"">'+es(r.protocol)+'</span></td>'+
   '<td class=""col-pri""><span class=""mono"">'+r.modelPriority+'/'+r.instancePriority+'/'+r.priority+'</span></td>'+
   '<td>'+(r.isEnabled?'<span class=""badge badge-on"">启用</span>':'<span class=""badge badge-off"">禁用</span>')+'</td>'+
   '<td><span class=""badge '+ab+'"">'+al+'</span></td>'+
  '</tr>';
 }).join('')
}

function renderSites(){
 var t=E('sitesBody');
 E('siteCount').textContent=S.length?' ('+S.length+')':'';
 if(!S.length){t.innerHTML='<tr><td colspan=""7"" class=""empty-state"">暂无站点</td></tr>';return}
 t.innerHTML=S.map(function(s,i){
  var p=s.protocol==='OpenAI'?'badge-openai':s.protocol==='Anthropic'?'badge-anthropic':'badge-mixed';
  return '<tr>'+
   '<td class=""col-num"">'+(i+1)+'</td>'+
   '<td><strong>'+es(s.name)+'</strong></td>'+
   '<td><span class=""badge '+p+'"">'+es(s.protocol)+'</span></td>'+
   '<td><span class=""mono"">'+es(s.baseUrl)+'</span></td>'+
   '<td><span class=""masked"">'+es(s.apiKeyMasked)+'</span></td>'+
   '<td><span class=""mono"">'+es(s.endpointPathMode)+'</span></td>'+
   '<td>'+(s.isEnabled?'<span class=""badge badge-on"">启用</span>':'<span class=""badge badge-off"">禁用</span>')+'</td>'+
  '</tr>';
 }).join('')
}

function renderConcurrency(){
 var t=E('concurrencyBody');
 E('concurrencyCount').textContent=C.length?' ('+C.length+')':'';
 if(!C.length){t.innerHTML='<tr><td colspan=""7"" class=""empty-state"">暂无并发记录</td></tr>';return}
 t.innerHTML=C.map(function(c,i){
  var ac=c.activeCount>0?'concurrency-active':'concurrency-idle';
  var mx=c.maxConcurrency>0?String(c.maxConcurrency):'不限';
  return '<tr>'+
   '<td class=""col-num"">'+(i+1)+'</td>'+
   '<td><span class=""mono"">'+es(c.modelName)+'</span></td>'+
   '<td>'+es(c.siteName||'-')+'</td>'+
   '<td class=""'+ac+'"">'+c.activeCount+'</td>'+
   '<td>'+mx+'</td>'+
   '<td>'+c.queueCount+'</td>'+
   '<td><span class=""mono"">'+es(c.lastSeenAt)+'</span></td>'+
  '</tr>';
 }).join('')
}

function sw(tab){
 I('.tab-btn').forEach(function(b){b.classList.toggle('active',b.dataset.tab===tab)});
 I('.panel').forEach(function(p){p.classList.toggle('active',p.id==='panel-'+tab)})
}

I('.tab-btn').forEach(function(b){b.addEventListener('click',function(){sw(this.dataset.tab)})});

var rb=E('refreshBtn');
if(rb){rb.addEventListener('click',function(){
 rb.disabled=true;
 rb.innerHTML='<span class=""spin""></span>';
 var u=new URL(window.location.href);
 fetch(u.toString(),{headers:{'X-Requested-With':'XMLHttpRequest'},cache:'no-store'})
  .then(function(r){if(!r.ok){location.reload();return}return r.text()})
  .then(function(t){
   if(!t)return;
   var m=t.match(/<script>var R=([\s\S]*?);var S=([\s\S]*?);var C=([\s\S]*?);/);
   if(!m)return;
   try{R=JSON.parse(m[1]);renderRoutes()}catch(e){}
   try{S=JSON.parse(m[2]);renderSites()}catch(e){}
   try{C=JSON.parse(m[3]);renderConcurrency()}catch(e){}
   var v=t.match(/id=""configVersion"">(\d+)/);
   var h=t.match(/id=""configHash"">([^<]+)/);
   var tm=t.match(/id=""snapshotTime"">([^<]+)/);
   if(v)E('configVersion').textContent=v[1];
   if(h)E('configHash').textContent=h[1];
   if(tm)E('snapshotTime').textContent=tm[1]
  })
  .catch(function(){location.reload()})
  .finally(function(){rb.disabled=false;rb.innerHTML='&#x21BB; 手动刷新'})
})}

renderRoutes();
renderSites();
renderConcurrency();
})();
</script>

<script type=""application/json"" id=""routesData"">" + safeRoutes + @"</script>
<script type=""application/json"" id=""sitesData"">" + safeSites + @"</script>
<script type=""application/json"" id=""concurrencyData"">" + safeConcurrency + @"</script>
</body>
</html>";
    }

    /// <summary>
    /// JS 代码里的 HTML 片段不需要 HTML 转义（它们通过 innerHTML 插入），
    /// 但嵌入 script 标签的 JSON 数据需要防 </ 序列。
    /// </summary>
    private static string EscapeScriptData(string json)
    {
        return json.Replace("</", "<\\/");
    }

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
                externalModel = rule.ExternalModelName,
                upstreamModel = rule.UpstreamModelName,
                siteName = site?.Name ?? "(未知站点)",
                siteModel = rule.SiteModelName,
                protocol = ResolveProtocol(site),
                priority = rule.Priority,
                modelPriority = rule.ModelPriority,
                instancePriority = rule.InstancePriority,
                isEnabled = rule.IsEnabled,
                availability = string.IsNullOrWhiteSpace(rule.AvailabilityMode) ? "AllDay" : rule.AvailabilityMode
            };
        });

        return JsonSerializer.Serialize(items);
    }

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

        return JsonSerializer.Serialize(items);
    }

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

        return JsonSerializer.Serialize(items);
    }

    private static string ResolveProtocol(CoreRuntimeSite? site)
    {
        if (site is null) return "-";
        if (site.SupportsOpenAi && site.SupportsAnthropic) return "OpenAI+Anthropic";
        if (site.SupportsOpenAi) return "OpenAI";
        if (site.SupportsAnthropic) return "Anthropic";
        return site.ProtocolType ?? "-";
    }

    private static string MaskSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        if (value.Length <= 6) return "***";
        return value[..3] + "***" + value[^3..];
    }

    private static string TruncateHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return "-";
        return hash.Length <= 16 ? hash : hash[..8] + "..." + hash[^8..];
    }

    /// <summary>
    /// HTML 属性值转义（只转双引号）。
    /// </summary>
    private static string EscapeHtmlAttr(string value)
    {
        return (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
