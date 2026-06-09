# Core / Admin 双宿主后续推进设计

日期：2026-06-09

## 目的

这份设计文档只覆盖**当前剩余未完成工作的推进顺序**，不重复描述整个 Core / Admin 拆分的全量架构。

当前已经完成的基础包括：

- Core 运行时配置快照
- full-sync / handshake / ready / runtime status
- last-good-config 本地恢复
- UsageLog / Conversation 事件发布
- sequence / spool / ack / replay 最小闭环
- Admin 最小客户端 `CoreAdminClient`
- `AITool.Admin` 独立宿主工程骨架已建立，并且宿主本体可单独 build

当前还未完成的关键事项是：

- 跑通 `AITool.Admin.IntegrationTests`
- 让一条真实事件链路完成 Admin 消费入库
- 迁移第一块真实 `/Admin/*` 页面或接口验证双宿主

这份文档给出后续实现的**固定推进顺序**，目标是继续满足“核心必须稳定、不能影响当前代理主流程”的要求。

---

## 设计结论

后续推进顺序固定为：

- 先跑通 `AITool.Admin.IntegrationTests`
- 再让 `UsageLog` 完成 Admin 消费入库
- 最后先迁 `/Admin/UsageLogs` 验证双宿主页面链路

这条顺序不是为了追求实现速度，而是为了把风险切成三层：

- 宿主能否独立启动与测试
- 数据链路能否从 Core 流到 Admin 并真正落库
- 页面能否在独立 Admin 宿主中使用这条新链路

只有按这个顺序推进，才能避免在宿主基础不稳时就迁页面，也避免在数据链路还没跑通时就让页面背锅。

---

## 当前阶段一：先跑通 `AITool.Admin.IntegrationTests`

### 目标

先确保独立 Admin 宿主可以被测试框架稳定拉起，并拥有独立的测试入口。

### 这一步为什么必须优先

虽然 `AITool.Admin` 现在已经能单独 build，但这还不等于它具备可测试性。当前真正缺的是：

- 一个独立的 `WebApplicationFactory` 入口
- 一个稳定的顶级 `Program` 暴露方式
- 独立测试项目与 Admin 宿主之间的正确引用关系
- 与现有 `AITool.Web` 的测试宿主不冲突

如果这一步没先做稳，后面无论是消费入库还是迁真实页面，都会落到一个不稳定的宿主基础上。那样后续失败时，无法判断是页面逻辑问题、消费问题，还是宿主拉起方式本身有问题。

### 这一阶段的完成标准

需要满足：

- `AITool.Admin.IntegrationTests` 能单独还原、编译、运行
- Admin 宿主能在 Testing 环境下被成功拉起
- 一个最小 smoke test 能验证宿主返回正常响应
- 这一步不影响现有 `AITool.Web` 的应用测试和集成测试

### 设计约束

- 不为追求快速打通而破坏现有 `AITool.Web` 顶级 `Program` 的稳定性
- 需要优先消除宿主级共享类型冲突与测试入口冲突
- 任何改动都必须保证现有完整测试套件继续通过

---

## 当前阶段二：让 `UsageLog` 完成 Admin 消费入库

### 目标

在 Admin 宿主具备稳定测试入口之后，让一条**真实事件链路**完成从 Core 到 Admin 的完整消费闭环。

### 为什么优先选 `UsageLog`

相比 Conversations、Developer traces 或 Analytics，`UsageLog` 更适合作为第一条完整消费链路，原因是：

- 当前 `UsageLogEntry -> CoreUsageLogEvent -> EventEnvelope -> spool/replay/ack` 已经存在
- `CoreUsageLogEvent` 与现有 `ProxyUsageLogs` 表结构高度接近
- 落库逻辑更直接，字段映射成本低
- 它是读多写少的分析型数据，不会反向影响 Core 配置执行
- 一旦这条链路跑通，后续 `/Admin/UsageLogs` 页面就有天然数据源

### 这一阶段的目标闭环

Admin 需要具备：

- 通过 `CoreAdminClient` 发起 handshake
- 根据 handshake 状态判断当前 backlog
- 调用 replay 拉取积压事件
- 过滤和解析 `usage-log` 事件
- 将事件批量写入 Admin 当前数据库中的 `ProxyUsageLogs`
- 写入成功后回 ack 给 Core
- 再次查询时看到 backlog 状态消失

### 这一阶段的完成标准

需要满足：

- Admin 能消费至少一批真实 UsageLog 事件
- Core 的 spool 中已确认的 UsageLog 事件能被正确清理
- Admin 当前数据库中能看到由 Core 事件写入的真实 UsageLog 数据
- 这条链路的测试可以单独运行，并且现有全量测试仍继续通过

### 设计约束

- 当前阶段仍然允许保留原本 `AITool.Web` 中的 UsageLogs 落库逻辑作为兼容路径
- 先以**旁路方式**验证 Admin 消费入库能力，不急着在同一阶段移除老逻辑
- 先保证数据链路成立，再做职责切换

---

## 当前阶段三：先迁 `/Admin/UsageLogs`

### 目标

在宿主和数据链路都稳定后，迁移第一块真实 `/Admin/*` 页面或接口到独立 Admin 宿主中，验证双宿主页面链路。

### 为什么优先选 `/Admin/UsageLogs`

相比 `/Admin/Conversations`、`/Admin/Routes`、`/Admin/System/Settings` 等页面，`/Admin/UsageLogs` 最适合作为第一块真实迁移目标：

- 它天然依赖第二阶段刚刚打通的 UsageLog 消费入库链路
- 是只读分析页面，不直接改 Core 配置
- 它不需要最先承载复杂的配置写回与运行时切换风险
- 它的成功可以证明：
  - 独立 Admin 宿主存在
  - Core 能产生日志事件
  - Admin 能消费并入库
  - 页面能独立读取并展示这些数据

### 为什么当前不优先迁别的页面

#### 不优先迁 `/Admin/Conversations`
因为它还涉及：

- Markdown 渲染
- tool result 格式化
- 角色拆分
- 会话聚合
- 时间筛选
- 布局与显示问题

它比 `/Admin/UsageLogs` 更复杂，适合放到 UsageLogs 页面成功后再迁。

#### 不优先迁 `/Admin/Routes` / `/Admin/Sites` / `/Admin/Models`
因为这些页面带有**核心配置写操作**。在双宿主真正稳定之前，不应把第一块迁移目标放在“直接改 Core 配置”的页面上。

### 这一阶段的完成标准

需要满足：

- `/Admin/UsageLogs` 能在独立 Admin 宿主中打开
- 页面数据来自 Admin 当前数据库
- 该数据库中的 UsageLogs 数据由 Core 事件消费链路写入
- 页面功能在双宿主下可继续工作，不影响现有 Core 代理请求

---

## 风险控制原则

整个后续阶段都必须继续遵守以下原则：

### 核心稳定优先

任何时候都不能为了让 Admin 更快拆出去，去破坏当前 Core 相关主链路。

如果某项改动会影响：

- Core 配置同步稳定性
- Core 当前代理转发能力
- 现有全量测试绿灯状态

那就必须回到更小步的推进方式。

### 先验证宿主，再验证链路，再验证页面

这个顺序不能倒过来：

- 宿主未稳就不要迁页面
- 数据链路未稳就不要让页面背锅
- 页面迁移应建立在前两者都明确成立之后

### 兼容路径先保留

在 `UsageLog` 消费入库链路完全稳定之前，允许保留当前兼容逻辑，不急着立即删除旧路径。先验证新链路成立，再考虑切职责。

---

## 后续计划边界

这份设计只定义**当前剩余工作的顺序**，不代表整个 Core / Admin 拆分已经全部完成。

在这三步之后，后面还会继续做：

- 真正的 `AITool.Core` 宿主拆分
- Conversations 事件消费入库
- `/Admin/Conversations` 页面迁移
- 配置写操作页面迁移（如 Routes / Models / Sites / System）
- patch 增量同步
- 实时流消费
- sequence / ack 元数据本地持久化增强

但这些都属于当前三步完成后的下一阶段工作，不应插队到前面来。

---

## 最终结论

当前剩余工作的最稳妥推进顺序是：

- 先跑通 `AITool.Admin.IntegrationTests`
- 再让 `UsageLog` 完成 Admin 消费入库
- 最后先迁 `/Admin/UsageLogs` 验证双宿主页面链路

这是当前最符合“核心绝对不能出问题”这一目标的实现顺序，也是最不容易在中途陷入大范围返工的路径。
