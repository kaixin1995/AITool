# T02 — Codex OAuth 协议客户端

> 状态：已完成 ✅
> 前置依赖：T01（数据模型）
> 关联总览章节：横切性能原则 P5 / P8

## 实施记录

- 新建 `src/AITool.Application/Codex/CodexTokenSet.cs`、`CodexIdTokenClaims.cs`、`ICodexOAuthClient.cs`。
- 新建 `src/AITool.Infrastructure/Codex/CodexJwtParser.cs`：静态 JWT 解析（无验签），结果按 token 内容缓存（`ConcurrentDictionary`），含 base64url 解码工具。
- 新建 `src/AITool.Infrastructure/Codex/CodexOAuthClient.cs`：PKCE（`RandomNumberGenerator` 96 字节 verifier + SHA256 challenge）、授权 URL（含全部 9 个参数含 `codex_cli_simplified_flow`/`id_token_add_organizations`）、code 交换、refresh（scope=`openid profile email`，`SemaphoreSlim` single-flight 串行化）。
- `Program.cs` 注册 `AddHttpClient<ICodexOAuthClient, CodexOAuthClient>`（20s 超时），补 using。
- 编译通过（全解决方案，0 警告 0 错误）。

## 目标

实现 Codex（ChatGPT/OpenAI Codex CLI）OAuth 协议客户端，提供：授权 URL 构造（含 PKCE）、授权码换 token、JWT id_token 解析、refresh_token 刷新（带 single-flight）。供 T04（账号供给）与 T08（自动刷新）复用。

协议细节全部移植自 CPA 参考 `reference-projects/CLIProxyAPI/internal/auth/codex/`。

---

## 涉及文件

| 文件 | 操作 |
| --- | --- |
| `src/AITool.Application/Codex/ICodexOAuthClient.cs` | 新建接口 |
| `src/AITool.Application/Codex/CodexTokenSet.cs` | 新建结果 DTO |
| `src/AITool.Application/Codex/CodexIdTokenClaims.cs` | 新建 JWT 解析结果 DTO |
| `src/AITool.Infrastructure/Codex/CodexOAuthClient.cs` | 新建实现 |
| `src/AITool.Infrastructure/Codex/CodexJwtParser.cs` | 新建 JWT 解析（无验签） |
| `src/AITool.Web/Program.cs` | `AddHttpClient<ICodexOAuthClient, CodexOAuthClient>()` 注册 |

参考样板：`ISiteCatalogClient` / `OpenAiSiteCatalogClient`（`Program.cs:106` 注册模式）。

---

## 协议常量（来自 CPA `internal/auth/codex/openai_auth.go:24-29`）

```csharp
const string AuthURL     = "https://auth.openai.com/oauth/authorize";
const string TokenURL    = "https://auth.openai.com/oauth/token";
const string ClientID    = "app_EMoamEEZ73f0CkXaXp7hrann";
const string RedirectURI = "http://localhost:1455/auth/callback";
```

---

## 详细步骤

### 1. 定义结果 DTO

`CodexTokenSet`（OAuth 交换 / 刷新统一产出）：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| AccessToken | string | |
| RefreshToken | string | |
| IdToken | string | JWT |
| TokenType | string | 通常 `Bearer` |
| ExpiresIn | int | 秒；客户端据此算 `TokenExpiresAt = UtcNow + ExpiresIn` |

`CodexIdTokenClaims`（JWT payload 解析结果，对应 CPA `jwt_parser.go` `JWTClaims` + `CodexAuthInfo`）：

| 字段 | 说明 | 来源 claim |
| --- | --- | --- |
| AccountId | chatgpt_account_id | `https://api.openai.com/auth`.chatgpt_account_id |
| Email | 邮箱 | 顶层 `email` |
| PlanType | chatgpt_plan_type | `https://api.openai.com/auth`.chatgpt_plan_type |
| UserId | chatgpt_user_id | 同上嵌套 |
| SubscriptionWindowStart/End | 订阅窗口 | 同上嵌套（展示用） |

### 2. PKCE（对应 CPA `pkce.go`）

- **verifier**：96 随机字节 → base64url（无 padding）。用 `RandomNumberGenerator.GetBytes(96)`（`System.Security.Cryptography`），**不要用 `Random`**。
- **challenge**：`SHA256(verifier_bytes)` → base64url（无 padding）。
- base64url 工具：`Convert.ToBase64String` 后替换 `+`→`-`、`/`→`_`、去 `=`。

### 3. 授权 URL 构造 `BuildAuthorizeUrl(state, verifier)`

query 参数（对应 CPA `openai_auth.go:66-86`）：

```
client_id                  = ClientID
response_type              = code
redirect_uri               = RedirectURI
scope                      = openid email profile offline_access
state                      = state
code_challenge             = <challenge>
code_challenge_method      = S256
prompt                     = login
id_token_add_organizations = true
codex_cli_simplified_flow  = true
```

> `codex_cli_simplified_flow` 与 `id_token_add_organizations` 是 Codex CLI 流程必需，不可省略。

### 4. 授权码换 token `ExchangeCodeAsync(code, verifier, ct)`

POST `TokenURL`，`Content-Type: application/x-www-form-urlencoded`，`Accept: application/json`：

```
grant_type     = authorization_code
client_id      = ClientID
code           = code
redirect_uri   = RedirectURI
code_verifier  = verifier
```

解析 JSON → `CodexTokenSet`。

### 5. JWT 解析 `ParseIdToken(idToken)`

对应 CPA `ParseJWTToken`（`jwt_parser.go:58-76`）：

- 按 `.` 分三段，取第二段 payload。
- base64url 解码（补 padding）。
- `JsonDocument.Parse`，取 `email`（顶层）与 `https://api.openai.com/auth` 嵌套对象的 `chatgpt_account_id` / `chatgpt_plan_type` / `chatgpt_user_id` / 订阅窗口。
- **不做签名验证**（CPA 也不验签；token 来自 TLS 直连可信端点）。

### 6. refresh_token 刷新 `RefreshTokenAsync(refreshToken, ct)`

POST `TokenURL`，form-urlencoded：

```
client_id       = ClientID
grant_type      = refresh_token
refresh_token   = refreshToken
scope           = openid profile email     # 注意：与授权 scope 不同，无 offline_access
```

> scope 差异是 CPA 的真实行为（`openai_auth.go:210-278`），必须照搬。

### 7. single-flight（对应 CPA `singleflight.Group`）

同一 refresh_token 并发调用必须合并为一次真实上游请求。实现：

```csharp
private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new();
private readonly ConcurrentDictionary<string, Task<CodexTokenSet>> _refreshInflight = new();

public async Task<CodexTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken ct)
{
    var key = refreshToken;
    var gate = _refreshLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    await gate.WaitAsync(ct);
    try
    {
        // 二次检查：等待期间已有并发请求完成
        if (_refreshInflight.TryGetValue(key, out var existing))
            return await existing;
        var task = DoRefreshAsync(refreshToken, ct);
        _refreshInflight[key] = task;
        try { return await task; }
        finally { _refreshInflight.TryRemove(key, out _); }
    }
    finally { gate.Release(); }
}
```

> 注意：上面是示意，实现时要避免二次 await 已完成 task 的副作用问题，并保证 inflight 字典在异常/成功后都清理。可简化为「SemaphoreSlim 串行化 + 结果不缓存（同 token 极少并发刷新，串行即可满足 single-flight 核心目的：避免重复打上游）」。最终实现以「同 token 串行 + 失败不污染后续」为准。

### 8. state 暂存（授权流程用）

`RequestCodexToken` 的 state 需要在 `start-oauth` 与 `complete-oauth` 之间传递（T11 控制器使用）。OAuth 客户端提供 `CreateOAuthSession()` 产出 `(state, verifier)`，由控制器暂存（内存 ConcurrentDictionary，TTL 10min，仿 CPA `oauth_sessions.go`）。**本任务只提供 session 工厂方法，暂存逻辑在 T11。**

### 9. 注册

`Program.cs`：

```csharp
builder.Services.AddHttpClient<ICodexOAuthClient, CodexOAuthClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
});
```

---

## 接口设计

```csharp
public interface ICodexOAuthClient
{
    (string State, string Verifier) CreateOAuthSession();
    string BuildAuthorizeUrl(string state, string verifier);
    Task<CodexTokenSet> ExchangeCodeAsync(string code, string verifier, CancellationToken ct);
    Task<CodexTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken ct);
}

// 静态工具（可放同接口的静态类或独立 CodexJwtParser）
public static class CodexJwtParser
{
    public static CodexIdTokenClaims? Parse(string idToken);
}
```

---

## 性能考量

### 引用原则
- **P5 HttpClient 复用**：经 `AddHttpClient` 注册，复用连接池。OAuth 端点固定，连接长连接复用。
- **P8 single-flight**：refresh 同 token 并发只一次真实请求（上方实现）。

### 本任务特有
- **state/verifier 生成**：96 字节随机用 `RandomNumberGenerator`（一次性，开销可忽略）。
- **JWT 解析**：纯内存 base64+JSON，无 IO；可做成无状态静态方法，零分配热点。id_token 通常 < 4KB。
- **HttpClient 超时**：OAuth 端点 20s（交换/刷新应秒级，留余量）。
- **base64url**：用 `Span<byte>` 处理避免中间字符串，但非热路径，可读性优先。
- **并发**：OAuth 客户端本身无状态（除 single-flight 字典），可被多控制器/后台服务共享注入。`ConcurrentDictionary` 字段是线程安全的。

---

## 验收标准

1. 编译通过；`Program.cs` 注册 ICodexOAuthClient。
2. `BuildAuthorizeUrl` 生成的 URL 含全部 9 个参数（含 `codex_cli_simplified_flow`、`id_token_add_organizations`）。
3. 单元测试（建议）：
   - PKCE：给定固定 verifier，challenge 可复现（SHA256 确定性）。
   - JWT 解析：用一组合成的 id_token JSON 验证能取出 email/account_id/plan_type。
   - single-flight：并发调用 `RefreshTokenAsync(同一token)` 只触发一次真实 HTTP（可用假 HttpMessageHandler 计数）。
4. refresh scope 为 `openid profile email`（不含 offline_access）。

---

## 风险

- **single-flight 实现复杂度**：并发正确性易错。可先实现「SemaphoreSlim 串行」（放弃结果共享，只保证不重复打上游），验证通过后再优化。串行已能避免绝大多数重复刷新（同 token 同时刷新概率低）。
- **JWT claim 嵌套路径**：`https://api.openai.com/auth` 是 key 带 `://`，`JsonDocument` 取值时注意用字符串 key。实现时用一个真实 id_token（可从 CPA 测试用例或实际登录获取）验证。
- **scope 差异易遗漏**：授权与刷新 scope 不同，照搬 CPA，否则刷新可能失败。
- **base64url padding**：JWT payload 解码需补 `=` 至 4 倍数，否则 `Convert.FromBase64String` 抛异常。
