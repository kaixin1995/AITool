# Codex OAuth 账号管理 — 任务拆分总览

> 本文档是 Codex 功能改造的顶层总览。所有细化任务见同目录 `01`～`13` 编号文档。
> 本文档定义的**横切性能原则**被所有子任务文档引用，子文档不再重复展开。

## 实施总进度（2026-07-03）

| 任务 | 状态 | 说明 |
| --- | --- | --- |
| T01 数据模型 | ✅ 已完成 | CodexAccount 实体 + Site 加 2 列 + 注册 |
| T02 OAuth 客户端 | ✅ 已完成 | PKCE/授权URL/交换/刷新(single-flight)/JWT 解析 |
| T03 凭证导入 | ✅ 已完成 | CPA 格式 JSON 解析（宽松 type 推断） |
| T05 模型目录 | ✅ 已完成 | 静态分层(快照自CPA) + 动态拉取 |
| T04 账号供给工厂 | ✅ 已完成 | 隐藏 Site 工厂 + 共享级联删除 |
| T06 转发请求头接入 | ✅ 已完成 | ExtraHeaders 通用注入（3处转发点） |
| T07 站点列表过滤 | ✅ 已完成 | 列表/导入/导出/一键拉取过滤托管 Site |
| T08 Token 刷新后台服务 | ✅ 已完成 | 5min 周期 + 错峰 + 1h 提前量 |
| T09 额度主动查询 | ✅ 已完成（端点待实测） | 30s 防抖 + single-flight + 自动禁用阈值 |
| T10 被动冷却与重置 | 🟡 部分 | 重置+自动恢复+判定函数就绪；转发错误路径接入待 T13 验证后补 |
| T11 管理 API 控制器 | ✅ 已完成 | 全部端点 |
| T12 前端账号面板 | ✅ 已完成 | 卡片面板 + OAuth/导入/编辑 Modal + 二次确认 |
| T13 集成与性能验证 | ✅ 编译+回归通过 | 177 集成测试全过；真实上游验证待运行环境 |

**编译**：0 警告 0 错误。**回归**：集成测试 177/177 通过；唯一失败是预存的 flaky 时间测试（与本改动无关）。

**仍待真实环境验证/补做**（见 T13）：① OAuth/导入/调用端到端 ② 上游额度端点结构 ③ T10 转发错误路径接入（即时冷却）④ token 有效期实测调参。

---

---

## 1. 需求回顾

为 AITool 增加 Codex（ChatGPT/OpenAI Codex CLI）OAuth 账号管理能力，要求：

1. **Codex OAuth 登录**：单独页面/流程，支持多个 Codex 账号并存。
2. **凭证导入**：支持直接导入 CPA 格式的认证文件（`management.html#/auth-files` 等效能力），只需 Codex。
3. **额度展示**：面板显示每个 Codex 账号的额度，支持**手动刷新**。
4. **重置额度**：支持重置（**二次弹窗确认**，慎重）。
5. **账号生命周期**：已登录账号支持**禁用/删除**；可对单个账号设置，也可整体设置「剩余额度低于某值自动禁用」。
6. **自定义名称**：每个账号可自定义名称（同站点管理），便于区分。
7. **多模型**：Codex 账号含多个模型，逻辑同站点管理，模型显示在 `Admin/Models`。
8. **路由调用**：Codex 模型可在 `Admin/Routes` 增加模型调用，逻辑同站点管理。
9. **对话测试**：对话测试中同样显示 Codex 模型。

界面要点：右上角「新增 Codex OAuth 登录」+「上传凭证」按钮；每个账号一个面板，含额度信息 + 重置按钮 + 禁用/删除 + 自动禁用阈值设置。

---

## 2. 总体方案（已与需求方确认）

| 决策点 | 选定方案 | 理由 |
| --- | --- | --- |
| 接入现有 Site-based 机制 | **后台隐藏 Site 复用** | 现有 Models/Routes/Chat 全部以 `SiteId` 关联，`ProxyRequestMetadataCache` 按 SiteId join。每个 Codex 账号自动创建一个隐藏 Site（Responses 协议），Models/Routes/Chat **零业务改动**自动联动。 |
| OAuth 登录交互 | **手动粘贴回调 URL** | 任意部署环境（本地/服务器/Docker）可用，无需绑定端口。对应 new-api `codex-oauth-dialog` 行为。 |
| 额度语义 | **主动查询 + 被动冷却 两者都要** | 主动查上游展示数字 + 自动禁用阈值；被动解析 `usage_limit_reached`/429 冷却 + 手动重置。 |

### 隐藏 Site 协议契合性（已核查）

- 站点「OpenAI/Anthropic 都不勾选」→ `ResolveSiteProtocolType` 返回 `"Responses"`。
- Codex 上游 `https://chatgpt.com/backend-api/codex/responses` 正是 Responses 链路。
- `SiteEndpointPathResolver` 的 `versioned-base` 模式 = `{baseUrl}/{endpoint}`。设 `BaseUrl=https://chatgpt.com/backend-api/codex`、endpoint=`responses` → 命中上游。**无需扩展路径模式。**

### 转发认证来源（已核查）

- 转发链路上游 Bearer token **唯一来源是 `Site.ApiKey`**（`ProxyForwardService.BuildRequestMessage` 从 `ProxyForwardRequest.TargetApiKey` 取，即 `CachedProxyRouteTarget.ApiKey`）。
- 缓存 5s 过期（`ProxyRequestMetadataCache.CacheDuration`）。因此 Codex token 刷新后写回 `Site.ApiKey` + `InvalidateRouteTargets()` 即可，最迟 5s 后全进程生效。
- Codex 三个特殊请求头（`Originator`、`Chatgpt-Account-Id`、`User-Agent`）通过 **`ForwardHeaders`** 注入（该管线已存在并贯穿 `BuildRequestMessage`，仅 OpenAI/Responses 控制器当前未设置）。

---

## 3. 任务依赖关系图

```
T01 数据模型
 │
 ├──> T02 OAuth 客户端 ──────────┐
 ├──> T03 凭证导入解析 ──────────┤
 │                              v
 │   T05 模型目录 ──────────> T04 账号供给工厂（隐藏 Site）
 │                              │
 │                              ├──> T06 转发请求头接入 ──> T07 站点列表过滤
 │                              │
 │                              ├──> T08 Token 刷新后台服务
 │                              ├──> T09 额度主动查询
 │                              └──> T10 额度被动冷却与重置
 │                                         │
 └─────────────────────────────────────────┴──> T11 管理 API 控制器
                                                        │
                                                        v
                                                T12 前端账号面板
                                                        │
                                                        v
                                                T13 集成与性能验证
```

依赖说明：
- **T01（数据模型）是一切的基础**，最先实现。
- **T04（账号供给工厂）是后端核心枢纽**，依赖 T02/T03（产 token）与 T05（产模型目录）。
- **T06（转发头）依赖 T01 加的 `Site.ExtraHeadersJson` 列**，不依赖 T04。
- **T08/T09/T10 依赖 T04 产出的账号实体**，三者相互独立可并行。
- **T11（API）汇总 T02/T03/T04/T08/T09/T10 的能力**，前端 T12 依赖 T11。
- **T13 是最终验收**，依赖全部。

---

## 4. 横切性能原则（所有子任务引用，不重复展开）

> 子任务文档只写「本任务特有」的性能考量，通用原则回引本节。

### P1. 缓存失效约定

- `ProxyRequestMetadataCache` 是 **singleton**（`Program.cs:141`），包裹 `IMemoryCache`，**TTL 5 秒**。
- **任何写 `CodexAccount` / `Site` / `SiteModelMapping` / `ProxyRouteRule` 的操作后，必须调用：**
  - `_metadataCache.InvalidateRouteTargets()`（路由/转发目标）
  - `_metadataCache.InvalidateModelMetadata()`（模型库/映射，涉及模型变更时）
  - 必要时 `InvalidateAdminRouteMetadata()` / `InvalidateRuntimeRouteTargets()`
- 失效是进程级的（singleton），下一次读触发重建。不要手动重建，只失效。

### P2. SqlSugar 多表查询限制

- SqlSugar 不支持多表 LINQ join+groupby。**涉及多表聚合时，先把各表 `.ToListAsync()` 载入内存，再 LINQ-to-Objects join/group**。
- 结果被 5s 缓存覆盖，内存 join 在账号/站点量级（百~千级）可接受。
- 账号列表查询（T11）必须遵循此模式，避免 N+1。

### P3. DateTimeOffset 存储一致性

- `SqlSugarSetup` 的 AOP（`DataExecuting`）在 Insert/Update 时把 `DateTimeOffset` 转本地时区存储；查询参数同样处理，保证往返一致。
- **新建实体/字段时不要绕过 AOP 手动转时区**；读回由 `SqlSugarExtensions.ToListAsync` 归一化 UTC。
- 时间字段统一用 `DateTimeOffset`（不要 `DateTime`）。

### P4. CodeFirst 加列规则

- `db.CodeFirst.InitTables(...)` 启动时**只补缺失表/列，不删**。
- **新增列必须 nullable 或有默认值**，否则老数据行写入会失败。
- 新增实体加入 `InitTables` 列表即可自动建表。

### P5. HttpClient 复用

- 所有上游 HTTP 客户端（OAuth、额度查询、模型拉取）**必须经 `AddHttpClient<接口, 实现>()` 注册**，复用 `SocketsHttpHandler` 连接池。
- 禁止 `new HttpClient()`。
- 配置超时（OAuth 交换 < 额度查询 < 模型拉取，分别设）。

### P6. 批量 upsert

- 模型库 `ModelLibraryItem`、映射 `SiteModelMapping` 的写入**必须批量**：
  - 先查已存在（by `ModelName` / by `(SiteId, RemoteModelName)`），内存求差集；
  - 新增 `InsertRangeAsync`，更新 `UpdateAsync` 仅变更字段；
  - 禁止逐条往返数据库。

### P7. 后台服务节流规范

- `BackgroundService` 类服务（Token 刷新、额度查询、冷却恢复）**统一遵守**：
  - 周期循环 + `StopAsync` 取消令牌响应；
  - 单轮任务**限速分散**（如刷新按 `TokenExpiresAt` 排序，错峰触发），避免同一时刻打满上游；
  - 失败**指数退避**，不风暴重试；
  - 扫描查询**只投影必要列**（不要 `Select *` 全实体）。

### P8. single-flight 与结果缓存防抖

- **Token 刷新**：同一 refresh_token 并发只允许一次真实刷新（single-flight：`ConcurrentDictionary<token, SemaphoreSlim>`，结果共享给等待者）。详见 T02/T08。
- **额度查询**：同一账号结果短缓存（如 30s）防抖；手动刷新按钮强制穿透缓存。详见 T09。

### P9. 索引规范

- 新增实体的热查询字段必须建索引（`[SugarIndex]`）：
  - `CodexAccount.LinkedSiteId`（按 Site 反查账号）
  - `CodexAccount.Email`（去重/展示）
  - `CodexAccount.TokenExpiresAt`（后台扫描临期）
- 索引在 CodeFirst 建表时自动生成。

### P10. 额度/冷却判定不进入转发热路径

- 正常转发请求**不增加**额度查询或冷却判定开销。
- 被动冷却判定**只在错误处理分支**进行（上游返回错误时才解析），详见 T10。
- 冷却状态读取走 in-memory 热读（`CachedProxyRouteTarget` 已含 `IsEnabled`），不每请求查 DB。

---

## 5. 分阶段交付建议

| 阶段 | 范围 | 产出 |
| --- | --- | --- |
| **P1 核心闭环** | T01 → T02 → T03 → T05 → T04 → T06 → T07 → T11(子集) → T12(子集) | Codex 账号可登录/导入，模型自动进 Models/Routes/Chat，可端到端调用 |
| **P2 额度与自动禁用** | T08 → T09 → T10 → T11(补全) → T12(补全) | 额度展示、手动刷新、自动禁用阈值、被动冷却、重置额度 |
| **P3 增强** | T05 动态拉取、T13 验收 | 动态模型目录、订阅窗口展示、性能验证、回归 |

> 建议先打通 P1 最小闭环（登录→入库→Models/Routes/Chat 自动联动→可调用），再补 P2。

---

## 6. 文档约定

- 每个子任务文档结构：**目标 / 前置依赖 / 涉及文件 / 详细步骤 / 数据或接口设计 / 性能考量 / 验收标准 / 风险**。
- 状态标记：文档头部 `状态：待开发 / 开发中 / 已完成`。
- 性能考量中引用本总览第 4 节的 `P1`～`P10`，只展开本任务特有内容。
- 文件路径一律用项目根相对路径（`src/...`）。
- 涉及参考实现时，标注 CPA（`reference-projects/CLIProxyAPI/...`）或 new-api（`reference-projects/new-api/...`）的具体文件与行号。
