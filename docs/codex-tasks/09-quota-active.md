# T09 — 额度主动查询与自动禁用阈值

> 状态：已完成 ✅（主动查询框架就绪；上游额度端点待实测确认解析）
> 前置依赖：T01（数据模型）、T02（OAuth，取 token）、T04（Provisioner，禁用模式）
> 关联总览章节：横切性能原则 P1 / P5 / P8 / P10

## 实施记录

- 新建 `src/AITool.Application/Codex/CodexQuotaInfo.cs`、`ICodexQuotaService.cs`。
- 新建 `src/AITool.Web/Services/CodexQuotaService.cs`（放 Web 层，因依赖 ProxyRequestMetadataCache）：
  - 30s `IMemoryCache` 结果缓存防抖；forceRefresh 穿透。
  - `ConcurrentDictionary<Guid, SemaphoreSlim>` single-flight（二次检查缓存）。
  - 候选端点 `chatgpt.com/backend-api/codex/usage`，带 Bearer/Originator/UA/Chatgpt-Account-Id。
  - `TryParseQuota` 宽松解析（尝试 remaining/used/total/unit/resets_at 多种字段名，unix/ISO 兼容）。
  - 持久化 LastQuotaRawJson/LastQuotaCheckedAt；自动禁用判定（剩余<阈值→禁用账号+Site+invalidate）。
  - 失败降级（Success=false 不影响账号）；缓存失败结果 30s 防风暴。
- Program.cs 注册 `AddHttpClient<ICodexQuotaService, CodexQuotaService>`（IMemoryCache 已在 line64 注册）。
- 编译通过。
- ⚠️ 待实测：上游额度端点结构与字段名确认后，补全 TryParseQuota 的具体提取。若无可读额度数字，RemainingQuota 留 null，自动禁用阈值不生效（由 T10 被动冷却兜底）。

## 目标

实现 `CodexQuotaService`，主动查询上游 Codex 额度，展示剩余额度数字，并在剩余额度低于账号 `AutoDisableThreshold` 时自动禁用账号。手动「刷新额度」按钮复用同一查询。

**用户选定「两者都要」**：本任务负责「主动查询 + 自动禁用」；被动冷却由 T10 负责。

对应 CPA：**CPA 不主动查询额度**（只被动解析错误）。主动查询参考 new-api：`GET /api/channel/{id}/codex/usage` → `CodexUsageResponse`。

---

## 涉及文件

| 文件 | 操作 |
| --- | --- |
| `src/AITool.Application/Codex/ICodexQuotaService.cs` | 新建接口 |
| `src/AITool.Application/Codex/CodexQuotaInfo.cs` | 新建额度信息 DTO |
| `src/AITool.Infrastructure/Codex/CodexQuotaService.cs` | 新建实现（HttpClient） |
| `src/AITool.Web/Program.cs` | 注册（HttpClient + service） |

---

## ⚠️ 待实测确认（实现第一步）

**上游「剩余额度」端点不确定**，这是本任务最大风险。需在动工前实测确认：

候选端点：
1. **new-api 风格**：`GET https://chatgpt.com/backend-api/codex/usage`（或类似），带 `Authorization: Bearer <token>` + `Chatgpt-Account-Id`。返回结构未知，参考 new-api `CodexUsageResponse { upstream_status, data: Record<string,unknown> }`。
2. **chatgpt backend-api rate-limit 头**：某些响应头（`X-RateLimit-Remaining` 等）可能携带额度。
3. **codex/models 响应附带**：`backend-api/codex/models` 响应里可能有 plan/limit 字段。

**实测方法**：用一个有效 Codex token，curl 试探上述端点，观察返回。根据真实结构定义 `CodexQuotaInfo` 解析。

**若无可读「剩余额度数字」端点**：本任务主动部分**退化为展示**——展示 PlanType、订阅窗口（JWT）、上次检查时间、状态（正常/冷却），不展示额度数字；**自动禁用阈值功能改为依赖 T10 被动冷却触发**（无数字则阈值无意义）。文档下方按「有额度数字」的完整方案写，退化策略见「风险」。

---

## 详细步骤（按有额度数字方案）

### 1. 额度信息 DTO `CodexQuotaInfo`

```csharp
public sealed class CodexQuotaInfo
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public decimal? RemainingQuota { get; set; }      // 剩余额度（单位由上游决定）
    public decimal? UsedQuota { get; set; }           // 已用
    public decimal? TotalQuota { get; set; }          // 总额（若有）
    public string? QuotaUnit { get; set; }            // 单位描述（如 "credits"/"requests"）
    public string? PlanType { get; set; }             // 从响应或 JWT
    public DateTimeOffset? ResetAt { get; set; }      // 额度重置时间（若有）
    public string? RawJson { get; set; }              // 原始响应（存 LastQuotaRawJson）
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

### 2. 服务 `CodexQuotaService`

注入 `HttpClient`（`AddHttpClient<ICodexQuotaService, CodexQuotaService>`，超时 20s）。

```csharp
public interface ICodexQuotaService
{
    Task<CodexQuotaInfo> QueryAsync(CodexAccount account, bool forceRefresh, CancellationToken ct);
}
```

#### `QueryAsync` 流程

1. **缓存防抖**（P8）：用 `IMemoryCache` 按 accountId 缓存结果，TTL 30s。
   - `forceRefresh=false`：先查缓存，命中直接返回。
   - `forceRefresh=true`（手动刷新）：穿透缓存。
2. **single-flight**（P8）：同一 accountId 并发只一次真实请求（`ConcurrentDictionary<accountId, SemaphoreSlim>`）。
3. **调上游**：
   ```csharp
   var req = new HttpRequestMessage(HttpMethod.Get, "<实测确认的端点>");
   req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
   req.Headers.Add("Chatgpt-Account-Id", account.AccountId);
   req.Headers.TryAddWithoutValidation("Originator", "codex_cli_rs");
   req.Headers.TryAddWithoutValidation("User-Agent", "codex_cli_rs/0.133.0 ...");
   var resp = await _httpClient.SendAsync(req, ct);
   ```
4. **解析**：按实测结构解析剩余额度等字段。`RawJson = await resp.Content.ReadAsStringAsync()`。
5. **持久化**：写 `account.LastQuotaRawJson = info.RawJson`、`LastQuotaCheckedAt = now`（列更新，见性能考量）。
6. **自动禁用判定**：
   ```csharp
   if (info.RemainingQuota.HasValue
       && account.AutoDisableThreshold.HasValue
       && info.RemainingQuota.Value < account.AutoDisableThreshold.Value)
   {
       await DisableAccountAsync(account, reason: $"剩余额度 {info.RemainingQuota} 低于阈值 {account.AutoDisableThreshold}");
   }
   ```
7. 返回 `CodexQuotaInfo`。

#### `DisableAccountAsync`

```csharp
private async Task DisableAccountAsync(CodexAccount account, string reason)
{
    account.IsEnabled = false;
    await db.UpdateColumnsAsync(account, nameof(account.IsEnabled));   // 只更新启用列
    var site = await db.Sites.FirstAsync(s => s.Id == account.LinkedSiteId);
    site.IsEnabled = false;
    await db.UpdateAsync(site);
    cache.InvalidateRouteTargets();
    // 记录禁用原因到 LastQuotaRawJson 或独立字段（便于面板展示）
    logger.LogWarning("Codex account {Id} auto-disabled: {Reason}", account.Id, reason);
}
```

> 禁用 Site（`site.IsEnabled=false`）后，`GetRouteTargetsAsync` 的 `where rule.IsEnabled && site.IsEnabled` 会自动排除该账号的路由目标（已核查 `ProxyRequestMetadataCache.cs` join 逻辑）。转发链路自动绕开，无需额外改动。

### 3. 注册

```csharp
builder.Services.AddHttpClient<ICodexQuotaService, CodexQuotaService>(c => c.Timeout = TimeSpan.FromSeconds(20));
```

---

## 性能考量

### 引用原则
- **P1 缓存失效**：禁用账号后失效路由缓存。
- **P5 HttpClient 复用**：`AddHttpClient` 注册。
- **P8 single-flight + 结果缓存**：30s 防抖 + 并发合并。
- **P10 热路径**：额度查询**不在转发主链路**，异步/手动触发。

### 本任务特有
- **30s 结果缓存**：同一账号 30s 内多次查询（如面板刷新 + 后台轮询）只一次真实上游请求。手动按钮 `forceRefresh=true` 穿透。
- **single-flight**：并发手动刷新同一账号只打一次上游。
- **列更新**：持久化额度结果时**只更新 `LastQuotaRawJson`/`LastQuotaCheckedAt`**（SqlSugar `UpdateColumns`），避免全字段更新覆盖并发进行的 token 刷新（T08）。同理禁用时只更新 `IsEnabled`。
- **异步不阻塞**：T11 手动刷新接口 `await QueryAsync`，但 T10 被动冷却或后台轮询调用时不阻塞转发。
- **解析失败降级**：上游返回非 200 或解析失败 → `Success=false, Error=...`，**不影响账号可用性**（不误禁用），面板显示「额度查询失败」。
- **自动禁用幂等**：已禁用账号再次触发禁用判定 → 无副作用（再次 Update IsEnabled=false）。
- **查询频率**：手动按钮 + 后台可选周期查询（如每 10 分钟一轮，与 T08 类似的 BackgroundService，但本期可仅手动触发，避免上游压力）。

---

## 验收标准

1. 手动「刷新额度」→ 调上游 → 面板展示剩余/已用额度（若上游返回）。
2. 剩余额度 < AutoDisableThreshold → 账号自动禁用，Site 同步禁用，缓存失效，转发绕开。
3. 阈值=null → 不自动禁用。
4. 同账号 30s 内多次查询只一次真实上游请求。
5. 上游失败 → 面板显示失败，账号不禁用、不影响转发。
6. 禁用后面板状态显示「已自动禁用（剩余额度不足）」+ 原因。

---

## 风险

### ⚠️ 上游额度端点不确定（最高风险）
**退化为：** 若实测无可读额度数字端点：
- `CodexQuotaInfo` 的 `RemainingQuota/UsedQuota` 留空，面板展示 PlanType + 订阅窗口 + 上次检查 + 状态。
- **自动禁用阈值功能暂不生效**（无数字无法比较），改为 T10 被动冷却触发禁用。
- 文档与前端文案标注「额度数字需上游支持，当前仅展示状态」。
- 本任务的 HttpClient + 缓存 + 解析框架仍保留，待上游确认后补解析。

### 其它风险
- **额度单位**：上游可能用 credits/requests/tokens 等不同单位。`QuotaUnit` 字段记录，阈值设置时同单位比较。前端设置阈值时提示单位。
- **列更新 API**：SqlSugar 的 `UpdateColumns` 写法需确认（`db.Updateable(entity).UpdateColumns(col => new { col.IsEnabled })` 或字符串名）。实现时 Read SqlSugar 文档/项目现有用法。
- **并发更新覆盖**：与 T08 共有的风险。靠「列更新」缓解，但若列更新不可靠，退化为「先读最新再写」或乐观锁。本期以列更新为主。
- **额度查询上游限流**：频繁查询可能触发上游限流（与额度耗尽不同）。缓存防抖 + 退避。被限流时 `Success=false`。
