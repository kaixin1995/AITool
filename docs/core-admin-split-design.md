# Core / Admin 双进程拆分设计（方案 A：Core 无数据库）

## 背景与目标

当前系统同时承担两类职责：

- 核心代理职责：协议透传、协议兼容中转、路由决策、并发控制、熔断、缓存、访问密钥校验
- 管理与分析职责：`/Admin/*` 页面、对话记录、UsageLogs、Analytics、Detection、Developer 调试、ModelHealth 等

这两类职责的变化频率和稳定性要求完全不同：

- 核心代理需要长期稳定、极少更新、更新时尽量不影响客户端调用
- 管理与分析功能变化频繁，允许持续迭代和单独更新

因此，本设计把系统拆成两个进程：

- **Core 进程**：只提供代理与核心接口，不提供任何页面，不连接业务数据库
- **Admin 进程**：提供全部 `/Admin/*` 页面，持有当前数据库，负责配置管理、历史数据持久化、分析与展示

目标如下：

- Claude Code、Codex、OpenCode、代理客户端全部只连接 Core
- 全部界面都在 Admin 进程中
- Core 不依赖数据库，避免日志膨胀、查询压力和表结构变化影响代理稳定性
- Admin 重启或更新时，Core 继续对外服务，不影响当前正在进行中的代理调用
- Core 与 Admin 通过控制通道与事件通道通信
- 配置由 Admin 作为唯一权威源管理，Core 只维护内存中的当前生效配置快照
- Core 在 Admin 不在线时把事件积压到本地磁盘，恢复后补传

---

## 一、总体架构

```text
Claude Code / OpenCode / Codex / Proxy Clients
                    |
                    v
              +-----------+
              |   Core    |
              |  仅接口   |
              +-----------+
               |   ^   \
               |   |    \
     配置控制API |   |  事件流/补传
               v   |      \
              +-----------+
              |   Admin   |
              | 全部页面  |
              +-----------+
                    |
                    v
            当前现有数据库（仅 Admin 使用）
```

### Core 进程职责

- OpenAI 协议代理接口
- Anthropic 协议代理接口
- 模型列表接口
- 核心配置查询与控制接口
- 路由决策、协议桥接、并发控制、熔断、缓存
- 维护内存中的当前配置快照
- 生成运行事件并发送给 Admin
- 在 Admin 不在线时把事件缓冲到本地 spool 文件

### Admin 进程职责

- 全部 `/Admin/*` 页面
- 当前数据库的唯一使用方
- 站点、模型、路由、访问密钥、运行时设置的管理界面
- 配置变更写数据库
- 把完整配置或增量变更同步给 Core
- 接收 Core 的运行事件并写入数据库
- UsageLogs、Conversations、Analytics、Detection、Developer、ModelHealth 的展示与分析

---

## 二、核心设计原则

### Core 不依赖数据库

Core 不连接当前业务数据库，不使用 EF Core 读取核心主数据，也不负责写入 UsageLogs / Conversations / Analytics 数据。

Core 只依赖：

- 自身进程
- 内存中的生效配置快照
- 与 Admin 的控制连接
- 与 Admin 的事件连接
- 本地配置快照文件
- 本地 spool 文件

### Admin 是配置权威源

以下配置的唯一权威来源都是 Admin：

- Sites
- ModelLibraryItems
- SiteModelMappings
- ProxyRouteEntries
- ProxyRouteRules
- ProxyAccessKeys
- SystemRuntimeSettings 中与 Core 运行相关的部分

Core 只消费由 Admin 发送过来的配置快照，不自行修改主配置。

### Core 只认版本化配置快照

每次配置同步都必须带：

- `ConfigVersion`
- `ConfigHash`
- `GeneratedAt`
- 完整快照或增量变更

Core 只在确认版本和 hash 合法时才切换配置。

### Core 与 Admin 的通信解耦

通信分两类：

- **配置控制通道**：Admin → Core，用于查询状态、发送全量配置、发送增量更新、触发重载
- **事件通道**：Core → Admin，用于推送 UsageLogs、Conversations、Developer traces、Detection 结果等运行事件

### Core 必须能在 Admin 离线时独立继续运行

当 Admin 更新、重启或崩溃时，Core：

- 继续使用当前内存配置对外提供代理能力
- 不中断当前已开始的请求
- 不因无法写日志或无法连 Admin 而失败
- 把运行事件先放入内存短队列，再落到本地 spool 文件中

### 配置更新必须原子切换

Core 不能出现半套新配置、半套旧配置的状态。任何一次配置更新，无论是全量还是增量，都必须：

- 先在候选副本中完成构建
- 校验合法性
- 校验 hash
- 一次性替换当前快照引用

失败时继续使用旧快照。

---

## 三、进程边界与项目边界

### Core 进程建议保留内容

- `OpenAiProxyController`
- `AnthropicProxyController`
- 协议桥接相关服务
- 路由选择与运行时缓存
- 并发限制与熔断
- 访问密钥校验
- 模型列表输出
- 健康检查与状态接口
- Core 配置控制接口
- 事件总线、sequence、spool 管理

### Admin 进程建议保留内容

- `/Admin/Chat`
- `/Admin/Conversations`
- `/Admin/Detection`
- `/Admin/DetectionTasks`
- `/Admin/ModelHealth`
- `/Admin/Developer/*`
- `/Admin/UsageLogs`
- `/Admin/Analytics`
- 站点、模型、路由、访问密钥、运行时设置管理页面
- Core 通信客户端
- 事件消费与入库

---

## 四、数据库使用策略

### 当前数据库的角色

当前现有数据库直接归 Admin 使用，Core 不再连接它。

Admin 负责：

- 配置写入数据库
- 页面查询数据库
- Core 事件消费后入库
- 历史 UsageLogs、Conversations、Detection 等数据继续沿用

### Core 不使用当前数据库

Core 不从数据库读这些内容：

- Sites
- Models
- Routes
- AccessKeys
- RuntimeSettings
- UsageLogs
- Conversations

这些都来自 Admin 的配置同步与事件消费体系。

---

## 五、配置快照模型

Core 实际运行时只使用一个 **RuntimeConfigSnapshot**。

### 快照应至少包含

- `ConfigVersion`
- `ConfigHash`
- `GeneratedAt`
- `Sites`
- `Models`
- `SiteModelMappings`
- `ProxyRouteEntries`
- `ProxyRouteRules`
- `ProxyAccessKeys`
- `CoreRuntimeSettings`

### 运行时要求

- 所有集合都是完整、可独立运行的
- 任意时刻 Core 只持有一份“当前生效快照”
- 已开始请求使用进入请求时拿到的那份快照，不被中途替换影响
- 新请求使用最新已切换的快照

---

## 六、配置版本与配置 hash

### ConfigVersion

建议使用单调递增的 long。

规则：

- 初始全量导入时为 1
- 每次核心配置变动（站点、模型、路由、访问密钥、核心运行设置）时 +1

### ConfigHash

由完整配置快照计算得出，用于：

- 判断重启后配置是否真的有变化
- 检测漂移
- 验证增量更新后的结果是否和 Admin 预期一致

### 两者配合的意义

- 版本一致 + hash 一致：配置无变化，可忽略
- 版本一致 + hash 不一致：发生漂移，必须全量重同步
- 版本不同：按差异走增量或全量更新

---

## 七、Admin 重启与重连边界

### 核心目标

Admin 重启不能中断 Core 的代理能力，也不能导致 Core 丢失运行数据。

### Admin 重启时 Core 的行为

- 保持当前配置快照不变
- 继续提供代理服务
- 控制通道断开
- 事件通道断开
- 新事件进入内存短队列
- 内存积压达到阈值或连接持续不可用时写入本地 spool 文件

### Admin 恢复时的握手流程

Admin 恢复后应先与 Core 握手，交换以下状态：

#### Admin → Core

- `CurrentConfigVersion`
- `CurrentConfigHash`
- `LastAckedSequenceId`
- `AdminInstanceId`
- `AdminStartedAt`

#### Core → Admin

- `AppliedConfigVersion`
- `AppliedConfigHash`
- `LatestSequenceId`
- `ActiveRequestCount`
- `SpoolStatus`
- `CoreInstanceId`
- `CoreStartedAt`

### 无变化则忽略

当且仅当：

- `Admin.CurrentConfigVersion == Core.AppliedConfigVersion`
- `Admin.CurrentConfigHash == Core.AppliedConfigHash`

则认为配置无变化：

- Core 无需重新应用配置
- Admin 直接进入事件补传阶段

### 有变化则更新

若配置版本或 hash 不一致：

- 优先尝试增量更新
- 增量不适用时改为全量同步

### 漂移处理

如果版本相同但 hash 不同，说明 Core 与 Admin 状态不一致：

- 不允许继续信任增量 patch
- 直接强制全量重同步

---

## 八、全量同步设计

### 全量同步使用场景

- Core 首次启动
- Core 本地没有上次成功配置快照
- Admin 首次连接 Core
- Admin 重启后发现漂移
- 增量更新被拒绝或失败
- 管理员主动要求重载全部配置

### 全量同步流程

1. Admin 从数据库读取完整核心配置
2. 构建 `RuntimeConfigSnapshot`
3. 生成 `ConfigVersion` 与 `ConfigHash`
4. 发送给 Core
5. Core 构建候选快照副本
6. 校验合法性与 hash
7. 原子替换当前快照引用
8. 返回已应用确认

### Core 启动时的两种状态

#### 有本地 `last-good-config`

- Core 可直接加载上次成功配置并进入可服务状态
- 等 Admin 恢复后再做版本校准

#### 没有任何配置快照

- Core 进入 `NotReady`
- 拒绝代理请求
- 直到 Admin 发来第一份全量快照才进入 `Ready`

### 推荐做法

即使 Core 不用数据库，也建议保留本地文件：

- `last-good-config.json`
- `config-meta.json`

用于启动恢复和 Admin 不在线时的持续服务。

---

## 九、增量更新设计

### 增量更新的目的

- 减少全量重载的频率
- 在配置局部变化时提高切换效率
- 降低大配置频繁重构成本

### 增量粒度建议

不要做字段级 patch，建议按资源集合分组：

- `SitesChanges`
- `ModelsChanges`
- `SiteModelMappingsChanges`
- `RouteEntriesChanges`
- `RouteRulesChanges`
- `AccessKeysChanges`
- `RuntimeSettingsChanges`

每组支持：

- `Added`
- `Updated`
- `Removed`

### 增量更新必须带 baseVersion

每个 patch 必须带：

- `ConfigVersion`
- `BaseVersion`
- `ConfigHash`
- `Changes`

### Core 应用增量的规则

- 若 `BaseVersion != Core.AppliedConfigVersion`，拒绝 patch
- 必须先复制当前快照
- 在副本中应用 patch
- 校验完整性
- 校验应用后 hash 与 Admin 提供的一致
- 成功后原子替换当前快照

### 增量失败后的策略

任何一项校验失败：

- 不切换配置
- 继续使用旧快照
- 返回错误给 Admin
- Admin 应立即改发全量快照

---

## 十、请求与配置切换边界

### 已开始请求不受新配置中断

对任何代理请求：

- 进入请求时获取一份当前快照引用
- 整个请求生命周期都使用这份快照
- 即使配置切换，已开始请求也不被中断
- 新请求才使用新快照

### 与当前路由保护逻辑保持一致

这与现有“活跃请求不应被路由变更中断”的目标一致，只是粒度从“单模型路由顺序”扩展为“整个配置快照切换”。

---

## 十一、事件流设计

### Core 要输出的事件类型

至少建议包含：

- `UsageLogEvent`
- `ConversationTurnEvent`
- `DeveloperTraceEvent`
- `DetectionResultEvent`
- `RouteFallbackEvent`
- `CircuitBreakerEvent`
- `HealthProbeEvent`
- `ActiveRequestSnapshotEvent`（可选）

### 统一 sequenceId

所有事件都使用统一全局递增 `SequenceId`。

作用：

- 断线补传
- 去重
- 排序
- ack 删除 spool

### 每条事件建议字段

- `SequenceId`
- `EventType`
- `OccurredAt`
- `RequestId`（如有）
- `SessionId`（如有）
- `SourceTool`（如有）
- `Payload`

### 事件设计原则

Core 输出的数据要尽量“字段完整”，避免 Admin 后续新增统计页面时被迫回头改 Core。

例如 UsageLogEvent 建议一次带全：

- RequestId
- SourceTool
- SessionId
- RequestModel
- AttemptedModel
- SiteId
- SiteName
- SiteModelName
- ProtocolType
- ForwardingMode
- Status
- RetryCount
- AttemptIndex
- FallbackTriggered
- InputTokens
- CachedTokens
- OutputTokens
- FirstTokenLatencyMs
- StreamDurationMs
- TotalDurationMs
- ErrorMessage
- Timestamps
- 关联 metadata

---

## 十二、Core 本地 spool 设计

### 设计目标

当 Admin 不在线时，Core 不能因为事件无法发送而影响主代理流程，也不能无限堆内存。

因此采用两层缓冲：

#### 第一层：内存短队列

- 高吞吐
- 处理短时抖动
- 降低磁盘写放大

#### 第二层：磁盘 spool

- Admin 长时间离线时落盘
- 防止内存爆掉
- 通信恢复后补传

### spool 文件格式建议

使用 JSONL：

```text
spool/
  events-000001.jsonl
  events-000002.jsonl
```

每行一条：

```json
{"sequenceId":1001,"eventType":"usage-log","occurredAt":"2026-06-08T10:22:33Z","payload":{...}}
```

### 滚动策略建议

例如：

- 单文件 10MB 滚动
- 或每 5000 条滚动

### 删除策略

不能发送后立刻删，只能在 **Admin ack** 之后删。

也就是说：

- Core 记录 `LastAckedSequenceId`
- 只有 `SequenceId <= LastAckedSequenceId` 的事件才可删除

### 极端积压保护

必须设置最大 spool 容量，例如 2GB 或 5GB。

当达到上限时：

- 优先保留最新事件
- 丢弃最旧未确认事件
- 输出严重告警日志

核心原则：

> 允许有限丢失观察型数据，也不能让 Core 因磁盘写满崩掉。

---

## 十三、事件补传与 ack 机制

### 实时模式

Admin 在线时：

1. Core 生成事件
2. 放入内存短队列
3. 通过长连接实时发送给 Admin
4. Admin 成功入库后 ack
5. Core 标记已确认，可从队列/缓存中移除

### 离线模式

Admin 离线时：

1. Core 继续生成事件
2. 先入内存队列
3. 达到阈值或连接持续失败后刷入 spool
4. Core 继续代理，不阻塞主流程

### 恢复模式

通信恢复后：

1. Admin 发送 `LastAckedSequenceId`
2. Core 从该序号之后开始补发
3. 先补 spool 中积压事件
4. 追平最新事件后再切回实时流
5. 补传成功后删除已确认 spool 数据

### 为什么必须有 ack

如果没有 ack：

- Core 不知道哪些事件已经成功入库
- 无法安全删除 spool
- 断线重连容易重复或漏数据

因此：

- `SequenceId`
- `LastAckedSequenceId`
- `AckBatch`

是必须的。

---

## 十四、配置同步与补传的执行顺序

Admin 恢复后，推荐严格按以下顺序执行：

### 第一步：握手

交换：

- 当前配置版本与 hash
- 最新事件 sequence
- 最后 ack 序号
- 当前活跃请求数

### 第二步：配置校准

- 配置无变化则跳过
- 有变化则增量或全量同步
- 只有 Core 确认已应用成功后，才继续下一步

### 第三步：补传积压事件

- 从 `LastAckedSequenceId + 1` 开始补发
- 直到追平 Core 当前最新 sequence

### 第四步：切回实时流

- 进入长连接实时消费状态

### 为什么先配配置再补事件

因为某些事件的解释和展示口径依赖当前配置语义。先让 Core 与 Admin 的配置版本一致，再恢复事件消费更稳妥。

---

## 十五、Core 本地最小持久化建议

虽然 Core 不使用数据库，但建议保留极小的本地文件状态：

- `last-good-config.json`
- `config-meta.json`
- `spool/`
- `sequence.meta`
- `ack.meta`

### 作用

- Core 重启后恢复最后一次成功配置
- Core 重启后恢复未 ack 的事件状态
- Admin 暂时不在线时继续服务
- 避免把配置和事件状态完全压在内存里

---

## 十六、Admin 入库策略

Admin 收到事件后，不建议逐条同步写库。

### 建议流程

- 接收事件
- 按 sequence 连续性校验
- 放入内存缓冲
- 批量写入数据库
- 批量 ack 给 Core

### 好处

- 降低 SQLite 写放大
- 提高吞吐
- 避免长连接事件消费成为数据库写锁瓶颈

---

## 十七、状态机建议

### Core 状态

- `NotReady`：没有可用配置，拒绝代理
- `Ready`：有生效配置，可正常代理
- `Draining`：后续若做无中断更新，可扩展为不接新请求只跑旧请求
- `AdminDisconnected`：Admin 离线，但 Core 继续代理并 spool 事件

### Admin 状态

- `Disconnected`：未连上 Core
- `Handshaking`：正在交换版本与 ack 状态
- `ConfigSyncing`：正在同步配置
- `Replaying`：正在补传积压事件
- `Streaming`：正在实时消费事件

---

## 十八、关键边界与处理策略

### 边界一：Admin 重启后重复发送同一配置

处理：

- Core 比较 version + hash
- 完全相同则忽略

### 边界二：Admin 发 patch 时 Core 已不是对应 baseVersion

处理：

- 拒绝 patch
- 记录日志
- 要求 Admin 发全量配置

### 边界三：版本相同但 hash 不同

处理：

- 视为漂移
- 不能继续增量
- 强制全量同步

### 边界四：Core 正在流式输出时配置更新

处理：

- 旧请求继续使用旧快照
- 新请求使用新快照
- 不中断当前流

### 边界五：Admin 长时间离线

处理：

- Core 继续服务
- 事件持续 spool
- 达上限后丢最旧事件并告警

### 边界六：Core 重启但 Admin 未恢复

处理：

- 从 `last-good-config.json` 恢复
- 从 spool 恢复未确认事件
- 继续代理
- 等 Admin 上线后补传

### 边界七：Admin 入库失败

处理：

- 不 ack
- Core 保留事件
- Admin 恢复后重新处理

---

## 十九、实施路线（先设计后编码）

### 阶段一：先定义契约

先输出稳定契约：

- `RuntimeConfigSnapshot`
- `RuntimeConfigPatch`
- `HandshakeRequest / Response`
- `EventEnvelope`
- `AckEnvelope`

这一步不能跳。

### 阶段二：Core 具备无数据库运行能力

- 加载本地 `last-good-config`
- 具备 `Ready / NotReady` 状态
- 无数据库也能代理

### 阶段三：Core 具备事件缓冲能力

- 内存队列
- spool 文件
- sequence / ack 元数据

### 阶段四：Admin 具备配置下发能力

- 数据库读取配置
- 发送全量配置
- 完成首次握手

### 阶段五：Admin 具备事件消费入库能力

- 接收事件
- 批量写库
- ack

### 阶段六：补全增量更新

- patch 构建
- baseVersion 校验
- 候选快照应用与切换

### 阶段七：补齐实时状态展示

- 活跃请求数
- 当前配置版本
- 当前 Admin / Core 连接状态
- spool 积压状态

---

## 二十、与当前系统的关系

当前系统中，管理页、UsageLogs、Conversations、Analytics、Developer、Detection 等都与页面强耦合，更新频率高；而代理核心更稳定，更新频率低。

该方案的核心价值在于：

- 以后新增页面、统计、调试功能尽量只改 Admin
- Core 尽量保持接口与事件模型稳定
- 管理界面更新不再需要动 Core
- Admin 更新时，Core 对客户端调用保持连续服务

---

## 二十一、结论

方案 A（Core 完全无数据库，配置全部来自 Admin，事件通过长连接推送，Admin 离线时 Core 本地 spool，恢复后补传）是可行的，并且非常符合“核心稳定、外围高频演进”的目标。

它要稳妥落地，关键不在于是否使用长连接，而在于以下能力必须一起具备：

- 版本化配置快照
- 配置 hash 校验
- 全量同步兜底
- 基于 baseVersion 的增量 patch
- Core 本地 `last-good-config`
- 统一事件 `sequenceId`
- Admin `ack`
- Core 本地 spool
- 重连补传与实时流切换
- 已开始请求不受配置切换中断

只要这些边界设计扎实，后续就能把 Core 做成一个长期稳定、几乎不需要频繁更新的纯后端代理服务，而把所有页面、日志、统计、分析能力都放到 Admin 进程中独立演进。
