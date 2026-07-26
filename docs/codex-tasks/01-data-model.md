# T01 — 数据模型与持久化

> 状态：已完成 ✅
> 前置依赖：无（一切的基础）
> 关联总览章节：横切性能原则 P3 / P4 / P9

## 实施记录

- 新建 `src/AITool.Domain/Codex/CodexAccount.cs`：字段与文档一致，加 `[SugarIndex]`（`IX_CodexAccounts_LinkedSiteId`、`IX_CodexAccounts_TokenExpiresAt`，均非唯一）。
- `Site.cs` 末尾增加 `ManagedSource`(nullable, 50) 与 `ExtraHeadersJson`(nullable, 2000)。
- `AppDbContext.cs`：using 加 `AITool.Domain.Codex`；访问器加 `CodexAccounts`；`InitTables` 注册 `typeof(CodexAccount)`（紧跟 `Site` 之后）。
- 编译通过（Domain + Application + Infrastructure，0 警告 0 错误）。

## 目标

新增 `CodexAccount` 实体（表 `CodexAccounts`），并在现有 `Site` 实体上增加 `ManagedSource` 与 `ExtraHeadersJson` 两列，完成 SqlSugar CodeFirst 注册与索引，使后续所有任务可持久化 Codex 账号及其托管 Site 关系。

---

## 涉及文件

| 文件 | 操作 |
| --- | --- |
| `src/AITool.Domain/Codex/CodexAccount.cs` | 新建实体 |
| `src/AITool.Domain/Sites/Site.cs` | 加 2 列 |
| `src/AITool.Infrastructure/Persistence/AppDbContext.cs` | 注册实体、加访问器、`InitTables` |

参考：现有实体样板 `src/AITool.Domain/Sites/Site.cs`；现有注册 `AppDbContext.cs:40-50`、`:169-180`。

---

## 详细步骤

### 1. 新建 `CodexAccount` 实体

路径：`src/AITool.Domain/Codex/CodexAccount.cs`

```csharp
using SqlSugar;

namespace AITool.Domain.Codex;

[SugarTable("CodexAccounts")]
public sealed class CodexAccount
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false, ColumnName = "Id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// 用户自定义名称（同站点管理，便于区分账号）。唯一性建议在应用层校验。
    [SugarColumn(Length = 200, IsNullable = false)]
    public string DisplayName { get; set; } = string.Empty;

    /// 从 JWT 解析的 email。
    [SugarColumn(Length = 200, IsNullable = true)]
    public string? Email { get; set; }

    /// chatgpt_account_id（JWT claim）。
    [SugarColumn(Length = 100, IsNullable = true)]
    public string? AccountId { get; set; }

    /// free / plus / team / pro（决定可见模型分层）。
    [SugarColumn(Length = 50, IsNullable = true)]
    public string? PlanType { get; set; }

    /// 当前 access_token（同步写回 LinkedSite.ApiKey）。
    [SugarColumn(Length = 2000, IsNullable = true)]
    public string? AccessToken { get; set; }

    /// OAuth refresh_token。
    [SugarColumn(Length = 2000, IsNullable = true)]
    public string? RefreshToken { get; set; }

    /// JWT id_token（含订阅窗口等，面板展示用）。
    [SugarColumn(Length = 4000, IsNullable = true)]
    public string? IdToken { get; set; }

    /// access_token 过期时间（UTC，AOP 自动转本地存储）。
    public DateTimeOffset? TokenExpiresAt { get; set; }

    public DateTimeOffset? LastRefreshAt { get; set; }

    /// 指向自动创建的隐藏 Site（逻辑 FK，非数据库约束）。
    [SugarColumn(IsNullable = false)]
    public Guid LinkedSiteId { get; set; }

    /// 手动启用/禁用。
    [SugarColumn(IsNullable = false)]
    public bool IsEnabled { get; set; } = true;

    /// 剩余额度低于此值自动禁用（单位由上游返回决定）；null=不自动禁用。
    [SugarColumn(IsNullable = true)]
    public decimal? AutoDisableThreshold { get; set; }

    /// 是否处于被动冷却（usage_limit_reached）。
    [SugarColumn(IsNullable = false)]
    public bool IsQuotaCooling { get; set; }

    /// 冷却恢复时间（UTC）。
    public DateTimeOffset? QuotaCoolingUntil { get; set; }

    /// 最近一次主动额度查询原始结果（面板展示）。
    [SugarColumn(Length = 4000, IsNullable = true)]
    public string? LastQuotaRawJson { get; set; }

    public DateTimeOffset? LastQuotaCheckedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

### 2. 在 `Site` 实体加 2 列

`src/AITool.Domain/Sites/Site.cs` 末尾增加：

```csharp
/// null=用户自建站点（默认）；"Codex"=Codex 托管的隐藏 Site。Sites 列表页据此过滤。
[SugarColumn(Length = 50, IsNullable = true)]
public string? ManagedSource { get; set; }

/// JSON 字典，Codex 隐藏 Site 存特殊请求头（Originator / Chatgpt-Account-Id / User-Agent）。
/// 通用字段，未来其他需要特殊头的站点也能复用。
[SugarColumn(Length = 2000, IsNullable = true)]
public string? ExtraHeadersJson { get; set; }
```

### 3. 注册到 `AppDbContext`

`src/AITool.Infrastructure/Persistence/AppDbContext.cs`：

- 顶部 using 加 `using AITool.Domain.Codex;`
- 访问器区（line 40-50 附近）加：
  ```csharp
  public ISugarQueryable<CodexAccount> CodexAccounts => _client.Queryable<CodexAccount>();
  ```
- `InitializeDatabase`（line 169-180）的 `InitTables` 列表末尾加 `typeof(CodexAccount)`。

---

## 性能考量

### 引用原则
- **P3 DateTimeOffset 一致性**：所有时间字段用 `DateTimeOffset?`，不绕过 AOP 手动转时区。
- **P4 CodeFirst 加列规则**：`Site` 新增两列均为 **nullable**，老数据行兼容（建库后老表自动补列，老行值为 NULL）。`CodexAccount` 为全新表，不影响老数据。
- **P9 索引规范**：见下方索引设计。

### 索引设计（本任务特有）

在 `CodexAccount` 上对热查询字段建索引。SqlSugar 用 `[SugarIndex]` 特性（参考现有 `ModelLibraryItem` / `SiteModelMapping` 的唯一索引写法）。建议：

- `LinkedSiteId`：按 SiteId 反查账号（转发链路、删除级联、额度面板按 Site 关联）。**非唯一**。
- `Email`：去重 / 展示。**非唯一**（同名 email 不同 plan 可能多次出现，且导入路径不强制唯一）。
- `TokenExpiresAt`：后台服务扫描临期账号（T08）。**非唯一**，扫描排序用。

实现示例（具体特性语法以现有项目 `SiteModelMapping.cs` 为准）：

```csharp
[SugarIndex("IX_CodexAccounts_LinkedSiteId", nameof(LinkedSiteId), OrderByType.Asc)]
[SugarIndex("IX_CodexAccounts_TokenExpiresAt", nameof(TokenExpiresAt), OrderByType.Asc)]
```

> 注：SqlSugar 的复合/多索引写法以项目现有实体的实际写法为准，实现时先 Read 一个带 `[SugarIndex]` 的现有实体对齐语法。

### 字段长度

- `AccessToken` / `RefreshToken` 用 2000，`IdToken` 用 4000（JWT 较长，预留余量）。
- `LastQuotaRawJson` 用 4000；若上游返回过长，考虑只存解析后摘要字段而非全量 JSON（T09 决策）。

---

## 验收标准

1. 编译通过（`dotnet build`）。
2. 删除 `aitool.db` 或首次启动后，`Sites` 表多出 `ManagedSource`、`ExtraHeadersJson` 两列；新表 `CodexAccounts` 已建且含全部字段与索引。
3. 老库（已有 `aitool.db`）启动后，老 `Sites` 行的 `ManagedSource`/`ExtraHeadersJson` 为 NULL，不报错；现有站点功能不受影响。
4. `AppDbContext.CodexAccounts` 可正常 `.ToListAsync()`。

---

## 风险

- **SqlSugar `[SugarIndex]` 多索引语法差异**：实现前必须 Read 现有带索引实体对齐写法，避免特性写错导致建索引失败（静默）。
- **CodeFirst 补列对生产库**：仅在缺失时补；已存在列不会改类型。若后续需改列类型，需手动迁移。当前两列均为 nullable 新增，无风险。
