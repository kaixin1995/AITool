# T10 — 额度被动冷却与重置

> 状态：部分完成 🟡（重置 + 自动恢复 + 错误解析判定函数 已就绪；转发错误路径接入待 T13 验证后补）
> 前置依赖：T01（数据模型）、T04（Provisioner，重启用模式）、T02（OAuth，重置时刷新 token）
> 关联总览章节：横切性能原则 P1 / P10

## 实施记录

- 新建 `src/AITool.Application/Codex/ICodexQuotaCooldownService.cs`。
- 新建 `src/AITool.Web/Services/CodexQuotaCooldownService.cs`：
  - `TryApplyCooldownFromErrorAsync`：解析 usage_limit_reached/429(或402)，按 resets_at/resets_in_seconds/默认30min 算 coolingUntil；按 LinkedSiteId 查 CodexAccount；标记冷却 + 禁用 Site + invalidate。
  - `ResetAsync`：刷新 token + 清冷却 + 恢复 Site + invalidate（兼容 token 轮换；刷新失败不阻断重置）。
  - `IsUsageLimitError`：严格匹配 `error.type=="usage_limit_reached"`（普通 rate_limit_exceeded 不计入），照搬 CPA。
- 新建 `src/AITool.Web/Services/CodexCooldownRecoveryService.cs`：BackgroundService，2 分钟周期，扫描 `IsQuotaCooling && QuotaCoolingUntil<=now`，清冷却；**恢复前检查 account.IsEnabled**（手动禁用优先，不被冷却到期覆盖）；恢复 Site 后 invalidate。
- Program.cs 注册 `ICodexQuotaCooldownService`(Scoped) + `AddHostedService<CodexCooldownRecoveryService>()`。
- 编译通过。

## 待办（转发错误路径接入）

⚠️ 当前 `TryApplyCooldownFromErrorAsync` 已实现但**尚未在代理控制器错误分支调用**。原因：OpenAI 控制器的错误处理嵌入在多路由迭代循环内，接入需注入 cooldown 服务并读取 forward result body/status，改动面较大且影响转发主路径回归风险。

当前冷却闭环通过以下方式兜底（已全部就绪）：
- 自动恢复：冷却到期由 `CodexCooldownRecoveryService` 自动清除。
- 手动重置：前端「重置额度」按钮 → API → `ResetAsync`。
- 主动查询：T09 `CodexQuotaService` 检测剩余额度低于阈值时自动禁用。

转发错误路径接入（让 usage_limit_reached 即时触发冷却，而非等下次主动查询）列为**T13 验证后的增强项**，在基础闭环验证通过后补做，避免现在大改转发主路径引入回归。接入点：OpenAiProxyController 与 Responses 控制器的非成功响应分支。

## 目标

实现 Codex 账号的**被动冷却**：在转发错误处理分支解析上游 `usage_limit_reached` / 429 错误，标记账号进入冷却（临时禁用），冷却到期自动恢复。并提供**重置额度**（清冷却、重新启用、刷新 token）。

**用户选定「两者都要」**：本任务负责「被动冷却 + 重置」；主动查询由 T09 负责。

对应 CPA：
- `isCodexUsageLimitError`：`internal/runtime/executor/codex_executor.go:1818-1832`。
- `parseCodexRetryAfter`：同文件 `:1834-1853`。
- `ResetQuota`：`sdk/cliproxy/auth/conductor.go:710-789`。
- 管理接口 `POST /v0/management/reset-quota`：`internal/api/handlers/management/quota.go:27-69`。

---

## 涉及文件

| 文件 | 操作 |
| --- | --- |
| `src/AITool.Web/Services/CodexQuotaCooldownService.cs` | 新建服务（冷却解析、应用、重置、恢复） |
| `src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs` | 错误处理分支接入冷却解析 |
| `src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs` | 同上（Responses 链路错误） |
| `src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Streaming.cs` | 同上（流式错误） |

参考：`ProxyForwardService` 的错误响应返回方式；CPA `codex_executor.go` 的判定逻辑。

---

## 详细步骤

### 1. 冷却服务 `CodexQuotaCooldownService`

```csharp
public interface ICodexQuotaCooldownService
{
    /// 判定一个上游错误响应是否为 Codex usage limit，若是则应用冷却。
    Task<bool> TryApplyCooldownFromErrorAsync(int httpStatus, string? responseBody, Guid linkedSiteId, CancellationToken ct);

    /// 重置：清冷却、重新启用 Site、刷新 token、失效缓存。
    Task ResetAsync(Guid codexAccountId, CancellationToken ct);
}
```

注入：`AppDbContext`（scoped）、`ProxyRequestMetadataCache`、`ICodexOAuthClient`（重置时刷新 token）。

### 2. 错误判定（照搬 CPA `isCodexUsageLimitError` + `parseCodexRetryAfter`）

```csharp
private static bool IsUsageLimitError(int httpStatus, string? body, out DateTimeOffset? coolingUntil)
{
    coolingUntil = null;
    if (httpStatus != 429 && httpStatus != 402) return false;  // CPA 主看 429；402 payment required 也可能

    // 解析 JSON 找 error.type == "usage_limit_reached"
    try {
        using var doc = JsonDocument.Parse(body ?? "");
        if (doc.RootElement.TryGetProperty("error", out var err)
            && err.TryGetProperty("type", out var type)
            && type.GetString() == "usage_limit_reached")
        {
            // 解析 resets_at（unix）或 resets_in_seconds
            if (err.TryGetProperty("resets_at", out var at) && at.TryGetInt64(out var unix))
                coolingUntil = DateTimeOffset.FromUnixTimeSeconds(unix);
            else if (err.TryGetProperty("resets_in_seconds", out var secs) && secs.TryGetInt64(out var s))
                coolingUntil = DateTimeOffset.UtcNow.AddSeconds(s);
            else
                coolingUntil = DateTimeOffset.UtcNow.AddMinutes(30);  // 无明确时间，默认冷却 30 分钟
            return true;
        }
    } catch { }
    return false;
}
```

> **关键区分（照搬 CPA）**：普通 `rate_limit_error` / `rate_limit_exceeded` 是**瞬时限流**（应重试），**不计入冷却**。只有 `usage_limit_reached` 才是额度耗尽，进入冷却。判定必须严格匹配 `usage_limit_reached`。

### 3. 应用冷却 `TryApplyCooldownFromErrorAsync`

```csharp
public async Task<bool> TryApplyCooldownFromErrorAsync(int httpStatus, string? body, Guid linkedSiteId, CancellationToken ct)
{
    if (!IsUsageLimitError(httpStatus, body, out var until) || until == null) return false;

    // 找 CodexAccount（按 LinkedSiteId）
    var account = await db.CodexAccounts.FirstOrDefaultAsync(a => a.LinkedSiteId == linkedSiteId, ct);
    if (account == null) return false;  // 非 Codex Site，不处理

    account.IsQuotaCooling = true;
    account.QuotaCoolingUntil = until;
    await db.UpdateColumnsAsync(account, nameof(account.IsQuotaCooling), nameof(account.QuotaCoolingUntil));

    // 禁用隐藏 Site，让转发链路绕开
    var site = await db.Sites.FirstAsync(s => s.Id == linkedSiteId, ct);
    if (site.IsEnabled) {
        site.IsEnabled = false;
        await db.UpdateColumnsAsync(site, nameof(site.IsEnabled));
        cache.InvalidateRouteTargets();
    }
    logger.LogWarning("Codex account {Id} cooling until {Until}", account.Id, until);
    return true;
}
```

### 4. 控制器错误分支接入

`OpenAiProxyController.cs` / `Responses.cs` / `Streaming.cs`：在转发返回**非成功**响应时，调用冷却解析。

```csharp
var upstreamResp = await _proxyForwardService.ForwardAsync(forwardRequest, ct);
if (!upstreamResp.IsSuccessStatusCode) {
    var body = await upstreamResp.Content.ReadAsStringAsync(ct);
    // 尝试 Codex 冷却解析（非 Codex Site 返回 false，零开销）
    await _cooldownService.TryApplyCooldownFromErrorAsync((int)upstreamResp.StatusCode, body, route.SiteId, ct);
}
```

> **接入点**：需 Read 三个控制器当前如何处理上游错误响应（是否已有统一错误透传逻辑），在透传**之前**或**同时**插入冷却解析。冷却解析失败/非 Codex 不影响错误透传给客户端。

### 5. 冷却到期自动恢复

用 BackgroundService（`CodexCooldownRecoveryService`）周期扫描，或在额度查询/转发路径惰性恢复。**建议 BackgroundService**（P7）：

```csharp
public sealed class CodexCooldownRecoveryService : BackgroundService
{
    // 每 2 分钟扫描到期的冷却
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
            var now = DateTimeOffset.UtcNow;
            var due = await db.CodexAccounts
                .Where(a => a.IsQuotaCooling && a.QuotaCoolingUntil != null && a.QuotaCoolingUntil <= now)
                .ToListAsync(stoppingToken);
            foreach (var account in due) {
                account.IsQuotaCooling = false;
                account.QuotaCoolingUntil = null;
                await db.UpdateColumnsAsync(account, nameof(account.IsQuotaCooling), nameof(account.QuotaCoolingUntil));
                // 仅当账号本身启用（非手动禁用）才恢复 Site
                if (account.IsEnabled) {
                    var site = await db.Sites.FirstAsync(s => s.Id == account.LinkedSiteId);
                    site.IsEnabled = true;
                    await db.UpdateColumnsAsync(site, nameof(site.IsEnabled));
                }
            }
            if (due.Any()) cache.InvalidateRouteTargets();
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }
}
```

> **注意**：恢复 Site 前检查 `account.IsEnabled`——若用户手动禁用，冷却到期不应自动重新启用（手动禁用优先）。

### 6. 重置额度 `ResetAsync`（前端 confirm 二次确认）

```csharp
public async Task ResetAsync(Guid codexAccountId, CancellationToken ct)
{
    var account = await db.CodexAccounts.FirstAsync(a => a.Id == codexAccountId, ct);

    // 1. 刷新 token（确保用新 token 重试，避免旧 token 仍触发限制）
    if (!string.IsNullOrEmpty(account.RefreshToken)) {
        var tokens = await _oauth.RefreshTokenAsync(account.RefreshToken, ct);
        account.AccessToken = tokens.AccessToken;
        account.RefreshToken = tokens.RefreshToken;
        account.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn);
        var site = await db.Sites.FirstAsync(s => s.Id == account.LinkedSiteId, ct);
        site.ApiKey = tokens.AccessToken;
        await db.UpdateColumnsAsync(site, nameof(site.ApiKey));
    }

    // 2. 清冷却
    account.IsQuotaCooling = false;
    account.QuotaCoolingUntil = null;
    account.IsEnabled = true;
    await db.UpdateColumnsAsync(account,
        nameof(account.AccessToken), nameof(account.RefreshToken), nameof(account.TokenExpiresAt),
        nameof(account.IsQuotaCooling), nameof(account.QuotaCoolingUntil), nameof(account.IsEnabled));

    // 3. 恢复 Site
    var linkedSite = await db.Sites.FirstAsync(s => s.Id == account.LinkedSiteId, ct);
    linkedSite.IsEnabled = true;
    await db.UpdateColumnsAsync(linkedSite, nameof(linkedSite.IsEnabled));

    cache.InvalidateRouteTargets();
}
```

---

## 性能考量

### 引用原则
- **P1 缓存失效**：应用冷却 / 重置 / 恢复后失效。
- **P7 后台服务节流**：恢复服务 2 分钟周期。
- **P10 热路径**：冷却判定**只在错误分支**，正常请求零开销。

### 本任务特有
- **错误分支开销**：正常转发不解析 body。仅当上游返回非 2xx 才 `ReadAsStringAsync` + JSON 解析。非 Codex Site 调用 `TryApplyCooldownFromErrorAsync` 时 `FirstOrDefaultAsync` 查 CodexAccount 返回 null，快速返回。可加一层 in-memory「SiteId 是否 Codex」快速判断（可选优化）。
- **冷却状态热读**：转发主链路**不读** `IsQuotaCooling`（靠 `Site.IsEnabled` 在缓存里已排除冷却中的 Site）。冷却字段只供面板展示与恢复服务扫描。
- **恢复服务查询**：`Where(IsQuotaCooling && QuotaCoolingUntil <= now)`，`QuotaCoolingUntil` 无索引，但冷却账号数量极少（个位数），扫描可忽略。
- **列更新**：全程 `UpdateColumns` 指定列，避免覆盖并发更新（T08 刷新 token、T09 额度查询）。
- **重置不阻塞**：重置是管理操作（低频），`await` 刷新 token 可接受（秒级）。

---

## 验收标准

1. 转发命中上游 `usage_limit_reached`（429）→ 账号标记冷却、Site 禁用、缓存失效、转发绕开。
2. 普通 `rate_limit_exceeded` → **不**进入冷却（仅瞬时限流，由现有重试/fallback 处理）。
3. 冷却到期（`QuotaCoolingUntil <= now`）→ 恢复服务自动清冷却、恢复 Site（若账号未手动禁用）。
4. 手动禁用的账号，冷却到期**不**自动恢复。
5. 重置额度（confirm 后）→ 刷新 token、清冷却、恢复 Site、缓存失效，账号可重新调用。
6. 错误透传给客户端不受影响（冷却解析在透传之外或之后）。

---

## 风险

- **错误响应结构**：CPA 的判定基于 `error.type`/`error.resets_at`。实际上游结构需实测（与 T09 同样需真实 token 验证）。若结构与预期不符，调整解析。**建议动工前用真实 Codex token 触发一次 usage_limit（耗尽额度）抓响应**。
- **状态码范围**：CPA 主看 429。也可能有 402（payment required）。实现时宽松处理 429/402 + body 验证。
- **冷却与手动禁用冲突**：冷却到期恢复时若用户已手动禁用，不应恢复（上方已检查 `account.IsEnabled`）。但「冷却中用户手动禁用」时，`account.IsEnabled=false`，恢复服务跳过——正确。
- **重置语义**：CPA 的 `ResetQuota` 清 cooldown + resume routing。本任务额外刷新 token（确保新 token 重试）。**重置不能真正增加上游额度**（额度由上游重置周期决定），只是清除本地冷却状态让账号重新参与转发——若上游额度仍未恢复，会立即再次触发冷却。文案需向用户说明。
- **流式错误解析**：流式响应错误可能出现在 SSE 中途，解析更复杂（需解析 SSE 事件）。CPA 在 executor 层统一处理。本期可先覆盖非流式 + 流式启动错误；流式中途错误的冷却解析作为增强项。
- **接入点遗漏**：必须在所有 Codex 可能命中的转发入口（chat/completions 经 Responses 桥接、responses HTTP、responses WebSocket、流式）的错误分支接入。漏一处导致该路径超限不冷却。全局搜索转发错误处理点。
