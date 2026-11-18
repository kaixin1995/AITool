const fs = require('fs');
const oldP = fs.readFileSync('src/AITool.Infrastructure/Proxy/ProxyRequestMetadataCache.AdminQueries.cs', 'utf8');
const pruned = fs.readFileSync('.tp.cs', 'utf8');
const methods = ['GetChatModelsAsync','GetChatTargetsAsync','GetModelConcurrencyLimitsAsync','GetEnabledSiteNamesAsync','GetRouteEntriesAsync','GetRouteSiteInstancesAsync','GetRouteModelsAsync','GetDiscoveredSitesAsync','GetRouteRulesAsync','GetDeveloperDefaultAccessKeyAsync','GetDeveloperDebugModelsAsync'];
let out = [];
out.push('// 本分部承载 Admin 查询族方法（聊天模型/路由入口/规则/开发者视图）。');
out.push('// master 同步：方法体取 master 最新版（master 把它们留在主文件，split 挪到分部以便主文件三方融合）。');
out.push('using AITool.Application.Common;');
out.push('using AITool.Domain.Codex;');
out.push('using AITool.Domain.Models;');
out.push('using AITool.Domain.Proxy;');
out.push('using AITool.Domain.SiteCatalog;');
out.push('using AITool.Domain.Sites;');
out.push('using AITool.Infrastructure.Persistence;');
out.push('using Microsoft.Extensions.Caching.Memory;');
out.push('using Microsoft.Extensions.DependencyInjection;');
out.push('');
out.push('namespace AITool.Infrastructure.Proxy;');
out.push('');
out.push('public sealed partial class ProxyRequestMetadataCache');
out.push('{');
for (const name of methods) {
  const lines = pruned.split('\n');
  let sig = -1;
  for (let k = 0; k < lines.length; k++) {
    if ((lines[k].includes('public ') || lines[k].includes('private ')) && lines[k].includes(name + '(')) { sig = k; break; }
  }
  if (sig < 0) { console.error('MISS method ' + name); process.exit(1); }
  let start = sig;
  while (start > 0 && (lines[start-1].trim().startsWith('///') || lines[start-1].trim() === '')) start--;
  if (start > 0 && lines[start].trim() === '') start++;
  let i = sig, depth = 0, seen = false, end = -1;
  while (i < lines.length) {
    for (const ch of lines[i]) { if (ch === '{') { depth++; seen = true; } else if (ch === '}') { depth--; if (seen && depth === 0) { end = i; break; } } }
    if (end >= 0) break; i++;
  }
  out.push(...lines.slice(start, end + 1));
  out.push('');
}
out.push('}');
fs.writeFileSync('src/AITool.Infrastructure/Proxy/ProxyRequestMetadataCache.AdminQueries.cs.new', out.join('\n'));
console.log('generated lines:', out.length);
