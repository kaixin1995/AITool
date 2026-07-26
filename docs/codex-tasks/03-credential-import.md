# T03 — 凭证文件导入解析

> 状态：已完成 ✅
> 前置依赖：T01（数据模型）、T02（OAuth 客户端，复用 JWT 解析）
> 关联总览章节：横切性能原则 P5 / P6

## 实施记录

- 新建 `src/AITool.Application/Codex/CodexCredentialParseResult.cs`：含 TypeInferred（宽松推断标记）。
- 新建 `src/AITool.Infrastructure/Codex/CodexCredentialParser.cs`：静态类，`Parse`（单文件，type 严格 codex 但缺失时宽松推断）+ `ParseMany`（批量，不中断）；id_token JWT 解析优先，回退顶层字段；expired 字段支持 ISO/unix 秒，JWT exp 兜底；DisplayName 优先 email。
- 编译通过。决策：缺失 type 时宽松推断为 codex（标记 TypeInferred），仅本期支持 codex 风险可控。

## 目标

实现 CPA 格式 Codex 认证文件（`auth-files` 等效能力）的解析器，支持单文件 / 多文件上传解析，产出统一的 token DTO，交给 T04（账号供给工厂）建账号。**只需 Codex 类型。**

对应 CPA：`internal/api/handlers/management/auth_files.go` 的 `UploadAuthFile` + `internal/auth/codex/token.go` 的 `CodexTokenStorage`。

---

## 涉及文件

| 文件 | 操作 |
| --- | --- |
| `src/AITool.Application/Codex/CodexCredentialParseResult.cs` | 新建解析结果 DTO |
| `src/AITool.Infrastructure/Codex/CodexCredentialParser.cs` | 新建解析器 |

参考：CPA `internal/auth/codex/token.go:18-39`（`CodexTokenStorage` 字段定义）。

---

## 凭证文件格式（CPA `CodexTokenStorage`）

单文件是一个扁平 JSON 对象：

```json
{
  "id_token": "<JWT>",
  "access_token": "...",
  "refresh_token": "...",
  "account_id": "<chatgpt_account_id>",
  "last_refresh": "2026-07-03T...Z",
  "email": "user@example.com",
  "type": "codex",
  "expired": "2026-07-03T...Z"
}
```

字段说明（注意 JSON tag 与字段名差异）：

| 字段名 | JSON key | 必填 | 说明 |
| --- | --- | --- | --- |
| Type | `type` | 是 | 必须为 `codex`，否则拒绝 |
| AccessToken | `access_token` | 是 | |
| RefreshToken | `refresh_token` | 是 | |
| IdToken | `id_token` | 是 | JWT（用于解析 account_id/email/plan_type） |
| AccountId | `account_id` | 否 | 可从 id_token JWT 解析兜底 |
| Email | `email` | 否 | 同上兜底 |
| Expired | `expired` | 否 | access_token 过期时间（ISO 8601） |
| LastRefresh | `last_refresh` | 否 | |

> CPA 保存时 `Metadata` 会平铺到顶层（priority/note/headers 等）。本任务**只解析 Codex 核心字段**，忽略 priority/note 等扩展键（不在本期范围）。

---

## 详细步骤

### 1. 解析结果 DTO `CodexCredentialParseResult`

```csharp
public sealed class CodexCredentialParseResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }            // 失败原因（type 非 codex / 缺字段 / JSON 非法）
    public string? FileName { get; set; }         // 原始文件名（多文件批量时用）
    public string? DisplayName { get; set; }      // 建议默认名：email 或 FileName 去 .json
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? IdToken { get; set; }
    public string? AccountId { get; set; }
    public string? Email { get; set; }
    public string? PlanType { get; set; }         // 从 id_token JWT 解析
    public DateTimeOffset? TokenExpiresAt { get; set; }  // 从 expired 字段或 JWT exp
}
```

### 2. 解析器 `CodexCredentialParser`

注入：无状态，可做静态类或注入 singleton。建议静态类（纯函数）。

```csharp
public static class CodexCredentialParser
{
    /// 解析单个 JSON 字符串。
    public static CodexCredentialParseResult Parse(string json, string? fileName = null);

    /// 批量解析（多文件），返回逐文件结果（含失败项），不抛异常。
    public static List<CodexCredentialParseResult> ParseMany(IEnumerable<(string FileName, string Json)> files);
}
```

#### `Parse` 逻辑

1. `JsonDocument.Parse(json)`，非法 JSON → `Success=false, Error="JSON 格式非法"`。
2. 取 `type`，若非 `codex`（缺失或其它值）→ `Success=false, Error="非 Codex 类型凭证"`。
3. 取 `access_token` / `refresh_token` / `id_token`，任一空 → `Success=false`。
4. **优先从 `id_token` JWT 解析** account_id / email / plan_type（调 `CodexJwtParser.Parse`，T02 产出）；解析失败则回退到顶层 `account_id` / `email` 字段。
5. `TokenExpiresAt`：优先 `expired` 字段（ISO 8601 解析），其次 JWT `exp` claim。
6. `DisplayName`：`email` ?? `FileName` 去 `.json` 后缀 ?? `"Codex 账号"`。
7. 返回 Success=true 结果。

#### `ParseMany` 逻辑

- 逐文件调 `Parse`，收集结果（含失败项）。
- 不因单文件失败中断。
- 返回列表供 T11 控制器构造 207 Multi-Status 风格响应。

### 3. 上传入口（T11 控制器调用）

解析器只负责「字符串 → DTO」。文件读取（multipart / raw body）在 T11 控制器完成：

- **multipart**：`Request.Form.Files`，逐个读流为字符串，组成 `(FileName, Json)` 列表 → `ParseMany`。
- **raw body**：`Request.Body` 读为字符串，需 query `?name=xxx.json` → 单文件 `Parse`。

---

## 性能考量

### 引用原则
- **P5 HttpClient 复用**：本任务无 HTTP（纯解析）。
- **P6 批量**：批量导入走 `ParseMany` 一次性解析。

### 本任务特有
- **纯内存解析**：JSON 解析 + base64 解码 + JWT 解析，无 IO、无数据库。开销可忽略（单文件 < 10KB）。
- **`JsonDocument` 而非 `JsonSerializer.Deserialize<T>`**：字段名不规则（`expired` 等），且需宽容缺字段；用 `JsonDocument` 逐键取值更稳。注意 `using JsonDocument`（Dispose）。
- **批量解析并行**：多文件解析 CPU 密集但极轻，**串行即可**（不必 Parallel.ForEach，避免线程池压力；单文件微秒级）。
- **文件大小上限**：T11 控制器侧限制单文件 / 总大小（如单文件 64KB、总计 10 个），防恶意大文件。
- **流读取**：multipart 文件用 `StreamReader.ReadToEndAsync`，限制读取长度防 OOM。

---

## 验收标准

1. 合法 Codex JSON → `Success=true`，字段齐全，DisplayName 合理。
2. `type` 非 `codex` 或缺失 → `Success=false, Error` 明确。
3. 缺 access/refresh/id_token → `Success=false`。
4. id_token 能解析出 account_id/email/plan_type；解析失败时回退顶层字段。
5. `ParseMany`：混合合法/非法文件 → 返回逐项结果，不中断。
6. JWT 解析失败但顶层有 account_id/email → 仍可成功（降级）。

---

## 风险

- **`type` 字段缺失的兼容**：CPA 老文件可能无 `type`。策略：若 `type` 缺失但存在 `access_token`+`refresh_token`+`id_token`，可视为 codex（宽松），或严格拒绝。**建议宽松**（缺失 type 默认 codex），并在结果标记 `TypeInferred=true`，避免误导入其他 provider 文件——但本期只支持 codex，宽松风险可控。实现时确认。
- **时间格式**：`expired` 可能是 ISO 8601 带或不带时区。用 `DateTimeOffset.Parse`（文化不变）+ 容错。若为 unix 秒，需额外判断（CPA 是字符串 ISO，但导入文件来源多样）。
- **account_id 双源**：文件顶层 `account_id` 与 JWT 解析可能不一致。**以 JWT 为准**（JWT 是权威，对应 CPA `RequestCodexToken` 行为）。
