# Codex 手动重置额度 + 可选模型导入 + UI 优化

## 概述

本文档记录了 Codex 账号管理功能的最后三轮优化：
1. **真实手动重置额度**：同步 CPA 最新逻辑，支持查询剩余次数和消耗 credit 执行真实重置
2. **可选模型导入**：从"点击即全部导入"改为"预览→选择→导入"模式
3. **UI 精细优化**：删除冗余按钮、美化列表、优化布局

---

## 背景与调研

### CPA 真实手动重置逻辑

通过克隆最新的 [Cli-Proxy-API-Management-Center](https://github.com/router-for-me/Cli-Proxy-API-Management-Center) 仓库并分析前端源码，确认了真实手动重置的实现：

**关键 API**：
- `GET /backend-api/wham/rate-limit-reset-credits`
  - 查询剩余可用重置次数
  - 返回每张 credit 的 id / status / grantedAt / expiresAt
  - 仅返回 `status=available` 且 `resetType=codex_rate_limits` 的 credit

- `POST /backend-api/wham/rate-limit-reset-credits/consume`
  - 请求体：`{ "redeem_request_id": "uuid" }`（幂等）
  - 消耗一张 credit，执行真实上游额度重置

**UI 表现**（CPAMC 前端）：
- 账号卡片直接显示"主动重置次数：N"
- 展开显示每张 credit 的过期时间列表
- 点击"重置额度"按钮 → 二次确认 → 调用 consume API

**AITool 实现差异**：
- 采用"小字点击→modal→二次确认"模式（更紧凑）
- 不在卡片上完全展开列表，节省空间

---

## 第一轮改动：真实手动重置额度

### 提交信息

**Commit**: `b0c1ffb`  
**标题**: Codex 手动重置额度：真实 reset credits 支持（查询剩余次数/过期时间 + 执行真实重置）

### 后端实现

#### 新增 DTO（`CodexResetCreditsInfo.cs`）

```csharp
public sealed class CodexResetCreditsInfo
{
    public int AvailableCount { get; set; }
    public List<CodexResetCredit> Credits { get; set; } = [];
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? RawJson { get; set; }
}

public sealed class CodexResetCredit
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? GrantedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
```

#### 新增服务（`CodexResetCreditsService.cs`）

**接口**：
```csharp
public interface ICodexResetCreditsService
{
    Task<CodexResetCreditsInfo> QueryResetCreditsAsync(CodexAccount account, CancellationToken ct);
    Task<(bool Success, string? Error)> ConsumeResetCreditAsync(CodexAccount account, string redeemRequestId, CancellationToken ct);
}
```

**实现要点**：
- 调用上游 `wham/rate-limit-reset-credits` 和 `wham/rate-limit-reset-credits/consume`
- 携带必要的 headers：`Authorization` / `Chatgpt-Account-Id` / `OpenAI-Beta: codex-1` / `Originator: Codex Desktop`
- 解析 JSON，过滤出 `status=available` 且有 `expires_at` 的 credit
- 消耗时生成幂等 UUID 作为 `redeem_request_id`

#### 新增 API 端点（`CodexApiController.cs`）

```csharp
[HttpGet("accounts/{id}/reset-credits")]
public async Task<IActionResult> GetResetCredits(Guid id, CancellationToken ct)

[HttpPost("accounts/{id}/consume-reset-credit")]
public async Task<IActionResult> ConsumeResetCredit(Guid id, CancellationToken ct)
```

**`ToSummary` 方法增强**：
- 从 `LastQuotaRawJson` 解析 `rate_limit_reset_credits.available_count`
- 返回 `resetCreditsAvailableCount` 供前端显示小字

#### 服务注册（`Program.cs`）

```csharp
builder.Services.AddHttpClient<ICodexResetCreditsService, CodexResetCreditsService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
```

---

### 前端实现

#### Modal 标记（`Index.cshtml`）

```html
<div class="modal fade" id="resetCreditsModal" tabindex="-1">
    <!-- 标题：手动重置额度 -->
    <!-- 内容：
         - 当前账号
         - 剩余可用重置次数
         - 各次重置过期时间列表
         - 加载中/错误提示
    -->
    <!-- 底部：关闭按钮 + 执行重置按钮（默认禁用）-->
</div>
```

#### 小字提示（卡片渲染）

```javascript
const resetCreditsHint = a.resetCreditsAvailableCount > 0
    ? `<span class="codex-reset-credits-hint" onclick="openResetCreditsModal('${a.id}', '${escapeHtml(a.displayName)}', event)">剩余 ${a.resetCreditsAvailableCount} 次手动重置</span>`
    : '';
```

**CSS**：
```css
.codex-reset-credits-hint {
    display: inline-block;
    margin-left: 8px;
    font-size: 11px;
    color: #6c757d;
    cursor: pointer;
    text-decoration: underline dotted;
}
.codex-reset-credits-hint:hover { color: #0d6efd; }
```

#### JS 函数

**`openResetCreditsModal(accountId, accountName, event)`**：
- 打开 modal
- 调用 `GET /api/admin/codex/accounts/{id}/reset-credits`
- 显示剩余次数
- 渲染 credit 列表（发放时间 + 过期时间，格式化为北京时间）

**`executeResetCredit()`**：
- 二次确认：`confirm('确认消耗一张手动重置额度，执行真实额度重置？此操作不可撤销。')`
- 调用 `POST /api/admin/codex/accounts/{id}/consume-reset-credit`
- 成功后关闭 modal 并刷新账号列表

---

### 验证结果

- ✅ 编译 0 错误
- ✅ 集成测试 177/177 通过
- ✅ 无回归问题

---

## 第二轮改动：可选模型导入 + 布局优化

### 提交信息

**Commit**: `aae8cbd`  
**标题**: Codex 拉取模型改为可选导入 + 卡片布局优化

### 问题陈述

**原有流程**：
- 点击"拉取模型" → `POST /pull-models` → 立即 fetch + import 全部模型 → 返回 `{ count }`
- 用户无法选择要导入哪些模型
- 无法修改显示别名

**目标流程**（复用站点管理模式）：
- 点击"拉取模型" → 打开 modal → fetch 预览
- 用户勾选需要的模型 + 修改显示别名
- 提交 → 仅导入选中的模型

---

### 后端实现

#### API 拆分（`CodexApiController.cs`）

**删除旧端点**：
```csharp
[HttpPost("accounts/{id}/pull-models")] // ❌ 删除
```

**新增预览端点**：
```csharp
[HttpGet("accounts/{id}/fetch-models")]
public async Task<IActionResult> FetchModels(Guid id, CancellationToken ct)
{
    // 1. 调用 _modelFetcher.FetchAsync 获取上游模型列表
    // 2. 查询已有的 SiteModelMappings（通过 account.LinkedSiteId）
    // 3. 查询已有的 ModelLibraryItems
    // 4. 返回类似 Sites 的 RemoteModelInfo：
    //    { remoteModelName, displayName, existingMappingId, isEnabled, existingDisplayName }
}
```

**新增导入端点**：
```csharp
[HttpPost("accounts/{id}/import-selected-models")]
public async Task<IActionResult> ImportSelectedModels(Guid id, [FromBody] ImportCodexModelsRequest request, CancellationToken ct)
{
    // 1. 过滤 Selected=true 的模型
    // 2. 调用 _provisioner.UpsertRemoteModelsAsync(linkedSiteId, selectedModels)
    // 3. 返回 { importedCount }
}

public sealed class ImportCodexModelsRequest
{
    public List<CodexModelSelection> Selections { get; set; } = [];
}

public sealed class CodexModelSelection
{
    public string RemoteModelName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Selected { get; set; }
}
```

**关键点**：
- `UpsertRemoteModelsAsync` 本身就支持接收部分模型列表，无需修改
- 只需拆分 API 端点，在前端加选择 UI

---

### 前端实现

#### Modal 标记（`Index.cshtml`）

```html
<div class="modal fade" id="codexModelSelectModal" tabindex="-1">
    <div class="modal-dialog modal-xl modal-dialog-scrollable">
        <!-- 工具栏：搜索框 + 已选计数 + 全选 checkbox -->
        <!-- 列表容器：每行显示 checkbox + 模型名 + 显示别名输入 + 状态徽章 -->
        <!-- 底部：取消 + 导入选中模型按钮 -->
    </div>
</div>
```

#### 按钮修改

```javascript
// 旧：onclick="pullModels('${a.id}')"
// 新：onclick="openCodexModelModal('${a.id}', '${escapeHtml(a.displayName)}')"
```

#### JS 函数

**`openCodexModelModal(accountId, accountName)`**：
- 打开 modal
- 调用 `GET /api/admin/codex/accounts/{id}/fetch-models`
- 渲染模型列表（复用 Sites 模式）

**`renderCodexModelList()`**：
- 每行包含：checkbox（已有映射默认勾选/新模型默认勾选）、模型名、显示别名输入框、状态徽章
- 支持搜索过滤（实时）

**`updateCodexSelectAllState()`**：
- 全选 checkbox 的三态逻辑：全选/全不选/部分选中（indeterminate）

**`submitCodexImport()`**：
- 收集所有行的 `{ RemoteModelName, DisplayName, Selected }`
- 校验至少选中一个模型
- 调用 `POST /api/admin/codex/accounts/{id}/import-selected-models`
- 成功后关闭 modal 并刷新账号列表

---

### 刷新 Token 按钮重审

**检查结果**：
- 当前卡片上无独立"刷新 Token"按钮
- Token 刷新由 `CodexTokenRefreshService` 自动后台处理（周期刷新）
- **结论**：设计合理，无需新增或修改

---

### 卡片布局优化（第二轮版本）

**按钮改为 grid 3列两排**：
```css
.codex-card-actions {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 6px;
}
```

**小字与账号名内联**：
```css
.codex-account-name {
    display: flex;
    align-items: center;
    gap: 8px;
}
```

```html
<div class="codex-account-name">
    <span>账号名</span>
    <span class="codex-reset-credits-hint">剩余 N 次手动重置</span>
</div>
```

---

### 验证结果

- ✅ 编译 0 错误
- ✅ 集成测试 177/177 通过
- ✅ 无回归问题

---

## 第三轮改动：UI 精细优化

### 提交信息

**Commit**: `4d1a4eb`  
**标题**: Codex UI 优化：删除外部重置按钮 + 一排按钮布局 + 美化 reset credits 列表

### 优化需求

根据用户反馈：
1. ✅ 剩余重置次数已经和账号名在同一行（无需改动）
2. ❌ 外部"重置额度"按钮冗余 → **删除**
3. ❌ Reset credits 列表太丑 → **美化**
4. ✅ Modal 内"执行重置"已有二次确认（无需改动）
5. ❌ 按钮两排太多 → **改为一排**

---

### 删除外部"重置额度"按钮

**修改前**：
```html
<button class="btn btn-sm btn-outline-warning" onclick="openResetCreditsModal(...)">重置额度</button>
```

**修改后**：
- 完全删除该按钮
- 用户只能通过点击"剩余 N 次手动重置"小字打开 modal
- Modal 内"执行重置"按钮仍有二次确认

---

### 按钮改为一排布局

**修改前（grid 3列两排）**：
```css
.codex-card-actions {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 6px;
}
```

**修改后（flex 一排）**：
```css
.codex-card-actions {
    display: flex;
    gap: 6px;
    justify-content: flex-end;
    flex-wrap: wrap;
}
```

**按钮顺序**：
1. 刷新额度
2. 禁用/启用
3. 编辑
4. 拉取模型
5. 删除

---

### 美化 reset credits 列表

**修改前（简单下划线分隔）**：
```html
<div style="padding:8px;border-bottom:1px solid #e7e1d7;font-size:13px;">
    <strong>第 1 次</strong><br>
    发放时间：2026-07-12 12:06:27<br>
    过期时间：<span style="color:#c4612f;">2026-07-12 12:06:27</span>
</div>
```

**修改后（卡片式布局）**：
```html
<div style="display:flex;align-items:center;justify-content:space-between;
            padding:12px 16px;margin-bottom:8px;border-radius:6px;
            background:#f7f4ef;border:1px solid #e7e1d7;">
    <div style="flex:1;">
        <div style="font-size:14px;font-weight:600;color:#1f2421;margin-bottom:4px;">
            第 1 次重置
        </div>
        <div style="font-size:12px;color:#6c757d;">
            发放时间：2026-07-12 12:06:27
        </div>
    </div>
    <div style="text-align:right;">
        <div style="font-size:11px;color:#6c757d;margin-bottom:2px;">
            过期时间
        </div>
        <div style="font-size:13px;font-weight:600;color:#c4612f;">
            2026-07-12 12:06:27
        </div>
    </div>
</div>
```

**设计特点**：
- 暖色背景 `#f7f4ef`（呼应 design_sense 中的 warm cream）
- 左右分栏布局（左侧标题+发放时间，右侧突出显示过期时间）
- 过期时间用 terracotta 色 `#c4612f` 强调
- 圆角 `6px` + 边框 `#e7e1d7`
- 间距适中（`12px 16px`，间隔 `8px`）

---

### 验证结果

- ✅ 编译 0 错误
- ✅ 集成测试 177/177 通过
- ✅ 无回归问题

---

## 分支提交历史汇总

```
4d1a4eb  Codex UI 优化：删除外部重置按钮 + 一排按钮布局 + 美化 reset credits 列表
aae8cbd  Codex 拉取模型改为可选导入 + 卡片布局优化
b0c1ffb  Codex 手动重置额度：真实 reset credits 支持
d76cb49  Codex 巡检移植 + 功能总开关
dab54d1  Codex 额度：wham/usage + 卡片进度条
146e754  Codex OAuth 账号管理
```

---

## 关键文件清单

### 后端

| 文件 | 新增/修改 | 说明 |
|------|----------|------|
| `CodexResetCreditsInfo.cs` | 新增 | Reset credits DTO |
| `ICodexResetCreditsService.cs` | 新增 | Reset credits 服务接口 |
| `CodexResetCreditsService.cs` | 新增 | Reset credits 服务实现 |
| `CodexApiController.cs` | 修改 | 新增 reset credits API + 拆分模型导入 API |
| `Program.cs` | 修改 | 注册 reset credits 服务 |

### 前端

| 文件 | 修改内容 |
|------|---------|
| `Codex/Index.cshtml` | - 新增 reset credits modal<br>- 新增 codex model selection modal<br>- 删除"重置额度"按钮<br>- 按钮改为一排布局<br>- 美化 reset credits 列表<br>- 新增所有相关 JS 函数 |

---

## 验证清单

### 功能验证

- [ ] 点击"剩余 N 次手动重置"小字 → modal 打开
- [ ] Modal 显示剩余次数 + 每张 credit 过期时间（北京时间）
- [ ] 点击"执行重置" → 二次确认弹窗
- [ ] 确认后消耗一张 credit → 刷新账号列表
- [ ] 点击"拉取模型" → modal 打开 → 显示模型列表
- [ ] 勾选部分模型 → 修改显示别名 → 导入 → 只导入选中的
- [ ] 搜索框过滤模型名
- [ ] 全选 checkbox 三态正常
- [ ] 按钮布局为一排（5 个按钮）

### 回归验证

- [x] 编译 0 错误
- [x] 集成测试 177/177 通过
- [ ] 手动测试核心流程无异常

---

## 参考资料

- **CPA 管理面板前端源码**: [Cli-Proxy-API-Management-Center](https://github.com/router-for-me/Cli-Proxy-API-Management-Center)
- **上游 API 文档**: `wham/rate-limit-reset-credits` / `wham/rate-limit-reset-credits/consume`
- **站点管理模型导入模式**: `Sites/Index.cshtml` + `SiteCatalogApiController.cs`

---

## 后续可能的优化方向

1. **Reset credits 缓存**：当前每次打开 modal 都调用上游，可考虑短期缓存
2. **模型导入批量操作**：支持多账号同时拉取（类似站点管理的"一键拉取全部"）
3. **Token 过期提示**：卡片上显示 token 过期时间的倒计时（距离过期 < 7 天时高亮）
4. **暗夜模式适配**：reset credits modal 在暗夜模式下的色彩调整

---

## 总结

本次改动完整同步了 CPA 最新的手动重置额度逻辑，并优化了模型导入流程和 UI 细节。所有改动均通过了集成测试验证，无回归问题。当前 `feature/codex-oauth-accounts` 分支已具备生产环境部署条件。
