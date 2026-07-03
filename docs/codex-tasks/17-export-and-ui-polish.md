# Task 17: Codex 凭证导出 + UI 精细打磨

**日期**：2026-07-03  
**分支**：`feature/codex-oauth-accounts`  
**提交**：`7660954`, `e3fa632`, `dcf374a`, `e683dc8`, `562d3c8`, `66b3a44`

---

## 任务背景

用户提出多个需求：
1. **新增功能**：添加凭证导出功能（类似 CPA，支持勾选账号批量导出 JSON）
2. **修复问题**：启动时控制台出现 BrowserLink 警告
3. **UI 优化**：卡片布局混乱、按钮文字太长、导航名称不合适、进度条显示逻辑不直观
4. **紧急修复**：导出功能 JS 丢失、卡片布局糟糕

---

## 1. 修复 BrowserLink 警告

### 问题描述

启动开发环境时控制台反复输出警告：
```
warn: Microsoft.WebTools.BrowserLink.Net.BrowserLinkMiddleware[4]
      Unable to configure Browser Link script injection on the response. 
      This may have been caused by the response's Content-Encoding: 'br'. 
      Consider disabling response compression.
```

### 根本原因

开发环境启用了响应压缩（Brotli），导致 BrowserLink 和热重载中间件无法注入脚本。

### 解决方案

在 `Program.cs` 中，仅在**生产环境**启用响应压缩：

```csharp
// 服务注册
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
    });
}

// 中间件使用
if (!app.Environment.IsDevelopment())
{
    app.UseResponseCompression();
}
```

### 效果

- ✅ 开发环境不再有警告
- ✅ 生产环境仍启用压缩（性能不受影响）

**提交**：`7660954`

---

## 2. Codex 凭证导出功能

### 需求说明

参考 CPA（Codex Proxy Admin）的凭证导出功能：
1. 勾选需要导出的账号
2. 点击导出按钮
3. 为每个账号生成独立的 JSON 文件并自动下载

### 2.1 后端 API

**端点**：`POST /api/admin/codex/accounts/export-credentials`

**请求体**：
```json
{
  "accountIds": ["guid1", "guid2", ...]
}
```

**响应**：
```json
{
  "credentials": [
    {
      "access_token": "...",
      "refresh_token": "...",
      "id_token": "...",
      "account_id": "...",
      "email": "user@example.com",
      "plan_type": "free"
    }
  ]
}
```

**代码实现**（`CodexApiController.cs`）：
```csharp
[HttpPost("accounts/export-credentials")]
public async Task<IActionResult> ExportCredentials([FromBody] ExportCredentialsRequest request, CancellationToken ct)
{
    if (request.AccountIds == null || request.AccountIds.Count == 0)
    {
        return BadRequest(new { message = "请至少选择一个账号" });
    }

    var accounts = await _dbContext.CodexAccounts
        .Where(a => request.AccountIds.Contains(a.Id))
        .ToListAsync(ct);

    if (accounts.Count == 0)
    {
        return NotFound(new { message = "未找到选中的账号" });
    }

    var credentials = accounts.Select(a => new
    {
        access_token = a.AccessToken ?? string.Empty,
        refresh_token = a.RefreshToken ?? string.Empty,
        id_token = a.IdToken ?? string.Empty,
        account_id = a.AccountId ?? string.Empty,
        email = a.Email ?? string.Empty,
        plan_type = a.PlanType ?? string.Empty
    }).ToList();

    return Ok(new { credentials });
}
```

### 2.2 前端 UI

#### 页面布局

在账号列表上方添加"导出凭证"按钮：
```html
<div class="d-flex justify-content-end gap-2 mb-3">
    <button type="button" class="btn btn-outline-secondary" onclick="toggleExportMode()">
        <span id="exportModeText">导出凭证</span>
    </button>
    <button type="button" class="btn btn-primary" onclick="openOAuthModal()">＋ OAuth 登录</button>
    <button type="button" class="btn btn-outline-primary" onclick="openImportModal()">上传凭证</button>
</div>
```

#### 导出工具栏

进入导出模式后显示工具栏：
```html
<div id="exportToolbar" style="display:none;margin-bottom:16px;padding:12px;background:#fff3cd;border-radius:8px;border:1px solid #ffc107;">
    <div style="display:flex;align-items:center;gap:12px;">
        <span style="font-weight:600;color:#856404;">已选中 <span id="selectedCount">0</span> 个账号</span>
        <button type="button" class="btn btn-sm btn-warning" onclick="exportSelectedCredentials()">下载凭证 JSON</button>
        <button type="button" class="btn btn-sm btn-outline-secondary" onclick="cancelExportMode()">取消</button>
    </div>
</div>
```

#### JavaScript 核心逻辑

```javascript
let isExportMode = false;
let selectedAccountIds = new Set();

function toggleExportMode() {
    isExportMode = !isExportMode;
    const container = document.getElementById('accountsContainer');
    const toolbar = document.getElementById('exportToolbar');
    const btnText = document.getElementById('exportModeText');
    
    if (isExportMode) {
        container.classList.add('export-mode');
        toolbar.style.display = 'block';
        btnText.textContent = '取消导出';
        selectedAccountIds.clear();
        updateSelectedCount();
    } else {
        cancelExportMode();
    }
}

function handleCardClick(event, accountId) {
    if (!isExportMode) return;
    if (event.target.tagName === 'INPUT') return;
    if (event.target.tagName === 'BUTTON') return;
    if (event.target.closest('button')) return;
    if (event.target.classList.contains('codex-reset-credits-hint')) return;
    
    event.preventDefault();
    event.stopPropagation();
    toggleCardSelection(event, accountId);
}

function toggleCardSelection(event, accountId) {
    if (!isExportMode) return;
    event.stopPropagation();
    
    const card = document.querySelector(`.codex-card[data-id="${accountId}"]`);
    const checkbox = card.querySelector('.codex-export-checkbox');
    
    if (selectedAccountIds.has(accountId)) {
        selectedAccountIds.delete(accountId);
        card.classList.remove('selected');
        checkbox.checked = false;
    } else {
        selectedAccountIds.add(accountId);
        card.classList.add('selected');
        checkbox.checked = true;
    }
    
    updateSelectedCount();
}

async function exportSelectedCredentials() {
    if (selectedAccountIds.size === 0) {
        alert('请至少选择一个账号');
        return;
    }

    try {
        const r = await fetch('/api/admin/codex/accounts/export-credentials', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ accountIds: Array.from(selectedAccountIds) })
        });

        if (!r.ok) {
            const data = await r.json();
            alert(data.message || '导出失败');
            return;
        }

        const data = await r.json();
        
        // 为每个账号生成独立的 JSON 文件并下载
        data.credentials.forEach((cred, index) => {
            const fileName = `codex_credential_${cred.email || cred.account_id || index + 1}.json`;
            const blob = new Blob([JSON.stringify(cred, null, 2)], { type: 'application/json' });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = fileName;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(url);
        });

        alert(`成功导出 ${data.credentials.length} 个账号的凭证文件`);
        cancelExportMode();
    } catch (e) {
        alert('导出异常：' + e.message);
    }
}
```

**提交**：`e3fa632`

---

## 3. UI 精细打磨（第一轮）

### 3.1 卡片布局修复

#### 问题 1：剩余重置次数位置不当

**修复**：从显示名称后移到邮箱后面

```javascript
// 修改前
<div class="codex-account-name">
    ${escapeHtml(a.displayName)}
    ${resetCreditsHint}  // ❌ 在名称后
</div>

// 修改后
<div class="codex-account-name">
    ${escapeHtml(a.displayName)}  // ✅ 名称单独一行
</div>
<div class="codex-account-email">
    ${escapeHtml(a.email || '')}
    ${resetCreditsHint}  // ✅ 在邮箱后
</div>
```

#### 问题 2：新账号没有进度条时卡片高度不一致

**修复**：添加占位区域

```css
.codex-windows-container {
    min-height: 60px;
    margin: 16px 0;
}

.codex-window-placeholder {
    padding: 20px;
    text-align: center;
    font-size: 13px;
    color: #9ca3af;
    background: #f9fafb;
    border-radius: 8px;
    border: 1px dashed #e5e7eb;
}
```

#### 问题 3：时间显示布局混乱

**修复**：改为独立行显示

```css
.codex-source-meta { 
    display: flex;
    flex-direction: column;
    gap: 6px;
}
.codex-source-meta div {
    word-break: break-all;
}
```

```javascript
// 修改前
<span title="${escapeHtml(lastCheck)}">${escapeHtml(lastCheck)}</span>
<span title="${escapeHtml(tokenExp)}">${escapeHtml(tokenExp)}</span>

// 修改后
<div>${escapeHtml(lastCheck)}</div>
<div>${escapeHtml(tokenExp)}</div>
```

### 3.2 文案精简

```html
<!-- 修改前 -->
<button>＋ 新增 Codex OAuth 登录</button>

<!-- 修改后 -->
<button>＋ OAuth 登录</button>
```

### 3.3 导航更新

`Pages/Shared/_Layout.cshtml`：
```html
<!-- 修改前 -->
<a class="sidebar-link" href="/Admin/Codex" title="Codex 账号">
    <span class="sidebar-link-icon">🤖</span>Codex 账号
</a>

<!-- 修改后 -->
<a class="sidebar-link" href="/Admin/Codex" title="OAuth 管理">
    <span class="sidebar-link-icon">🔐</span>OAuth 管理
</a>
```

**提交**：`dcf374a`

---

## 4. 进度条显示逻辑优化

### 4.1 显示逻辑从"已用"改为"剩余"

```javascript
// 修改前
const percent = w.usedPercent ?? 0;
const percentText = w.usedPercent.toFixed(1) + '%';

// 修改后
const usedPercent = w.usedPercent ?? 0;
const remainingPercent = Math.max(0, 100 - usedPercent);
const percentText = remainingPercent.toFixed(1) + '%';
```

### 4.2 颜色规则反转

```javascript
// 修改前（基于已用）
const tone = percent >= 100 ? 'bad' : percent >= 80 ? 'warn' : 'good';

// 修改后（基于剩余）
const tone = remainingPercent < 20 ? 'bad' : remainingPercent < 50 ? 'warn' : 'good';
```

**颜色规则**：
- **剩余 >= 50%**：🟢 **绿色**（额度充足）
- **剩余 20-50%**：🟡 **黄色**（需注意）
- **剩余 < 20%**：🔴 **红色**（告警）

**提交**：`e683dc8`

---

## 5. 紧急修复：导出功能 JS 丢失 + 卡片布局优化

### 5.1 问题发现

用户反馈：
1. 点击"导出凭证"按钮无反应
2. 控制台报错：
   ```
   Uncaught ReferenceError: toggleExportMode is not defined
   Uncaught ReferenceError: handleCardClick is not defined
   Uncaught ReferenceError: toggleCardSelection is not defined
   ```
3. 卡片布局"太糟糕了"

### 5.2 根本原因

1. **JS 函数丢失**：之前添加的导出模式 JS 代码块在某次编辑中丢失
2. **卡片布局问题**：
   - 时间信息和按钮使用 grid 布局，时间信息占据左侧整个宽度
   - 按钮不允许换行，导致溢出
   - 时间格式太长（`2026/7/3 20:40:13`），占用空间过大

### 5.3 修复方案

#### 修复 1：重新添加导出模式 JS

在 `// —— 工具 ——` 之前添加完整的导出模式代码块：

```javascript
// —— 导出模式 ——
let isExportMode = false;
let selectedAccountIds = new Set();

function toggleExportMode() { /* ... */ }
function cancelExportMode() { /* ... */ }
function handleCardClick(event, accountId) { /* ... */ }
function toggleCardSelection(event, accountId) { /* ... */ }
function updateSelectedCount() { /* ... */ }
async function exportSelectedCredentials() { /* ... */ }
```

#### 修复 2：优化卡片布局

**CSS 修改**：
```css
/* 修改前 */
.codex-card-meta {
    display: grid;
    grid-template-columns: 1fr auto;  /* ❌ 时间占整个左侧 */
    gap: 12px;
}

/* 修改后 */
.codex-card-meta {
    display: flex;
    flex-direction: column;  /* ✅ 垂直排列 */
    gap: 8px;
}

.codex-source-meta { 
    font-size: 11px;  /* ✅ 缩小字号 */
    color: #9ca3af;   /* ✅ 浅灰色 */
}

.codex-card-actions {
    flex-wrap: wrap;  /* ✅ 允许换行 */
    margin-top: 8px;
}
```

#### 修复 3：优化时间格式

**formatTime() 改为相对时间显示**：

```javascript
function formatTime(s) {
    if (!s) return '—';
    try {
        const d = new Date(s);
        const now = new Date();
        const diffMs = now - d;
        const diffMins = Math.floor(diffMs / 60000);
        const diffHours = Math.floor(diffMs / 3600000);
        const diffDays = Math.floor(diffMs / 86400000);
        
        // 相对时间显示（更简洁）
        if (diffMins < 1) return '刚刚';
        if (diffMins < 60) return `${diffMins}分钟前`;
        if (diffHours < 24) return `${diffHours}小时前`;
        if (diffDays < 7) return `${diffDays}天前`;
        
        // 超过 7 天显示日期（月-日 时:分）
        return d.toLocaleString('zh-CN', { 
            month: 'numeric', 
            day: 'numeric', 
            hour: '2-digit', 
            minute: '2-digit',
            hour12: false 
        });
    } catch { 
        return s; 
    }
}
```

**时间格式对比**：

| 修改前 | 修改后 |
|-------|-------|
| 2026/7/3 20:40:13 | 5分钟前 |
| 2026/7/3 14:30:00 | 6小时前 |
| 2026/6/28 10:00:00 | 6-28 10:00 |

### 5.4 效果对比

**修改前**：
- ❌ 导出按钮点击无反应
- ❌ 时间信息占据整个左侧宽度
- ❌ 按钮被挤到右侧，排列混乱
- ❌ 时间格式冗长（19 个字符）

**修改后**：
- ✅ 导出功能正常工作
- ✅ 时间信息简洁，上方显示
- ✅ 按钮在时间下方，允许换行
- ✅ 时间格式简短（4-10 个字符）

**提交**：`562d3c8`

---

## 6. 导出模式：复选框位置优化

### 6.1 问题反馈

用户反馈："这个账号选择的复选框，我希望放在卡片的左上角，目前这个位置实在太丑了。"

### 6.2 原因分析

复选框使用默认流式布局，位置不固定：
- 在卡片内容流中，位置随内容变化
- 视觉上不够突出
- 与卡片内容混在一起，不够独立

### 6.3 优化方案

#### CSS 样式调整

```css
/* 卡片添加相对定位 */
.codex-card {
    position: relative;
}

/* 复选框绝对定位到左上角 */
.codex-export-checkbox {
    display: none;
    position: absolute;
    top: 16px;
    left: 16px;
    width: 20px;
    height: 20px;
    cursor: pointer;
    z-index: 10;
    accent-color: #C4612F;  /* 使用主题色 */
}

/* 导出模式时显示 */
.export-mode .codex-export-checkbox {
    display: block;
}

/* 导出模式下卡片左侧留出空间 */
.export-mode .codex-card {
    cursor: pointer;
    padding-left: 48px;  /* 为复选框留出空间 */
}

/* 选中状态高亮 */
.export-mode .codex-card.selected {
    border-color: #C4612F;
    box-shadow: 0 0 0 3px rgba(196, 97, 47, 0.15);
    background: rgba(196, 97, 47, 0.02);
}
```

### 6.4 效果对比

**修改前**：
- ❌ 复选框位置不固定，随内容流动
- ❌ 视觉上与内容混在一起
- ❌ 不够突出，不美观

**修改后**：
- ✅ 复选框固定在左上角（16px, 16px）
- ✅ 位置独立，不影响内容布局
- ✅ 选中状态清晰（边框高亮 + 外发光）
- ✅ 使用主题色（#C4612F），视觉统一

### 6.5 交互细节

1. **默认状态**：复选框隐藏（`display: none`）
2. **进入导出模式**：复选框显示在左上角
3. **卡片留白**：左侧 padding 增加到 48px，避免内容被遮挡
4. **选中反馈**：
   - 复选框勾选
   - 卡片边框变为主题色
   - 外围显示发光效果
   - 背景微调为浅色

**提交**：`66b3a44`

---

## 验证结果

### 编译验证
- ✅ 编译 0 错误
- ✅ 编译 0 警告

### 集成测试
- ✅ 177/177 测试通过
- ✅ 无回归问题

### 功能验证
1. ✅ BrowserLink 警告已消失
2. ✅ 凭证导出功能正常（勾选 → 导出 → 下载 JSON）
3. ✅ 卡片布局整齐，时间信息不再挤压按钮
4. ✅ 新账号有占位提示框，高度一致
5. ✅ 时间信息简洁易读（相对时间）
6. ✅ 按钮文字精简，视觉舒适
7. ✅ 左侧导航"OAuth 管理" + 🔐 图标合适
8. ✅ 进度条显示剩余额度，颜色规则直观

---

## 远程同步

所有提交已推送到两个远程仓库：
- ✅ 内网 Gitea：`http://192.168.3.150:3000/kaixin1995/AI-Tool.git`
- ✅ GitHub：`https://github.com/kaixin1995/AITool.git`

---

## 总结

本次任务完成了 **6 个主要改进**：

1. **修复 BrowserLink 警告**：开发环境禁用响应压缩
2. **新增凭证导出功能**：后端 API + 前端交互 + JSON 下载
3. **UI 精细打磨（第一轮）**：卡片布局 + 文案精简 + 导航更新
4. **进度条逻辑优化**：显示剩余额度 + 反转颜色规则
5. **紧急修复**：导出 JS 丢失 + 卡片布局优化 + 时间格式简化
6. **导出模式优化**：复选框移到卡片左上角，视觉更美观

所有改动均已通过编译验证、集成测试和功能验证，代码已推送到远程仓库。

---

## 经验教训

1. **代码完整性**：大段 JS 代码容易在编辑时丢失，需要：
   - 使用明显的注释分隔符（`// —— 导出模式 ——`）
   - 编辑前先读取相关代码块
   - 编辑后验证函数是否存在

2. **布局设计**：
   - Grid 布局适合固定结构，但容易导致挤压
   - Flexbox 布局更灵活，适合响应式内容
   - 时间信息等次要内容应使用小字号和浅色

3. **时间格式**：
   - 绝对时间（`2026/7/3 20:40:13`）太长，占用空间
   - 相对时间（`5分钟前`）更简洁、更易读
   - 超过 7 天再显示日期

4. **完整工作流**：
   - ✅ 修改代码
   - ✅ 编译测试
   - ✅ 提交推送
   - ✅ 更新文档

---

## 后续建议

1. **性能优化**（P1 优先级）：
   - Reset Credits 添加内存缓存
   - 账号列表 API 缓存 `resetCreditsAvailableCount` 字段

2. **功能增强**：
   - 凭证导出支持批量压缩为 ZIP 下载
   - 导出前预览凭证信息
   - 支持导出格式选择（JSON / YAML / ENV）

3. **UI 完善**：
   - 添加卡片骨架屏加载动画
   - 优化移动端响应式布局
   - 添加快捷键支持（Ctrl+A 全选、Ctrl+E 导出）

4. **代码健壮性**：
   - 考虑将导出模式代码提取为单独的 JS 模块
   - 添加单元测试覆盖关键函数
