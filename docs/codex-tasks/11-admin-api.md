# T11 — 管理 API 控制器

> 状态：已完成 ✅
> 前置依赖：T01～T10 全部后端能力
> 关联总览章节：横切性能原则 P1 / P2 / P6 / P8

## 实施记录

- 新建 `src/AITool.Web/Controllers/Admin/CodexApiController.cs`（`[Route("api/admin/codex")]`）。
- 全部端点实现：start-oauth、complete-oauth、import-credential（multipart/raw + 207 部分成功）、accounts(GET)、refresh-quota、reset-quota、toggle、delete、update(PUT)、refresh-token、pull-models。
- OAuth 会话 `ConcurrentDictionary` 暂存（state→verifier，TTL 10min），StartOAuth 时顺带清理过期项防泄漏。
- complete-oauth 解析回调 URL（容错非法 URL）；state 消费即删。
- ToSummary 解析 LastQuotaRawJson 的 remaining/unit 供列表展示；避免 N+1（单次 ToListAsync）。
- 编译通过。

## 目标

实现 `CodexApiController`，集中暴露 Codex 账号管理的全部 HTTP API，供前端 T12 调用。涵盖：OAuth 登录流程、凭证导入、账号列表、额度查询/重置、启用/禁用、删除、编辑、token 刷新、模型拉取。

路由前缀 `[Route("api/admin/codex")]`，自动受 `/api/admin/*` 鉴权保护（已核查 `Program.cs:261-300`）。

---

## 涉及文件

| 文件 | 操作 |
| --- | --- |
| `src/AITool.Web/Controllers/Admin/CodexApiController.cs` | 新建控制器 |
| `src/AITool.Application/Codex/*Dto.cs` | 新建请求/响应 DTO |

参考：`SiteCatalogApiController.cs`（上游拉取 + import 范式）、`ChatApiController.cs`（控制器注入模式）。

---

## 详细步骤

### 1. 控制器骨架

```csharp
[ApiController]
[Route("api/admin/codex")]
[Authorize]
public sealed class CodexApiController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICodexOAuthClient _oauth;
    private readonly ICodexAccountProvisioner _provisioner;
    private readonly ICodexModelFetcher _modelFetcher;
    private readonly ICodexQuotaService _quotaService;
    private readonly ICodexQuotaCooldownService _cooldownService;
    // ...
}
```

### 2. OAuth 登录流程（手动粘贴回调 URL）

```csharp
public sealed record StartOAuthResponse(string Url, string State);

private static readonly ConcurrentDictionary<string, OAuthSession> _sessions = new();
private sealed record OAuthSession(string State, string Verifier, DateTimeOffset ExpiresAt);

[HttpPost("start-oauth")]
public ActionResult<StartOAuthResponse> StartOAuth()
{
    var (state, verifier) = _oauth.CreateOAuthSession();
    var url = _oauth.BuildAuthorizeUrl(state, verifier);
    _sessions[state] = new OAuthSession(state, verifier, DateTimeOffset.UtcNow.AddMinutes(10));
    return new StartOAuthResponse(url, state);
}

[HttpPost("complete-oauth")]
public async Task<ActionResult<CodexAccountSummary>> CompleteOAuth([FromBody] CompleteOAuthRequest req, CancellationToken ct)
{
    // req.CallbackUrl = "http://localhost:1455/auth/callback?code=...&state=..."
    // 解析 code + state
    var query = HttpUtility.ParseQueryString(new Uri(req.CallbackUrl).Query);
    var code = query["code"];
    var state = query["state"];
    if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        return BadRequest("回调 URL 缺少 code 或 state");

    if (!_sessions.TryRemove(state, out var session))
        return BadRequest("state 无效或已过期（10 分钟）");
    if (session.ExpiresAt < DateTimeOffset.UtcNow)
        return BadRequest("state 已过期，请重新开始登录");

    var tokens = await _oauth.ExchangeCodeAsync(code, session.Verifier, ct);
    var claims = CodexJwtParser.Parse(tokens.IdToken);

    var input = new CodexProvisionInput {
        DisplayName = req.DisplayName ?? claims?.Email ?? "Codex 账号",
        AccessToken = tokens.AccessToken,
        RefreshToken = tokens.RefreshToken,
        IdToken = tokens.IdToken,
        AccountId = claims?.AccountId,
        Email = claims?.Email,
        PlanType = claims?.PlanType,
        TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn),
    };
    var account = await _provisioner.ProvisionFromTokensAsync(input, ct);
    return Ok(ToSummary(account));
}
```

> **state 清理**：`TryRemove` 消费即删；过期 session 由后台或惰性清理（TTL 10min）。建议加一个简单清理：每次 StartOAuth 时顺手清掉过期项。

### 3. 凭证导入

```csharp
[HttpPost("import-credential")]
public async Task<ActionResult> ImportCredential([FromQuery] string? name, CancellationToken ct)
{
    // 支持 multipart 多文件 或 raw body 单文件
    var results = new List<CodexCredentialParseResult>();

    if (Request.HasFormContentType && Request.Form.Files.Count > 0) {
        foreach (var file in Request.Form.Files) {
            using var sr = new StreamReader(file.OpenReadStream());
            var json = await sr.ReadToEndAsync(ct);
            results.Add(CodexCredentialParser.Parse(json, file.FileName));
        }
    } else {
        using var sr = new new StreamReader(Request.Body);
        var json = await sr.ReadToEndAsync(ct);
        results.Add(CodexCredentialParser.Parse(json, name));
    }

    // 对每个成功的解析结果调用 Provisioner（批量）
    var summaries = new List<CodexAccountSummary>();
    foreach (var r in results.Where(r => r.Success)) {
        var input = new CodexProvisionInput {
            DisplayName = r.DisplayName!,
            AccessToken = r.AccessToken!, RefreshToken = r.RefreshToken!,
            IdToken = r.IdToken!, AccountId = r.AccountId, Email = r.Email,
            PlanType = r.PlanType, TokenExpiresAt = r.TokenExpiresAt,
        };
        summaries.Add(ToSummary(await _provisioner.ProvisionFromTokensAsync(input, ct)));
    }

    if (results.Any(r => !r.Success))
        return StatusCode(207, new { successes = summaries, failures = results.Where(r => !r.Success) });
    return Ok(new { successes = summaries });
}
```

> 207 Multi-Status 风格：部分成功部分失败时返回，前端分别展示。

### 4. 账号列表（避免 N+1）

```csharp
[HttpGet("accounts")]
public async Task<ActionResult<List<CodexAccountSummary>>> ListAccounts(CancellationToken ct)
{
    // P2 内存 join：一次载入所有账号
    var accounts = await _db.CodexAccounts.OrderByDescending(a => a.CreatedAt).ToListAsync(ct);
    return Ok(accounts.Select(ToSummary).ToList());
}

private static CodexAccountSummary ToSummary(CodexAccount a) => new() {
    Id = a.Id, DisplayName = a.DisplayName, Email = a.Email, AccountId = a.AccountId,
    PlanType = a.PlanType, IsEnabled = a.IsEnabled,
    IsQuotaCooling = a.IsQuotaCooling, QuotaCoolingUntil = a.QuotaCoolingUntil,
    AutoDisableThreshold = a.AutoDisableThreshold,
    LastQuotaCheckedAt = a.LastQuotaCheckedAt,
    // 额度数字从 LastQuotaRawJson 解析（若 T09 解析存了结构化字段，直接用）
    // 或前端单独调 refresh-quota 拿最新
};
```

> **额度数字**：列表 Summary 可带最近缓存的额度（从 LastQuotaRawJson 反序列化或 T09 缓存的字段）。若担心列表慢，额度数字由前端展开时单独调 refresh-quota。

### 5. 额度查询/重置/启用禁用/删除/编辑/刷新token/拉模型

| 端点 | 方法 | 说明 |
| --- | --- | --- |
| `accounts/{id}/refresh-quota` | POST | `_quotaService.QueryAsync(account, forceRefresh:true)`，返回 CodexQuotaInfo |
| `accounts/{id}/reset-quota` | POST | `_cooldownService.ResetAsync(id)`（**前端 confirm 二次确认**） |
| `accounts/{id}/toggle` | POST | 切换 IsEnabled + 同步 LinkedSite.IsEnabled + invalidate |
| `accounts/{id}` | DELETE | `_provisioner.DeprovisionAsync(id)`（前端 confirm） |
| `accounts/{id}` | PUT | 编辑 DisplayName / AutoDisableThreshold：`_provisioner.UpdateAsync` |
| `accounts/{id}/refresh-token` | POST | 手动 `_oauth.RefreshTokenAsync` + 更新 |
| `accounts/{id}/pull-models` | POST | `_modelFetcher.FetchAsync` + Provisioner upsert 映射 |

每个端点统一：找不到账号返回 404，成功返回 200 + Summary。

### 6. DTO 定义

```csharp
public sealed record CompleteOAuthRequest(string CallbackUrl, string? DisplayName);
public sealed record CodexAccountSummary(
    Guid Id, string DisplayName, string? Email, string? AccountId, string? PlanType,
    bool IsEnabled, bool IsQuotaCooling, DateTimeOffset? QuotaCoolingUntil,
    decimal? AutoDisableThreshold, DateTimeOffset? LastQuotaCheckedAt, /* + 额度字段 */);
public sealed record UpdateAccountRequest(string DisplayName, decimal? AutoDisableThreshold);
```

### 7. 错误响应风格

统一现有项目风格：`BadRequest(new { message = "..." })` / `NotFound()` / `StatusCode(207, ...)`。参考 `SiteCatalogApiController` 错误返回。

---

## 性能考量

### 引用原则
- **P1 缓存失效**：toggle / delete / edit / reset / pull-models 后失效。
- **P2 内存 join**：列表查询一次载入。
- **P6 批量**：导入批量供给。
- **P8 single-flight**：refresh-token / refresh-quota 走服务层 single-flight。

### 本任务特有
- **列表无 N+1**：单次 `.ToListAsync()` 取全部账号，内存映射 Summary。不每账号查额度（额度走缓存或展开时单独查）。
- **state 暂存清理**：`_sessions` ConcurrentDictionary，StartOAuth 时顺带 `ClearExpired()`（O(n) 但 n 小）。防止内存泄漏（用户开始登录但未完成）。
- **批量导入并发**：逐文件供给（Provisioner 内部有事务/批量 upsert）。多文件可串行（账号少，开销低）。
- **并发账号操作加锁**：同一账号的 toggle/reset/refresh 并发可能冲突。服务层（T08/T09/T10）已用列更新缓解。控制器层不额外加锁（低频管理操作）。
- **响应大小**：accounts 列表 Summary 含额度字段，账号量级小（几十），响应 KB 级，无压力。
- **拉取模型限频**：`pull-models` 加单账号最小间隔校验（如 60s 内重复返回 429 或上次结果），保护上游。

---

## 验收标准

1. 全部端点可达，鉴权生效（未登录 401）。
2. start-oauth → complete-oauth 流程跑通（手动粘贴回调 URL）。
3. import-credential 支持 multipart 多文件与 raw body。
4. accounts 列表无 N+1，响应含状态与额度。
5. toggle/reset/delete/edit/refresh-token/pull-models 各自生效并失效缓存。
6. 错误返回统一风格。

---

## 风险

- **state 过期清理**：若不清理，长期累积泄漏。务必 StartOAuth 时清理过期项。
- **回调 URL 解析**：用户粘贴的可能带额外参数或编码。`HttpUtility.ParseQueryString(new Uri(url).Query)` 容错。若 URL 格式非法（用户粘贴错），`new Uri` 抛异常 → 捕获返回 BadRequest。
- **DisplayName 重复**：不强制唯一，但建议前端提示。
- **拉取模型失败**：上游不可达时 pull-models 返回错误，账号映射不受影响（保留静态目录）。
- **导入大小限制**：multipart 总大小与单文件大小限制（Kestrel 默认或配置），防 OOM。
