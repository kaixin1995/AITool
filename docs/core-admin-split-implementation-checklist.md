# Core / Admin 双进程拆分实施清单

## 文档目的

这份文档用于把当前单体系统拆成 **Core 进程** 与 **Admin 进程** 时，给出一份可执行、可逐步落地的实施清单。

目标约束如下：

- Core 进程只有接口，没有任何 Web 页面
- Claude Code、Codex、OpenCode、代理客户端全部连接 Core
- 全部 `/Admin/*` 页面都迁到 Admin
- Core 不连接当前业务数据库
- 当前数据库继续由 Admin 使用，保证历史数据可继续展示
- Admin 负责配置管理、Core 配置同步、事件消费入库
- Core 在 Admin 不在线时继续服务，并把运行事件暂存到本地 spool，恢复后补传

这份文档不修改代码，只定义拆分边界、模块归属、通信方式和实施顺序。

---

## 一、拆分后的系统形态

### Core 进程

职责：

- OpenAI / Anthropic 协议代理入口
- 协议兼容中转
- 路由选择
- 并发限制
- 熔断
- 访问密钥校验
- 运行时缓存
- 健康检查与状态接口
- 配置快照管理
- 事件生成、spool、补传

特点：

- 无 Razor 页面
- 无 `/Admin/*`
- 不使用当前数据库
- 只消费 Admin 下发的配置快照
- 对外提供稳定的代理 API 与 Core API

### Admin 进程

职责：

- 全部 `/Admin/*` 页面
- 当前数据库的唯一使用方
- 历史 UsageLogs / Conversations / Detection / Analytics / Developer 数据继续使用
- 站点、模型、路由、访问密钥、运行时设置的管理
- 负责从数据库构建配置快照并同步给 Core
- 负责接收 Core 事件并入库
- 负责全部展示、统计、分析、调试、检测能力

特点：

- 变化频率高
- 可以单独更新
- 挂掉或重启时不应影响 Core 对外代理请求

---

## 二、目录与项目边界建议

建议最终形成两个独立宿主：

- `AITool.Core`
- `AITool.Admin`

同时保留共享的领域与基础设施项目，例如：

- `AITool.Domain`
- `AITool.Application`
- `AITool.Infrastructure`

但要逐步把 `Infrastructure` 中与 Core 强相关和与 Admin 强相关的部分拆清。

推荐概念上的进一步分组：

### Core 侧共享模块

- `AITool.Core.Runtime`
- `AITool.Core.ProtocolBridge`
- `AITool.Core.Communication`

### Admin 侧共享模块

- `AITool.Admin.Communication`
- `AITool.Admin.Analytics`
- `AITool.Admin.Conversations`
- `AITool.Admin.Developer`
- `AITool.Admin.Detection`

第一阶段不要求立刻拆成这么细，但概念边界要先按这个方向整理。

---

## 三、当前代码归属清单

下面按当前项目现状，列出建议归属。

---

## 3.1 建议保留在 Core 的 Controller

### 必留

- `src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs`
- `src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Helpers.cs`
- `src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs`

### 后续应新增（Core 专用）

- `CoreHealthController`
- `CoreReadyController`
- `CoreConfigController`
- `CoreRuntimeStatusController`
- `CoreDrainController`（后续若做无中断更新）

### 理由

这些 Controller 是：

- 客户端真实调用入口
- 核心配置执行入口
- Core 与 Admin 的控制边界

它们必须和代理运行时保持在同一个进程内。

---

## 3.2 建议迁到 Admin 的 Controller

### 必迁

- `src/AITool.Web/Controllers/Admin/ChatApiController.cs`
- `src/AITool.Web/Controllers/Admin/ConversationsApiController.cs`
- `src/AITool.Web/Controllers/Admin/UsageLogsApiController.cs`
- `src/AITool.Web/Controllers/Admin/AnalyticsApiController.cs`
- `src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs`
- 其他 `/Controllers/Admin/*`

### 说明

这里有一类特殊项：

- `RouteRulesApiController`
- 站点、模型、访问密钥、系统设置相关 API

它们对应的**页面和管理入口**应在 Admin，但其**最终写操作语义**应收口到 Core 配置同步机制。

也就是说，Admin 页面调用自己的控制器后，最终做的是：

- 写 Admin 当前数据库
- 触发配置版本递增
- 下发增量或全量配置给 Core

而不是让 Core 再去数据库读。

---

## 3.3 建议保留在 Core 的服务

### 协议与转发

- `src/AITool.Web/Services/ProxyProtocol/*`
- `src/AITool.Infrastructure/Proxy/ProxyForwardService.cs`
- 与 OpenAI / Anthropic 协议桥接有关的实现

### 路由与运行时状态

- `src/AITool.Web/Services/ProxyRequestMetadataCache.cs`
- `src/AITool.Web/Services/ModelConcurrencyLimiter.cs`
- `RouteCircuitStateStore`（若存在）
- 访问密钥校验缓存
- 路由快照、模型并发、熔断状态

### Core 新增能力（后续新增）

- `RuntimeConfigSnapshotManager`
- `RuntimeConfigPatchApplier`
- `CoreEventBus`
- `CoreSpoolService`
- `CoreSequenceService`
- `CoreAdminControlChannelService`
- `CoreAdminEventStreamService`
- `CoreLastGoodConfigStore`

### 理由

这些服务都直接影响代理请求成功与否，必须与代理主流程同进程。

---

## 3.4 建议迁到 Admin 的服务

### 对话记录与页面存储

- `src/AITool.Infrastructure/Conversations/*` 中偏展示、查询、持久化的部分
- Conversations 页面相关服务

### Usage / Analytics / Developer

- UsageLogs 查询与统计服务
- Analytics 聚合服务
- DeveloperInvocationTraceStore
- Conversations 展示格式化逻辑（可继续在 Admin）

### Detection / ModelHealth

- `ModelHealthRequestService`（建议 Admin 持有）
- DetectionTask 相关调度展示能力
- Detection 历史存储和展示逻辑

### Admin 新增能力（后续新增）

- `CoreConfigPublisher`
- `CoreEventConsumer`
- `CoreReplayClient`
- `CoreHandshakeClient`
- `AdminSequenceAckStore`
- `AdminEventIngestBatchWriter`

---

## 四、页面迁移清单

以下页面全部迁到 Admin：

### 聊天与对话

- `/Admin/Chat`
- `/Admin/Conversations`

### 检测

- `/Admin/Detection`
- `/Admin/DetectionTasks`
- `/Admin/ModelHealth`

### 日志与分析

- `/Admin/UsageLogs`
- `/Admin/Analytics`

### 调试

- `/Admin/Developer/*`

### 配置管理页面

- `/Admin/Sites/*`
- `/Admin/Models/*`
- `/Admin/Routes/*`
- `/Admin/AccessKeys/*`
- `/Admin/System/*`

### 注意

这些页面迁走后，Core 不再带任何 Razor 页面，不再承担 UI 布局、CSS、脚本、交互逻辑。

---

## 五、数据库职责清单

在方案 A 下，**当前数据库仅由 Admin 使用**。

### 当前数据库继续保留的数据

#### 核心配置主数据（由 Admin 管理）

- Sites
- ModelLibraryItems
- SiteModelMappings
- ProxyRouteEntries
- ProxyRouteRules
- ProxyAccessKeys
- SystemRuntimeSettings

#### 展示与分析数据

- ProxyUsageLogs
- ConversationTurnLogs
- DetectionTasks
- DetectionTaskExecutions
- Analytics 相关数据
- Developer traces
- ModelHealth 展示历史

### Core 不连接数据库

Core 不再：

- 查询 Sites
- 查询 ModelLibraryItems
- 查询 ProxyRouteRules
- 查询 ProxyAccessKeys
- 查询或写 UsageLogs
- 查询或写 ConversationTurnLogs

### 结果

- 数据库完全成为 Admin 的数据中心
- Core 成为无数据库的纯运行时后端

---

## 六、配置同步职责清单

这是拆分后最关键的职责切换。

### 由 Admin 负责的事

- 从数据库读取核心相关主数据
- 构建完整配置快照
- 生成 `ConfigVersion`
- 生成 `ConfigHash`
- 判断哪些变更可增量表达
- 决定发全量还是发 patch
- 跟踪 Core 当前已应用到的配置版本

### 由 Core 负责的事

- 保存当前已生效配置快照
- 校验全量快照合法性
- 校验 patch 的 baseVersion
- 在候选副本上应用 patch
- 原子切换当前快照
- 保留 `last-good-config`

### Admin 重启时

Admin 恢复后必须：

- 先握手
- 比对 Core 当前版本与 hash
- **无变化则忽略**
- **有变化则增量更新**
- 增量失败则全量同步

这正是方案 A 最需要被稳妥处理的边界。

---

## 七、事件流职责清单

Core 应把这些运行数据事件化，不再自己落业务数据库。

### 事件类型建议

#### Usage / 请求链路类

- `UsageLogEvent`
- `RouteFallbackEvent`
- `CircuitBreakerEvent`

#### 对话类

- `ConversationTurnEvent`

#### Developer 类

- `DeveloperTraceEvent`

#### Detection / Health 类

- `DetectionResultEvent`
- `HealthProbeEvent`

#### 状态类

- `ActiveRequestSnapshotEvent`
- `ConfigAppliedEvent`

### Admin 负责

- 接收事件
- 批量入库
- 成功后 ack
- 页面直接读本地数据库展示

### Core 负责

- 生成事件
- 统一分配 `SequenceId`
- 推送实时事件
- Admin 离线时 spool
- Admin 恢复后补传

---

## 八、通信接口清单

### 8.1 客户端 → Core

保留：

- `/v1/chat/completions`
- `/v1/responses`
- `/v1/messages`
- `/v1/models`
- 其他兼容协议接口

### 8.2 Admin → Core（控制通道）

建议接口：

- `/api/core/config/full-sync`
- `/api/core/config/patch`
- `/api/core/config/status`
- `/api/core/runtime/status`
- `/api/core/runtime/active-requests`
- `/api/core/health`
- `/api/core/ready`

### 8.3 Core → Admin（事件通道）

建议使用：

- gRPC streaming
- 或 WebSocket

不建议第一版自己手写 TCP 协议。

### 8.4 Admin → Core（重连补传）

握手时携带：

- `LastAckedSequenceId`
- `CurrentConfigVersion`
- `CurrentConfigHash`

Core 返回：

- `AppliedConfigVersion`
- `AppliedConfigHash`
- `LatestSequenceId`
- `SpoolStatus`

---

## 九、Core 本地文件职责清单

因为 Core 不用数据库，所以需要最小本地文件状态。

### 必备文件

- `last-good-config.json`
- `config-meta.json`
- `sequence.meta`
- `ack.meta`
- `spool/` 目录

### 职责

- Core 重启后恢复上次成功配置
- Core 重启后恢复未确认事件状态
- Admin 离线时持久化事件积压
- Core 即使无数据库也能继续运行

### 说明

这不违背“Core 无数据库”的设计，因为这些只是本地运行元数据，不是业务数据库。

---

## 十、按阶段实施的具体清单

下面给出建议的实际实施顺序。

---

## 阶段 1：先明确边界，不改部署

### 目标

- 先把 Core 和 Admin 的职责从代码层面梳理清楚
- 先不急着拆成两个可执行程序

### 动作

- 标记所有 Controller 的归属（Core / Admin）
- 标记所有 Service 的归属（Core / Admin）
- 梳理哪些页面依赖哪些服务
- 梳理哪些服务仍直接查询数据库

### 验收标准

- 能得到一份明确的 Controller / Service 归属表
- 能列出必须通信的边界点

---

## 阶段 2：实现 Core 无数据库运行模型

### 目标

- Core 仅依赖配置快照与本地状态文件
- 不再要求数据库可用

### 动作

- 设计 `RuntimeConfigSnapshot`
- 设计 `ConfigVersion + ConfigHash`
- 增加 `last-good-config` 本地恢复能力
- 增加 `Ready / NotReady` 状态

### 验收标准

- Core 在不连数据库的情况下可启动
- 有 `last-good-config` 时可直接进入可服务状态
- 无配置时返回 not ready

---

## 阶段 3：实现 Admin → Core 全量配置同步

### 目标

- Admin 能从数据库读取完整配置并发给 Core

### 动作

- Admin 构建完整快照
- Core 接收并应用全量快照
- Core 保存最新成功配置到本地文件

### 验收标准

- 清空 Core 本地配置后，Admin 能重新把它拉起到 Ready
- Core 应用后的版本和 hash 可查询

---

## 阶段 4：实现 Core → Admin 事件流 + spool

### 目标

- Core 不再要求自己写 UsageLogs / Conversations
- Admin 消费事件并入库

### 动作

- 统一定义 `EventEnvelope`
- Core 生成 sequenceId
- Admin 入库后 ack
- Core 支持本地 spool
- Admin 重启后支持补传

### 验收标准

- Admin 在线时，事件实时入库
- Admin 离线时，Core 继续代理且事件写入 spool
- Admin 恢复后，积压事件可补传并入库

---

## 阶段 5：把 `/Admin/*` 页面迁到 Admin

### 目标

- Core 不再带页面
- 所有管理界面完全在 Admin

### 动作

- 迁 Razor Pages
- 迁 Admin Controllers
- 页面读数据库继续使用 Admin 当前库
- 页面写操作改成：更新数据库并触发 Core 配置同步

### 验收标准

- Core 去掉 `/Admin/*` 后，Claude Code 等代理调用不受影响
- Admin 页面仍能完整查看历史数据

---

## 阶段 6：补增量 patch

### 目标

- 配置无变化则忽略
- 有变化则增量同步
- 失败时自动退回全量

### 动作

- 定义 `RuntimeConfigPatch`
- Admin 构建变更集
- Core 校验 `baseVersion`
- Core 在候选副本上应用 patch

### 验收标准

- 同一配置重复下发时 Core 忽略
- 局部配置变化时 Core 无需全量切换
- patch 不匹配时自动转全量

---

## 阶段 7：再考虑无中断更新

### 目标

- 只给 Core 做无中断更新
- Admin 单独更新即可

### 动作

- 为 Core 增加 active request drain 能力
- 配合前置代理做蓝绿/排空

### 验收标准

- Admin 更新不影响代理
- Core 更新时尽量不打断已开始请求

---

## 十一、优先级建议

### 最高优先级

- Core 无数据库配置快照
- Admin → Core 全量配置同步
- Core → Admin 事件流 + spool

### 中优先级

- `/Admin/*` 页面整体迁移
- 页面写操作改为通过配置同步生效

### 低优先级

- patch 增量优化
- Core 无中断更新
- 数据结构瘦身

---

## 十二、风险与回滚建议

### 风险一：页面仍强耦合核心运行时实现

#### 建议

先做接口收口，不要让 Admin 页面继续直接依赖 Core 内部类。

### 风险二：事件字段不够全

#### 建议

事件模型一次设计尽量丰富，否则后续页面一新增又要回头改 Core。

### 风险三：Admin 重启期间丢数据

#### 建议

必须实现：

- `SequenceId`
- `Ack`
- `Spool`
- `Replay`

### 风险四：配置更新影响已开始请求

#### 建议

Core 必须按“请求进入时绑定快照”的方式运行，不能在处理中途切换。

### 风险五：拆分初期出问题难回滚

#### 建议

在真正双进程上线前，保留原单体验证通路：

- 可先并行验证 Core / Admin 新通道
- 出问题时能快速回到单体模式

---

## 十三、最终结论

当前项目如果按方案 A 拆分，最合适的实施路线是：

- **Core 成为无数据库、纯接口、纯运行时的后端代理服务**
- **Admin 成为唯一数据库使用方与全部页面承载方**
- **配置通过快照/patch 从 Admin 同步到 Core**
- **运行时数据通过事件流从 Core 推送到 Admin**
- **Admin 离线时 Core 用内存队列 + 本地 spool 兜底**
- **Admin 恢复后根据 version/hash 实现无变化忽略、有变化增量更新**

按这个顺序拆，可以最大限度降低对现有历史数据和现有功能的冲击，同时把你最想保护的 Core 稳定性从根上提升上来。
