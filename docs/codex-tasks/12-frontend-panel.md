# T12 — 前端账号管理面板

> 状态：已完成 ✅
> 前置依赖：T11（管理 API）
> 关联总览章节：横切性能原则（前端相关）

## 实施记录

- 新建 `src/AITool.Web/Pages/Admin/Codex/Index.cshtml.cs`（薄壳 PageModel，[Authorize]，OnGet 无服务端数据）。
- 新建 `src/AITool.Web/Pages/Admin/Codex/Index.cshtml`：
  - 顶部按钮组（新增 OAuth / 上传凭证）。
  - 每账号卡片：名称 + PlanType 徽章 + 状态徽章（正常/冷却中·恢复时间/已禁用）+ email；额度行（剩余/单位/上次检查/token 过期）；操作按钮组（刷新额度/重置额度/禁用·启用/编辑/拉取模型/刷新Token/删除）。
  - OAuth Modal：开始登录→显示授权 URL（复制/打开）+ 步骤提示（含「无法访问 localhost:1455 是正常的」说明）+ 粘贴回调 URL → 完成。
  - 导入 Modal：多文件选择 + JSON 文本粘贴。
  - 编辑 Modal：显示名 + 自动禁用阈值。
  - 重置额度/删除/刷新Token 均 `confirm()` 二次确认，文案明确警示。
  - escapeHtml 防 XSS；formatTime；按钮点击 disable 防重复。
- `_Layout.cshtml`：资源管理区加「🤖 Codex 账号」导航（站点管理之后）。
- 编译通过。

## 目标

新建 `Admin/Codex` Razor 页面，提供 Codex 账号管理面板：顶部按钮组（新增 OAuth 登录 / 上传凭证）、每个账号一个卡片面板（额度/状态/PlanType/操作）、OAuth 弹窗（粘贴回调 URL）、凭证上传弹窗、二次确认（重置/删除）。侧边栏加导航。

**注意**：AITool 是 Razor Pages + 原生 JS + Bootstrap 5（非 SPA）。`management.html#/oauth` 等是 new-api 的 React 哈希路由，不适用。

---

## 涉及文件

| 文件 | 操作 |
| --- | --- |
| `src/AITool.Web/Pages/Admin/Codex/Index.cshtml` | 新建页面 |
| `src/AITool.Web/Pages/Admin/Codex/Index.cshtml.cs` | 新建 PageModel |
| `src/AITool.Web/Pages/Shared/_Layout.cshtml` | 侧边栏加导航项 |

参考模板：`Pages/Admin/Sites/Index.cshtml`（顶部按钮、Bootstrap Modal、原生 fetch、confirm 模式）。

---

## 详细步骤

### 1. PageModel `Index.cshtml.cs`

```csharp
public class IndexModel : PageModel
{
    // 薄壳：仅返回是否登录等基础数据。账号列表由前端 fetch /api/admin/codex/accounts 拿（动态刷新友好）
    public void OnGet() { }
}
```

> 账号列表用前端 fetch 动态加载（便于额度刷新局部更新，避免整页刷新）。

### 2. 页面结构 `Index.cshtml`

```html
@page
@model AITool.Web.Pages.Admin.Codex.IndexModel
@{ ViewData["Title"] = "Codex 账号"; }

<div class="page-header">
    <div>
        <h2 class="page-title">Codex 账号</h2>
        <p class="page-subtitle">管理 Codex OAuth 登录账号、凭证导入、额度与自动禁用</p>
    </div>
    <div class="d-flex gap-2">
        <button class="btn btn-primary" onclick="openOAuthModal()">＋ 新增 Codex OAuth 登录</button>
        <button class="btn btn-outline-primary" onclick="openImportModal()">上传凭证</button>
    </div>
</div>

<div id="accountsContainer" class="codex-account-list">
    <!-- 账号卡片由 JS 动态渲染 -->
    <div class="table-empty">加载中...</div>
</div>

<!-- OAuth Modal -->
<div class="modal fade" id="oauthModal" tabindex="-1"> ... 粘贴回调 URL 流程 ... </div>
<!-- Import Modal -->
<div class="modal fade" id="importModal" tabindex="-1"> ... 文件选择/粘贴 JSON ... </div>

@section Scripts { <script> /* 见下方 JS */ </script> }
```

### 3. 账号卡片（每个账号一个面板）

每卡片含：
- **头部**：DisplayName + PlanType 徽章（free/plus/team/pro 颜色区分）+ 状态徽章（正常/冷却中[显示恢复时间]/已禁用[手动/自动]）。
- **额度区**：剩余/已用额度（若有）+ 上次检查时间 + 「刷新额度」按钮。
- **操作区**：
  - 重置额度（`onclick="return confirm('确认重置该账号额度？这将清除冷却状态并刷新 token，重新参与转发。上游真实额度恢复时间由 OpenAI 决定。')"`）
  - 启用/禁用（toggle）
  - 编辑（改 DisplayName / AutoDisableThreshold，小弹窗或行内）
  - 拉取模型
  - 刷新 Token
  - 删除（`onclick="return confirm('确认删除该 Codex 账号？将同时删除关联的隐藏站点、模型映射和路由规则，不可恢复。')"`）

### 4. JS：加载与渲染

```js
let codexAccounts = [];

async function loadAccounts() {
    const r = await fetch('/api/admin/codex/accounts');
    if (!r.ok) { alert('加载账号失败'); return; }
    codexAccounts = await r.json();
    renderAccounts();
}

function renderAccounts() {
    const container = document.getElementById('accountsContainer');
    if (codexAccounts.length === 0) {
        container.innerHTML = '<div class="table-empty">暂无 Codex 账号，点击右上角新增或上传凭证</div>';
        return;
    }
    container.innerHTML = codexAccounts.map(a => renderCard(a)).join('');
}

function renderCard(a) {
    const statusBadge = renderStatusBadge(a);
    return `
    <div class="codex-account-card" data-id="${a.id}">
        <div class="card-header-row">
            <span class="account-name">${escapeHtml(a.displayName)}</span>
            ${renderPlanBadge(a.planType)}
            ${statusBadge}
        </div>
        <div class="card-quota-row">
            <span>剩余额度：${a.remainingQuota ?? '—'} ${a.quotaUnit ?? ''}</span>
            <span>上次检查：${formatTime(a.lastQuotaCheckedAt) ?? '—'}</span>
            <button class="btn btn-sm btn-outline-secondary" onclick="refreshQuota('${a.id}')">刷新额度</button>
        </div>
        <div class="card-actions-row">
            <button class="btn btn-sm btn-outline-warning" onclick="resetQuota('${a.id}')">重置额度</button>
            <button class="btn btn-sm btn-outline-secondary" onclick="toggleAccount('${a.id}')">${a.isEnabled ? '禁用' : '启用'}</button>
            <button class="btn btn-sm btn-outline-primary" onclick="editAccount('${a.id}')">编辑</button>
            <button class="btn btn-sm btn-outline-info" onclick="pullModels('${a.id}')">拉取模型</button>
            <button class="btn btn-sm btn-outline-danger" onclick="deleteAccount('${a.id}')">删除</button>
        </div>
    </div>`;
}
```

### 5. OAuth 登录流程（手动粘贴回调 URL）

```js
let pendingState = null;

async function openOAuthModal() {
    const modal = new bootstrap.Modal(document.getElementById('oauthModal'));
    modal.show();
}

async function startOAuth() {
    const r = await fetch('/api/admin/codex/start-oauth', { method: 'POST' });
    const data = await r.json();
    pendingState = data.state;
    // 显示授权 URL（可复制/点击新标签打开）
    document.getElementById('oauthUrl').value = data.url;
    document.getElementById('oauthUrlBox').style.display = 'block';
    document.getElementById('callbackInput').style.display = 'block';
}

async function completeOAuth() {
    const callbackUrl = document.getElementById('callbackUrlInput').value.trim();
    if (!callbackUrl) { alert('请粘贴登录后浏览器跳转到的完整 URL'); return; }
    const r = await fetch('/api/admin/codex/complete-oauth', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ callbackUrl })
    });
    if (!r.ok) { const e = await r.json(); alert(e.message || '登录失败'); return; }
    bootstrap.Modal.getInstance(document.getElementById('oauthModal')).hide();
    await loadAccounts();
}
```

> **关键文案**：OAuth Modal 内明确指引：「① 点击上方链接在新标签登录 OpenAI；② 登录后浏览器会跳转到 `localhost:1455/...`（页面会显示无法访问，这是正常的）；③ 复制浏览器地址栏的完整 URL 粘贴到下方输入框；④ 点击完成。」

### 6. 上传凭证流程

```js
async function submitImport() {
    const fileInput = document.getElementById('credentialFile');
    const textInput = document.getElementById('credentialText').value.trim();
    if (fileInput.files.length > 0) {
        const fd = new FormData();
        for (const f of fileInput.files) fd.append('files', f);
        const r = await fetch('/api/admin/codex/import-credential', { method: 'POST', body: fd });
        return handleImportResult(r);
    }
    if (textInput) {
        const r = await fetch('/api/admin/codex/import-credential?name=imported.json', {
            method: 'POST', headers: { 'Content-Type': 'application/json' }, body: textInput
        });
        return handleImportResult(r);
    }
    alert('请选择文件或粘贴 JSON');
}
```

### 7. 操作函数（带 confirm）

```js
async function resetQuota(id) {
    if (!confirm('确认重置该账号额度？这将清除冷却状态并刷新 token，重新参与转发。注意：上游真实额度恢复时间由 OpenAI 决定，重置不会凭空增加额度。')) return;
    const r = await fetch(`/api/admin/codex/accounts/${id}/reset-quota`, { method: 'POST' });
    if (!r.ok) { alert('重置失败'); return; }
    await loadAccounts();
}

async function deleteAccount(id) {
    if (!confirm('确认删除该 Codex 账号？将同时删除关联的隐藏站点、模型映射和路由规则，不可恢复。')) return;
    const r = await fetch(`/api/admin/codex/accounts/${id}`, { method: 'DELETE' });
    if (!r.ok) { alert('删除失败'); return; }
    await loadAccounts();
}

async function refreshQuota(id) {
    const btn = event.target; btn.disabled = true;
    const r = await fetch(`/api/admin/codex/accounts/${id}/refresh-quota`, { method: 'POST' });
    btn.disabled = false;
    if (!r.ok) { alert('额度查询失败'); return; }
    await loadAccounts();  // 或局部更新单卡片
}

async function toggleAccount(id) { /* POST toggle */ }
async function pullModels(id) { /* POST pull-models，confirm 前提示 */ }
async function editAccount(id) { /* 小弹窗改名称/阈值，PUT */ }
```

### 8. 侧边栏导航

`_Layout.cshtml`（约 56-64 行区，现有导航项附近）加：

```html
<a class="nav-link" asp-page="/Admin/Codex/Index">Codex 账号</a>
```

### 9. 页面加载

```js
document.addEventListener('DOMContentLoaded', loadAccounts);
```

---

## 性能考量

### 引用原则
- 列表单次 fetch；额度刷新局部更新（非整页）。

### 本任务特有
- **局部更新**：`refreshQuota` 后可只更新对应卡片的额度 DOM，而非 `loadAccounts()` 全量重渲染。实现时优先全量（简单，账号少），若体验不佳再优化局部。账号卡片量级小（几十），全量 innerHTML 可忽略。
- **轮询/防抖**：额度自动刷新（若加）用 `setInterval` 但加可见性检测（`document.hidden` 时不轮询）。本期建议纯手动刷新，避免无谓请求。
- **escapeHtml**：DisplayName/Email 等用户输入渲染时转义，防 XSS。
- **原生 fetch**：无 axios 等依赖，与现有页面一致。
- **Modal 复用**：用现有 Bootstrap Modal 模式（参考 Sites 的 `#modelSelectModal`）。

---

## 验收标准

1. 侧边栏出现「Codex 账号」入口，点击进入面板。
2. 新增 OAuth → 弹窗显示授权 URL → 粘贴回调 URL → 完成后账号出现在列表。
3. 上传凭证 → 多文件/单文件/粘贴文本 → 导入成功账号出现。
4. 每账号卡片显示名称/PlanType/状态/额度/操作。
5. 刷新额度按钮更新额度数字。
6. 重置额度/删除有二次 confirm。
7. 启用/禁用切换生效，状态徽章更新。
8. 编辑名称/阈值生效。
9. 拉取模型成功后模型进 Models/Routes（验证于 T13）。

---

## 风险

- **OAuth 流程用户困惑**：粘贴回调 URL 流程对非技术用户不直观。Modal 文案必须清晰（含「无法访问页面是正常的」提示）。
- **状态徽章一致性**：冷却中 + 手动禁用 + 自动禁用（额度不足）三种状态需清晰区分，避免用户困惑为何账号不可用。
- **额度数字缺失**：若 T09 上游端点不可用，额度区显示「—」，需文案说明「额度数字需上游支持」。
- **XSS**：所有动态内容 escapeHtml。
- **并发操作**：用户快速连点按钮可能并发请求。按钮点击后 disable（见 refreshQuota），防重复。
