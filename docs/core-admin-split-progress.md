# Core / Admin 拆分当前进展说明

## 文档目的

这份文档用于记录 **Core / Admin 双宿主拆分** 当前已经完成的工作，以及后续仍未完成的事项，方便在后续继续开发时快速对齐现状。

这份文档是阶段性进展记录，不替代整体设计文档与协议文档。

相关文档：

- [core-admin-split-design.md](core-admin-split-design.md)
- [core-admin-split-implementation-checklist.md](core-admin-split-implementation-checklist.md)
- [core-admin-split-communication-protocol.md](core-admin-split-communication-protocol.md)

---

## 当前目标回顾

当前拆分目标是：

- **Core 进程**：无页面、只提供接口、面向 Claude Code / OpenCode / Codex / 代理客户端
- **Admin 进程**：承载全部 `/Admin/*` 页面，面向管理员
- Core 不再依赖当前业务数据库
- Admin 作为配置权威源与历史数据中心
- Core 与 Admin 通过：
  - 配置同步协议
  - 事件流协议
  - ack / replay / spool
  进行通信

在物理拆成两个独立宿主之前，先在当前单体内把协议与运行模型打通，确保核心链路稳定，再推进双宿主落地。

---

## 一、当前已经完成的内容

### 已完成：Core 运行时配置快照模型

已经完成并接入：

- `src/AITool.Application/CoreRuntime/CoreRuntimeConfigSnapshot.cs`
- `src/AITool.Application/CoreRuntime/ICoreRuntimeConfigProvider.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreRuntimeConfigProvider.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreRuntimeConfigSnapshotBuilder.cs`
- `src/AITool.Infrastructure/Operations/SystemRuntimeSettingsService.cs`
- `src/AITool.Application/Operations/ISystemRuntimeSettingsService.cs`

#### 已实现能力

- 定义了 Core 运行时完整配置快照模型
- 支持从 Admin 当前数据库中的主数据构建完整快照
- 支持稳定计算配置哈希 `ConfigHash`
- Core 当前内存持有一份生效快照
- Core 启动时可以从本地 `last-good-config.json` 恢复最后一次成功配置

#### 配置快照能力状态

- 已可工作
- 已有测试覆盖

---

### 已完成：Core 全量同步最小闭环

已经完成并接入：

- `src/AITool.Web/Controllers/Core/CoreConfigController.cs`
- `src/AITool.Web/Controllers/Core/CoreConfigSyncController.cs`
- `src/AITool.Web/Controllers/Core/CoreConfigHandshakeController.cs`
- `src/AITool.Web/Controllers/Core/CoreRuntimeStatusController.cs`
- `src/AITool.Application/CoreRuntime/CoreAdminHandshakeModels.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreConfigSyncDecisionResolver.cs`

#### 当前接口

- `GET /api/core/config/status`
- `POST /api/core/config/full-sync`
- `POST /api/core/config/handshake`
- `GET /api/core/health`
- `GET /api/core/ready`
- `GET /api/core/runtime/status`

#### 配置同步已实现能力

- Core 无配置时 `ready=false`
- Admin 可下发完整配置快照
- Core 会校验：
  - `ConfigVersion`
  - `ConfigHash`
  - 最小主配置完整性
- 同版本同哈希会被 `ignored`
- 握手时可以返回当前：
  - 已应用配置版本
  - 已应用配置哈希
  - 是否 ready
  - 当前同步建议（`noop` / `full-sync-required` / `admin-version-behind`）

#### 全量同步闭环状态

- 最小闭环已打通
- 已有应用测试与集成测试验证

---

### 已完成：Core 本地 last-good-config 恢复

已经完成并接入：

- `src/AITool.Infrastructure/CoreRuntime/CoreRuntimeConfigFileOptions.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreRuntimeConfigProvider.cs`
- `src/AITool.Web/Program.cs`

#### 本地恢复已实现能力

- Core 在设置当前快照后，会把配置写入本地文件
- Core 启动时会尝试恢复该文件
- 如果没有可恢复配置，会保持 `not-ready`，等待 Admin 下发首个完整快照

#### 本地恢复状态

- 已可工作
- 已有测试验证

---

### 已完成：Core 事件模型与最小事件总线

已经完成并接入：

- `src/AITool.Application/CoreRuntime/CoreAdminEventModels.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreAdminEventEnvelopeBuilder.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreEventSequenceProvider.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreAdminEventBus.cs`

#### 事件模型已实现能力

- 定义统一 `CoreAdminEventEnvelope`
- 已定义事件负载：
  - `CoreUsageLogEvent`
  - `CoreConversationTurnEvent`
- 使用单进程全局递增 `SequenceId`
- 提供最小内存事件总线

#### 事件总线状态

- 已工作
- 已有测试验证

---

### 已完成：两条真实链路接入事件发布

#### UsageLog 链路

已经完成：

- `src/AITool.Infrastructure/CoreRuntime/CoreUsageLogEventPublisher.cs`
- `src/AITool.Infrastructure/Proxy/UsageLogService.cs`

当前行为：

- 继续保留原有数据库落库逻辑
- 同时额外发布一份 UsageLog 事件到 Core 事件总线

#### Conversation 链路

已经完成：

- `src/AITool.Infrastructure/Conversations/CoreConversationEventPublisher.cs`
- `src/AITool.Infrastructure/Conversations/ConversationLogService.cs`

当前行为：

- 继续保留原有对话记录持久化逻辑
- 同时额外发布一份 Conversation 事件到 Core 事件总线

#### 真实事件接入状态

- 已可工作
- 已有测试验证

---

### 已完成：spool / ack / replay 最小可靠闭环

已经完成并接入：

- `src/AITool.Infrastructure/CoreRuntime/CoreEventSpoolOptions.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreEventSpoolStore.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreEventSpoolBackgroundService.cs`
- `src/AITool.Application/CoreRuntime/CoreAdminAckModels.cs`
- `src/AITool.Web/Controllers/Core/CoreEventAckController.cs`

#### 事件可靠性接口

- `POST /api/core/events/ack`
- `GET /api/core/events/replay?afterSequenceId=...`

#### 可靠事件已实现能力

- 发布的事件会进入内存总线
- 后台服务会把事件写入本地 spool JSONL 文件
- Core 可以：
  - 读取最新 `latestSequenceId`
  - 判断 `hasSpoolBacklog`
  - 根据 `afterSequenceId` replay 积压事件
  - 根据 ack 序号清理已确认事件

#### 可靠事件闭环状态

- 最小可靠事件闭环已打通
- 已有应用测试与集成测试验证

---

### 已完成：Admin 最小客户端与 UsageLog 真实消费入库

已经完成：

- `src/AITool.Infrastructure/CoreRuntime/CoreAdminClient.cs`
- `src/AITool.Infrastructure/CoreRuntime/AdminUsageLogEventIngestor.cs`
- `tests/AITool.ApplicationTests/CoreRuntime/AdminUsageLogEventIngestorTests.cs`
- `tests/AITool.IntegrationTests/Core/CoreUsageLogAdminIngestTests.cs`
- `tests/AITool.IntegrationTests/Core/CoreAdminClientTests.cs`

#### Admin 消费已实现能力

- Admin 侧最小客户端已支持：
  - `HandshakeAsync(...)`
  - `FullSyncAsync(...)`
  - `AckAsync(...)`
  - `ReplayAsync(...)`
- Admin 现在已经不只是“会调用 Core 协议”，而是能够：
  - 读取 `replay`
  - 解析 `usage-log` 事件
  - 将 UsageLog 事件写入 Admin 当前数据库中的 `ProxyUsageLogs`
  - 返回最大连续 sequence，供上层提交 `ack`
- 已做最小幂等去重，避免 replay 或重复提交写入完全相同的日志记录

#### Admin 消费状态

- **UsageLog 这条真实链路已经具备最小消费入库闭环**
- 但目前仍属于骨架级能力，还没有与独立 Admin 页面真正连通

---

### 已完成：独立 AITool.Admin 宿主骨架

已经完成：

- `src/AITool.Admin/AITool.Admin.csproj`
- `src/AITool.Admin/Program.cs`
- `AITool.slnx` 已包含 `AITool.Admin`
- `tests/AITool.Admin.IntegrationTests/AITool.Admin.IntegrationTests.csproj`
- `tests/AITool.Admin.IntegrationTests/AdminHostSmokeTests.cs`

#### Admin 宿主已实现能力

- `AITool.Admin` 宿主工程已经创建
- `AITool.Admin` 已能单独 `dotnet build`
- 已接入：
  - Razor Pages
  - Controllers
  - Authentication / Authorization
  - 数据库
  - CoreAdminClient
- `AITool.Admin.IntegrationTests` 已能单独拉起 Admin 宿主并完成最小 smoke test

#### Admin 宿主状态

- **宿主本体已能单独编译成功**
- **独立测试工程已打通最小宿主启动验证**

---

### 已完成：第一批宿主共享能力抽取

已经完成或开始接入：

- `src/AITool.Infrastructure/Hosting/AppVersionInfo.cs`
- `src/AITool.Infrastructure/Hosting/HttpLogFormatter.cs`
- `src/AITool.Infrastructure/Hosting/AdminAuthOptions.cs`
- `src/AITool.Infrastructure/Hosting/HttpExceptionLoggingFilter.cs`

以及：

- `src/AITool.Web/GlobalUsings.cs`
- `src/AITool.Web/Services/AdminAuthService.cs`
- `src/AITool.Web/Program.cs`
- `src/AITool.Admin/Program.cs`

#### 当前作用

- 把一部分宿主公共能力从 `AITool.Web.Services` 中抽出，供双宿主复用
- 避免在 `AITool.Admin` 中重复实现这些类型

#### 宿主共享层状态

- 第一轮抽取已完成
- 当前 `AITool.Admin` 仍然临时直接复用部分 `AITool.Web.Services` 中的实现类型
- 后续还需要继续收口：
  - 哪些应抽成真正的宿主共享层
  - 哪些应只属于 Admin
  - 哪些应通过 Core API 替代，而不再直接引用 Web 侧实现

---

### 已完成：当前主线完整验证

在持续推进期间，以下一直保持通过：

- `dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj`
- `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj`
- `dotnet build src/AITool.Web/AITool.Web.csproj`
- `dotnet build src/AITool.Admin/AITool.Admin.csproj`
- `dotnet test tests/AITool.Admin.IntegrationTests/AITool.Admin.IntegrationTests.csproj`

最新一轮完整验证结果：

- ApplicationTests：`99/99` 通过
- IntegrationTests：`175/175` 通过
- AITool.Web：构建成功，`0 warning / 0 error`
- AITool.Admin：单独构建成功
- AITool.Admin.IntegrationTests：`3/3` 通过
- AITool.slnx：整体构建成功，`0 error`

这说明当前主宿主与核心协议链路仍然稳定，没有被双宿主改造破坏，同时独立 Admin 宿主已经不只是能启动，而且开始承载真实页面与只读接口并通过验证。

---

## 二、当前还未完成的内容

下面这些是下一阶段必须继续推进的工作。

---

### 已关闭：AITool.Admin 独立测试工程跑通

这部分已经完成。

#### Admin 测试宿主状态

- `AITool.Admin.IntegrationTests` 已能独立启动 Admin 宿主并完成最小 smoke test
- 之所以单独拆测试工程，是为了避免与 `AITool.Web` 顶级 `Program` 的宿主入口冲突
- 后续不再需要围绕宿主入口做基础打通，只需在其上继续增加真实页面或接口验证

---

### 未完成：宿主共享层边界完全清理

虽然第一批共享能力已经抽取，但还没有完全结束。

#### 还需要继续确认的内容

- `DeveloperInvocationTraceStore`
- `ModelConcurrencyLimiter`
- `ProxyRequestMetadataCache`
- `ModelVendorCatalogService`
- `AnalyticsBackgroundQueryExecutor`

#### 这些服务要继续判断

- 哪些应该继续留在 `AITool.Web.Services`
- 哪些应该抽到可共享宿主层
- 哪些应该只属于 Admin
- 哪些后续应通过 Core API 替代，而不应在 Admin 宿主直接引用

#### 当前边界结论

宿主共享层已经从“完全混杂”进入“边界已暴露”的阶段，但还没有最终定型。

---

### 已完成：Admin 真实事件链路最小消费入库

已经完成：

- `src/AITool.Infrastructure/CoreRuntime/AdminUsageLogEventIngestor.cs`
- `tests/AITool.ApplicationTests/CoreRuntime/AdminUsageLogEventIngestorTests.cs`
- `tests/AITool.IntegrationTests/Core/CoreUsageLogAdminIngestTests.cs`

#### UsageLog 消费已实现能力

- Admin 现在已经不只是“会调用 Core 协议”，而是能够：
  - 读取 `replay`
  - 解析 `usage-log` 事件
  - 将 UsageLog 事件写入 Admin 当前数据库中的 `ProxyUsageLogs`
  - 返回最大连续 sequence，供上层提交 `ack`
- 已做最小幂等去重，避免 replay 或重复提交写入完全相同的日志记录

#### UsageLog 消费状态

- **UsageLog 这条真实链路已经具备最小消费入库闭环**
- 当前 `/Admin/UsageLogs` 真实页面还没有切到独立 `AITool.Admin` 宿主
- 当前阶段只能说明：数据链路已经具备页面迁移条件，但页面本身尚未迁移

---

### 已完成：第一块真实 Admin 页面/接口迁移验证

已经完成：

- `src/AITool.Admin/Pages/Admin/UsageLogs/Index.cshtml`
- `src/AITool.Admin/Pages/Admin/UsageLogs/Index.cshtml.cs`
- `src/AITool.Admin/Controllers/Admin/UsageLogsApiController.cs`
- `tests/AITool.Admin.IntegrationTests/UsageLogsPageSmokeTests.cs`
- `tests/AITool.Admin.IntegrationTests/AdminHostSmokeTests.cs`

#### 第一块页面迁移已实现能力

- `/Admin/UsageLogs` 已开始由独立 `AITool.Admin` 宿主承载
- 页面基础路由、标题、筛选骨架、汇总卡片和列表框架已迁入 `AITool.Admin`
- `AITool.Admin` 中已新增对应的 `UsageLogsApiController`，开始承接列表、汇总和链路详情这三类只读查询接口
- UsageLogs 页面脚本已开始迁入 `AITool.Admin`，当前已具备基础的筛选、分页、汇总加载和详情抽屉联动逻辑
- `AITool.Admin.IntegrationTests` 已能直接访问 `/Admin/UsageLogs` 页面与对应只读 API，并验证最小页面/API 联动
- 本轮已把独立 Admin 宿主中的 UsageLogs 查询接口继续向 Web 侧现有语义靠齐，补齐了查询 DTO、分页响应 DTO、详情 DTO、汇总 DTO，以及站点模型名称解析逻辑
- 本轮已补强 Admin 独立宿主的 UsageLogs 集成测试数据，覆盖了失败重试后成功、不同来源、不同协议和模型关键字筛选这几类真实查询场景
- 本轮继续补强了 `src/AITool.Admin/Pages/Admin/UsageLogs/Index.cshtml` 的前端展示，把模型列、状态徽标与详情抽屉继续向真实链路信息靠齐，开始展示请求模型、站点模型、重试/回退/最终结果标记，以及更完整的尝试级指标和错误信息

#### 第一块页面迁移状态

- **第一块真实 Admin 页面迁移验证已打通**
- 当前页面骨架、只读接口和最小宿主联动已经成立
- `/Admin/UsageLogs` 对应的列表、汇总、链路详情三类只读 API 已在 `AITool.Admin` 中接入并通过独立宿主测试验证
- 页面脚本已经开始迁移，但样式与更复杂的前端交互仍属于“继续收口中”状态
- 本轮完成后，`AITool.Admin.IntegrationTests` 中关于 UsageLogs 的独立宿主验证已提升到 **4/4 通过**，能覆盖页面访问、汇总列表、请求详情与筛选条件生效
- 本轮完成后，独立 Admin 宿主中的 UsageLogs 页面对真实链路细节的展示完整度又向前推进了一步，但与 `AITool.Web` 中更成熟的页面体验仍未完全收口

---

### 未完成：Core 真正物理独立宿主

当前虽然已经完成了大量 Core 协议与运行时模型工作，但它们仍然跑在当前 `AITool.Web` 宿主中。

#### 核心宿主拆分待办

- 单独创建真正的 `AITool.Core` 宿主工程
- 把 `/v1/*`、`/api/core/*` 真正迁到 `AITool.Core`
- 让 `AITool.Web` 逐步退出核心代理角色

#### 核心宿主当前状态

- 协议与事件模型已经具备迁出条件
- 但物理 Core 宿主拆分还没开始真正实施

---

### 未完成：patch 增量更新协议

当前配置同步只有：

- `full-sync`
- `handshake`
- `noop / full-sync-required / admin-version-behind`

#### 还没有做

- `patch` 协议模型
- `baseVersion` 校验后的增量更新应用
- 增量失败后自动回退到全量同步

这部分还在后续阶段。

---

### 未完成：事件流实时消费通道

当前事件链路仍然是：

- 发布 → 总线 → spool → replay/ack

#### 实时消费待办

- 真正的 Core → Admin 长连接实时推送
- Admin 长连接消费
- 实时流与 replay 的衔接

目前属于：

- **可靠协议最小闭环已完成**
- **实时推送通道尚未接入**

---

### 未完成：事件 sequence / ack 持久化元数据增强

当前已经有：

- `CoreEventSequenceProvider`
- `CoreEventSpoolStore`
- `Ack`
- `Replay`

#### 但还没做的更稳妥能力

- sequence 持久化元数据文件
- ack 持久化元数据文件
- Core 重启后恢复 sequence/ack 状态
- 更细粒度的 spool 文件轮转策略

目前最小版本已经能工作，但这部分仍然可以继续增强。

---

## 三、当前阶段的整体判断

当前已经完成：

- Core 协议闭环
- Core 最小可靠事件闭环
- Admin 最小客户端闭环
- AITool.Admin 独立宿主骨架编译通过
- AITool.Admin 独立测试宿主跑通最小启动验证

这说明我们已经从“只有设计文档”的阶段，进入了：

> **双宿主架构的真实实施阶段。**

当前真正的主任务不再是继续堆 Core 协议，而是：

- 让 Admin 真正消费并入库
- 迁移第一块真实页面或接口
- 继续清理宿主共享边界
- 再逐步把 Core 真正迁出到独立宿主

---

## 四、建议的下一步顺序

建议按这个顺序继续推进：

### 下一步

- 先让 Admin 完成一条真实事件链路的消费入库
- 优先建议选择 `UsageLog` 链路

### 再下一步

- 迁移一块低风险真实页面或接口
- 让独立 Admin 宿主开始承载真实功能

### 之后

- 再开始真正的 `AITool.Core` 宿主拆分
- 再接 patch
- 再接实时流
- 再增强 sequence/ack 持久化元数据

---

## 五、最近一轮进度同步

### 本轮完成了什么

- 继续补强了 `AITool.Admin` 中 `/Admin/UsageLogs` 对应的独立宿主只读接口，把查询参数、分页响应、汇总响应和请求详情响应全部显式收口成 DTO，减少页面端与接口端字段语义漂移
- 对 `src/AITool.Admin/Controllers/Admin/UsageLogsApiController.cs` 增加了站点模型名称解析、统一的模型关键字匹配逻辑，以及与页面筛选条件一致的查询入口
- 扩充了 `tests/AITool.Admin.IntegrationTests/AdminHostSmokeTests.cs` 的种子数据，增加了失败后重试成功、不同协议、不同来源与不同时间的日志样本，便于后续继续验证独立 Admin 宿主中的 UsageLogs 链路
- 扩充了 `tests/AITool.Admin.IntegrationTests/UsageLogsPageSmokeTests.cs`，当前已覆盖页面访问、列表/汇总/详情联动以及来源/状态/模型关键字筛选三类核心场景
- 继续完善了 `src/AITool.Admin/Pages/Admin/UsageLogs/Index.cshtml`，把模型列与详情抽屉继续向真实链路表达靠齐，开始显示请求模型、站点模型、回退/重试/最终结果标记，以及更完整的尝试级指标和错误信息
- 本轮又进一步补强了 UsageLogs 页面骨架验证，增加对新样式结构和详情展示容器的覆盖，确保页面侧新增展示逻辑至少被独立宿主测试触达
- 本轮继续补强了前端状态提示行为，汇总和列表在成功加载后会主动隐藏错误提示，避免独立 Admin 宿主中的 UsageLogs 页面在一次失败后长期残留旧错误状态
- 已重新执行 `dotnet build src/AITool.Admin/AITool.Admin.csproj`，结果为 **构建成功，0 error**
- 已重新执行 `dotnet test tests/AITool.Admin.IntegrationTests/AITool.Admin.IntegrationTests.csproj`，结果为 **5/5 通过**

### 当前还剩什么

- `/Admin/UsageLogs` 页面本身仍需继续向 `AITool.Web` 现有页面行为靠齐，尤其是样式细节、更多展示字段、列表行信息密度与详情抽屉内容完整度
- 还没有开始第二批 `/Admin/*` 页面与接口迁移，当前主线仍需在 UsageLogs 进一步收口后再进入边界清理与下一批页面迁移
- `AITool.Core` 物理独立宿主、patch 增量同步、实时事件流与 sequence/ack 持久化增强仍未进入本轮实施

### 当前阻塞点是什么

- 目前没有新的代码级阻塞
- 但独立 Admin 宿主中的 `/Admin/UsageLogs` 仍属于“已打通最小可用链路，但尚未完全对齐 Web 侧行为”的阶段，因此下一轮仍应继续围绕这块收口，而不是过早跳到 `AITool.Core` 物理拆分
- 当前构建输出仍存在来自既有 `AITool.Infrastructure` 文件的 nullability warning，本轮未改动这些历史文件，也没有影响本轮 Admin UsageLogs 相关链路验证

### 下一步准备做什么

- 继续完善 `src/AITool.Admin/Pages/Admin/UsageLogs/Index.cshtml`，把更多 Web 侧已存在的展示细节、字段和交互逐步迁入独立 Admin 宿主
- 在 UsageLogs 页面继续稳定后，开始进入宿主共享边界清理，优先判断 `DeveloperInvocationTraceStore`、`ModelConcurrencyLimiter`、`ProxyRequestMetadataCache` 这几类服务的归属
- 每完成一个小阶段后继续同步更新本文档，并补充对应测试验证结果

---

## 五、结论

当前项目状态可以概括为：

> **协议与运行时基础已经打好，双宿主也开始真正落地；现在独立 Admin 宿主已经能单独编译和测试启动，下一阶段重点应转向真实事件消费入库与第一块页面/接口迁移。**
