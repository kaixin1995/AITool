# T08 — Token 自动刷新后台服务

> 状态：已完成 ✅
> 前置依赖：T01（数据模型）、T02（OAuth 客户端，refresh + single-flight）、T04（Provisioner，更新 token 模式）
> 关联总览章节：横切性能原则 P1 / P5 / P7 / P8

## 实施记录

- 新建 `src/AITool.Web/Services/CodexTokenRefreshService.cs`：`BackgroundService`，ScanInterval=5min、RefreshLead=1h、InterAccountDelay=500ms。
- 测试环境跳过；循环 try/catch + Task.Delay；RefreshLead 临期判定含 `TokenExpiresAt==null`（保守刷新）。
- `OrderBy(TokenExpiresAt)` 错峰；每账号刷新后同步 `LinkedSite.ApiKey`；轮末一次性 `InvalidateRouteTargets()`。
- 复用 OAuth 客户端 single-flight；refresh_token 以返回值覆盖（兼容轮换）；ExpiresIn<=0 时兜底 3600。
- Program.cs 注册 `AddHostedService<CodexTokenRefreshService>()`。
- 编译通过。
- 待办（需实测后调）：RefreshLead=1h 为保守默认，若实测 token 有效期 < 1h 需调小并缩短 ScanInterval。

## 目标

实现 `CodexTokenRefreshService : BackgroundService`，周期扫描临期的 Codex 账号，用 refresh_token 刷新 access_token，写回 `CodexAccount` + `LinkedSite.ApiKey` + 失效缓存。保证转发链路始终用未过期 token。

对应 CPA：`sdk/cliproxy/auth/conductor.go` 的 `authAutoRefreshLoop`；CPA 的 `CodexAuthenticator.RefreshLead()` = 5 天提前量（`sdk/auth/codex.go:34-36`）。

---

## 涉及文件

| 文件 | 操作 |
| --- | --- |
| `src/AITool.Web/Services/CodexTokenRefreshService.cs` | 新建 BackgroundService |
| `src/AITool.Web/Program.cs` | `AddHostedService<CodexTokenRefreshService>()` 注册 |

参考现有 BackgroundService：`Program.cs:120/130/132/153`（`AddHostedService` 用法）、`MemoryMaintenanceService` 等周期服务样板。

---

## 详细步骤

### 1. 服务骨架

```csharp
public sealed class CodexTokenRefreshService : BackgroundService
{
    // 周期：每 N 分钟扫描一次（建议 5 分钟）
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(5);
    // 提前刷新量：到期前多久就刷新（建议 1 小时，比 CPA 5 天激进——Codex access_token 通常 1 小时级）
    private static readonly TimeSpan RefreshLead = TimeSpan.FromHours(1);
    // 失败退避：刷新失败的账号下次重试前等待
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(15);

    private readonly IServiceProvider _services;
    private readonly ICodexOAuthClient _oauth;
    private readonly ILogger<CodexTokenRefreshService> _logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshDueAccountsAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Codex token refresh loop error"); }
            await Task.Delay(ScanInterval, stoppingToken);
        }
    }
}
```

### 2. 扫描临期账号 `RefreshDueAccountsAsync`

```csharp
private async Task RefreshDueAccountsAsync(CancellationToken ct)
{
    using var scope = _services.CreateScope();  // BackgroundService 是 singleton，需建 scope 取 scoped AppDbContext
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();

    var now = DateTimeOffset.UtcNow;
    var due = await dbContext.CodexAccounts
        .Where(a => a.IsEnabled
                 && !string.IsNullOrEmpty(a.RefreshToken)
                 && (a.TokenExpiresAt == null                          // 无过期时间，保守刷新
                     || a.TokenExpiresAt <= now + RefreshLead))        // 临期
        .OrderBy(a => a.TokenExpiresAt)                                // 错峰：最紧迫的先刷
        .ToListAsync(ct);

    foreach (var account in due)
    {
        if (ct.IsCancellationRequested) break;
        await RefreshOneAsync(dbContext, cache, account, ct);
        // 错峰：每两次刷新之间小延迟，避免瞬间打满上游
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
    }
}
```

> **查询只取必要字段**：上方 `.ToListAsync()` 取全实体（字段不多），可接受。若想优化，`.Select(a => new { a.Id, a.RefreshToken, a.LinkedSiteId, a.TokenExpiresAt })` 投影，但刷新后要更新全实体，权衡后建议取全实体（更新方便）。

### 3. 刷新单个账号 `RefreshOneAsync`

```csharp
private async Task RefreshOneAsync(AppDbContext db, ProxyRequestMetadataCache cache, CodexAccount account, CancellationToken ct)
{
    try
    {
        // single-flight：OAuth 客户端内部保证同 refresh_token 并发只刷一次
        var tokens = await _oauth.RefreshTokenAsync(account.RefreshToken!, ct);

        account.AccessToken = tokens.AccessToken;
        account.RefreshToken = tokens.RefreshToken;  // 部分上游轮换 refresh_token，更新
        if (!string.IsNullOrEmpty(tokens.IdToken)) account.IdToken = tokens.IdToken;
        account.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn);
        account.LastRefreshAt = DateTimeOffset.UtcNow;
        await db.UpdateAsync(account, ct);

        // 同步写回隐藏 Site.ApiKey
        var site = await db.Sites.FirstAsync(s => s.Id == account.LinkedSiteId, ct);
        site.ApiKey = tokens.AccessToken;
        await db.UpdateAsync(site, ct);

        cache.InvalidateRouteTargets();  // 让转发链路 5s 内拿到新 token
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Refresh failed for Codex account {Id}", account.Id);
        // 失败不立即重试，等下一轮（FailureBackoff 由「临期判定」自然体现——token 仍临期会再扫到）
    }
}
```

### 4. refresh_token 轮换处理

部分 OAuth 提供商每次刷新会**轮换 refresh_token**（返回新的）。必须用返回的新 refresh_token 覆盖旧值（上方 `account.RefreshToken = tokens.RefreshToken`）。**若沿用旧 refresh_token 二次刷新，会触发 `refresh_token_reused` 错误**（CPA `isNonRetryableRefreshErr` 对此停止重试）。

> 实现时确认 OpenAI Codex 是否轮换。无论是否轮换，用返回值覆盖都是安全的（不轮换时返回相同值）。

### 5. 注册

`Program.cs`：

```csharp
builder.Services.AddHostedService<CodexTokenRefreshService>();
```

> 注意：`AddHostedService` 注册为 singleton。注入 `IServiceProvider`，在循环内建 scope 取 scoped 服务（`AppDbContext` 是 scoped）。

### 6. 启动时机

`BackgroundService` 在应用启动后自动运行 `ExecuteAsync`。首扫可能此时 DB 未就绪——`try/catch` 包裹，下一轮重试即可。无需特殊启动顺序处理。

---

## 性能考量

### 引用原则
- **P1 缓存失效**：每次成功刷新后 `InvalidateRouteTargets()`。
- **P5 HttpClient 复用**：经 OAuth 客户端（`AddHttpClient` 注册）。
- **P7 后台服务节流**：周期 + 错峰 + 失败退避。
- **P8 single-flight**：与 OAuth 客户端协同，避免重复刷新。

### 本任务特有
- **错峰（thundering herd 规避）**：`OrderBy(TokenExpiresAt)` + 每两次刷新间 `Task.Delay(500ms)`。避免一轮内几十个账号同时打上游导致限流。
- **RefreshLead 取值**：CPA 用 5 天（Codex CLI token 有效期较长）。AITool 场景建议 1 小时（保守，避免边界过期）。若实测 token 有效期 < 1 小时，调小 RefreshLead 并相应缩短 ScanInterval。
- **ScanInterval**：5 分钟。token 有效期通常 ≥ 1 小时，5 分钟扫描足够提前。不可过短（无谓上游压力）。
- **失败退避**：刷新失败不重试，等下一轮扫描。失败的账号因 `TokenExpiresAt` 未更新仍「临期」，下轮会再扫到——天然退避一个 ScanInterval（5 分钟）。若想更长退避，记录 `LastRefreshFailedAt`，跳过近期待重试（优化项，本期可不做）。
- **scope 创建**：每轮扫描建一个 scope，不每账号建（一个 scope 内多次 db 操作可接受）。SqlSugarScope 是 singleton 底层连接池，scope 仅是 DI 容器范围。
- **single-flight 协同**：自动刷新与 T11 手动刷新 token 可能并发触发同一账号。OAuth 客户端的 single-flight 保证只一次真实上游请求。两者都更新 DB，最后写赢（同 token 值，无冲突）。
- **取消响应**：`Task.Delay` 与循环都传 `stoppingToken`，应用停止时快速退出。

---

## 验收标准

1. 服务随应用启动自动运行。
2. 临期账号（`TokenExpiresAt <= now + RefreshLead`）在 5 分钟内被刷新，DB 的 AccessToken/TokenExpiresAt/LastRefreshAt 更新。
3. 刷新成功后 `LinkedSite.ApiKey` 同步更新，缓存失效，转发链路用新 token。
4. refresh_token 轮换时新 token 被保存。
5. 刷新失败不崩溃，下一轮重试；日志记录失败。
6. 同一账号并发刷新（手动 + 自动）只触发一次真实上游请求（single-flight）。
7. 应用停止时服务及时退出。

---

## 风险

- **RefreshLead 与 token 有效期不匹配**：若 token 有效期 < RefreshLead（如 30 分钟 token + 1 小时 lead），会导致**每次扫描都在刷新**（始终临期）。浪费上游配额。**实现前实测 token 有效期**（`expires_in` 返回值），据此设 RefreshLead = 有效期 × 0.2 左右。
- **refresh_token 失效**：长期不用或上游策略导致 refresh_token 失效。此时刷新持续失败，账号 token 过期后转发会 401。**建议**：刷新失败 N 次后标记账号需重新授权（前端提示），本期可仅日志告警。
- **时区**：`TokenExpiresAt` 用 UTC 比较（`DateTimeOffset.UtcNow`），DB 存储由 AOP 转本地，读回调 UTC（P3）。比较逻辑用 UTC 一致。
- **并发更新覆盖**：自动刷新与手动刷新/额度查询同时更新同一 CodexAccount 行。SqlSugar `UpdateAsync` 是全字段更新，可能互相覆盖（如额度查询更新 LastQuotaRawJson 时被刷新覆盖回旧值）。**建议**：更新时只更新本任务相关字段（`UpdateColumns` 指定列），而非全实体。SqlSugar 支持 `db.Updateable(entity).UpdateColumns(...)`。详见实现时 SqlSugar 列更新 API。
- **首扫 DB 未就绪**：启动瞬间表可能未建完。`InitializeDatabase` 在 `Program.cs:169` 早于 hosted service 启动，理论上就绪。`try/catch` 兜底。
