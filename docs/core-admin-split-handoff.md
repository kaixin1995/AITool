# Core / Admin 双宿主拆分交接文档

## 文档目的

这份文档是给**下一个新会话**直接接力开发使用的交接文档。

目标是让下一个会话在阅读完本文档与相关基线文档后，可以：

- 直接理解当前拆分进度
- 明确哪些已经完成，哪些还没完成
- 知道最近几轮实际改了什么代码
- 明确当前最合适的下一步是什么
- 避免重复做已经完成的工作
- 避免误动高风险运行时链路

---

## 新会话开始后的必读顺序

每一轮开始时，仍然必须先阅读并综合以下文档：

- [core-admin-split-progress.md](core-admin-split-progress.md)
- [core-admin-split-design.md](core-admin-split-design.md)
- [core-admin-split-implementation-checklist.md](core-admin-split-implementation-checklist.md)
- [core-admin-split-communication-protocol.md](core-admin-split-communication-protocol.md)
- **本文件** [core-admin-split-handoff.md](core-admin-split-handoff.md)

推荐顺序：

- 先看 `progress`
- 再看 `handoff`
- 再回看 `design / checklist / protocol`
- 然后决定本轮具体工作

---

## 当前用户约束与执行规则

这些规则在后续会话中应继续遵守：

- 目标是**继续全部未完成任务，直到全部任务全部完成**
- 开发时必须始终优先保证 **Core / Proxy 主链路稳定**
- **绝对不要破坏现有 `AITool.Web` 的代理能力**
- 每一小阶段后都要同步更新 [core-admin-split-progress.md](core-admin-split-progress.md)
- 每轮改动后都要跑相关测试和必要构建
- 如果只是局部改动，就跑最相关测试
- 如果完成一个阶段，就跑更完整的测试集与必要构建
- 若遇到失败、超时、偶发错误、宿主冲突或环境问题，应先在当前轮修复；修不完就写进 `progress` 文档，不要丢任务
- 新增代码注释要自然、详细，属性和函数使用标准 XML 注释，函数体内使用正常中文注释，不要使用带序号的注释

### 关于 git 提交

有一个重要变更：

用户后来**明确撤销了“每个小阶段都必须提交 git”这个硬要求**。

因此后续规则应按**最新指令**理解为：

- 可以继续做阶段性 git 提交
- 但**不再强制每一个小阶段都必须提交**
- 文档同步和测试验证仍然必须做

### 关于 Skill

本项目的全局规则仍然是：

- **除非用户手动显式要求调用 Skill，否则不要自己调用 Skill**

---

## 当前仓库状态（交接时）

### 分支

- 当前分支：`split-core-admin-architecture`

### 工作区状态

- 交接整理时 `git status --short` **无输出**
- 即：**工作区是干净的**

### 最近重要提交

最近关键提交（与本轮拆分强相关）包括：

- `b2acef1` 收口共享宿主层服务归属调整
- `f5ab2ed` 修正 Admin UsageLogs 状态提示与页面验证
- `a54c027` 补强 Admin UsageLogs 页面展示验证
- `9c286a9` 完善 Admin UsageLogs 页面链路展示
- `4ba907e` 文档更新

### 关于 loop / goal

本会话中曾创建过多个 `/loop` 任务，也设置过 `/goal`。

但需要注意：

- `/loop` 是 **session-only** 的，不会自动继承到新会话
- `/goal` 也是当前会话级别的
- 新会话里如果需要继续自动推进，需要用户重新设置

因此在新会话中不要假设旧 loop 还在运行。

---

## 当前已经完成的工作

下面按拆分主线分组列出。

---

## 一、Core 运行时与协议基础已完成

### 配置快照与 Core 无数据库运行基础

已完成：

- Core 运行时配置快照模型
- 从 Admin 当前数据库构建完整快照
- 稳定配置哈希计算
- Core 内存持有当前生效配置
- `last-good-config.json` 本地恢复

关键文件：

- [CoreRuntimeConfigSnapshot.cs](../src/AITool.Application/CoreRuntime/CoreRuntimeConfigSnapshot.cs)
- [ICoreRuntimeConfigProvider.cs](../src/AITool.Application/CoreRuntime/ICoreRuntimeConfigProvider.cs)
- [CoreRuntimeConfigProvider.cs](../src/AITool.Infrastructure/CoreRuntime/CoreRuntimeConfigProvider.cs)
- [CoreRuntimeConfigSnapshotBuilder.cs](../src/AITool.Infrastructure/CoreRuntime/CoreRuntimeConfigSnapshotBuilder.cs)

### Core 全量同步最小闭环

已完成：

- `handshake`
- `full-sync`
- `ready / health / runtime-status`
- 配置版本校验
- 配置哈希校验
- 最小配置完整性校验

### spool / ack / replay 最小可靠闭环

已完成：

- 事件进入内存总线
- 后台 spool 到本地 JSONL
- `ack`
- `replay`
- backlog 判断

### 事件模型与真实事件发布

已完成：

- `usage-log`
- `conversation-turn`

两条真实事件发布链路接入。

---

## 二、Admin 真实消费最小闭环已完成

### UsageLog 真实消费入库

已完成：

- Admin 最小客户端
- `replay` 读取
- UsageLog 事件解析
- 写入 Admin 数据库 `ProxyUsageLogs`
- 最小幂等去重
- 返回最大 sequence 供 ack

这是目前最完整打通的一条 Admin 事件消费链路。

---

## 三、独立 `AITool.Admin` 宿主骨架已完成

已完成：

- `AITool.Admin` 工程
- `AITool.Admin.IntegrationTests` 工程
- 单独构建
- 单独启动
- 独立 smoke test

关键文件：

- [AITool.Admin.csproj](../src/AITool.Admin/AITool.Admin.csproj)
- [Program.cs](../src/AITool.Admin/Program.cs)
- [AITool.Admin.IntegrationTests.csproj](../tests/AITool.Admin.IntegrationTests/AITool.Admin.IntegrationTests.csproj)

---

## 四、`/Admin/UsageLogs` 已完成第一块真实页面迁移，并已持续收口多轮

这是当前 Admin 页面迁移里推进得最深的一块。

### 已完成的部分

- 页面路由迁入 `AITool.Admin`
- 列表 API
- 汇总 API
- 请求详情 API
- 页面 JS 联动
- 分页
- 筛选
- 汇总加载
- 详情抽屉
- 独立宿主测试验证

关键文件：

- [Index.cshtml](../src/AITool.Admin/Pages/Admin/UsageLogs/Index.cshtml)
- [Index.cshtml.cs](../src/AITool.Admin/Pages/Admin/UsageLogs/Index.cshtml.cs)
- [UsageLogsApiController.cs](../src/AITool.Admin/Controllers/Admin/UsageLogsApiController.cs)

### 已补强的展示与交互

已推进：

- 模型列展示更完整链路信息
- 请求模型 / 站点模型展示
- 回退 / 重试 / 最终结果标记
- 详情抽屉补更多尝试级信息
- 成功刷新后自动清理旧错误提示
- 页面新增展示结构测试补强

### 已知状态

- 这块**已经不是骨架状态**
- 但**还没达到“彻底跑通并与真实数据完整联动”的最终目标**
- 仍需继续收口到接近 `AITool.Web` 成熟页面体验

---

## 五、宿主共享边界清理已推进到“门面化分离”阶段

这是最近几轮推进最多的主线。

### 1. `ModelVendorCatalogService` 已收口

已完成：

- 共享实现迁到：
  - [ModelVendorCatalogService.cs](../src/AITool.Infrastructure/Hosting/ModelVendorCatalogService.cs)
- Web 旧实现降为桥接壳：
  - [ModelVendorCatalogService.cs](../src/AITool.Web/Services/ModelVendorCatalogService.cs)
- Web 模型页引用切到共享宿主层
- 补了应用测试：
  - [ModelVendorCatalogServiceTests.cs](../tests/AITool.ApplicationTests/Hosting/ModelVendorCatalogServiceTests.cs)

结论：
- 明确偏 **宿主共享层 / 偏 Admin 管理展示能力**

### 2. `AnalyticsBackgroundQueryExecutor` 已收口

已完成：

- 共享实现迁到：
  - [AnalyticsBackgroundQueryExecutor.cs](../src/AITool.Infrastructure/Hosting/AnalyticsBackgroundQueryExecutor.cs)
- Web 旧实现降为桥接壳：
  - [AnalyticsBackgroundQueryExecutor.cs](../src/AITool.Web/Services/AnalyticsBackgroundQueryExecutor.cs)
- 系统设置页与 Analytics 控制器引用已切换
- 相关测试已通过

结论：
- 明确偏 **宿主共享层 / 偏 Admin 统计分析后台能力**

### 3. `ProxyRequestMetadataCache` 已完成五轮结构收口

这是当前最复杂的一条边界清理线。

#### 已完成的轮次

##### 第一轮
- 显式标出：
  - Core 运行时元数据入口
  - Admin 查询元数据入口
  - 共享失效入口

##### 第二轮
- Admin 查询方法拆到：
  - [ProxyRequestMetadataCache.AdminQueries.cs](../src/AITool.Web/Services/ProxyRequestMetadataCache.AdminQueries.cs)

##### 第三轮
- 查询结果模型抽到：
  - [ProxyRequestMetadataQueryModels.cs](../src/AITool.Web/Services/ProxyRequestMetadataQueryModels.cs)

##### 第四轮
- 新增后台查询门面：
  - [AdminQueryMetadataService.cs](../src/AITool.Web/Services/AdminQueryMetadataService.cs)
- Chat / RouteRules / Developer Invocations 查询切到门面

##### 第五轮
- 继续扩大门面覆盖范围
- `Developer Invocations` 页面清掉了不再需要的直接 `_metadataCache` 查询依赖

#### 当前效果

- 主文件更聚焦 **运行时路径**
- Admin 查询方法已 partial 拆分
- 查询模型已集中
- 后台查询开始走门面服务
- Admin 对运行时缓存对象的读取依赖明显下降

#### 当前关键文件

- [ProxyRequestMetadataCache.cs](../src/AITool.Web/Services/ProxyRequestMetadataCache.cs)
- [ProxyRequestMetadataCache.AdminQueries.cs](../src/AITool.Web/Services/ProxyRequestMetadataCache.AdminQueries.cs)
- [ProxyRequestMetadataQueryModels.cs](../src/AITool.Web/Services/ProxyRequestMetadataQueryModels.cs)
- [AdminQueryMetadataService.cs](../src/AITool.Web/Services/AdminQueryMetadataService.cs)

### 4. `DeveloperInvocationTraceStore` 已开始做读写分离

已新增：

- [DeveloperInvocationTraceQueryService.cs](../src/AITool.Web/Services/DeveloperInvocationTraceQueryService.cs)

当前状态：

- 代理控制器继续直接写 `DeveloperInvocationTraceStore`
- `Developer Invocations` 页面读取改走只读查询门面

结论：
- 运行时写保持靠近主链路
- 管理页读已经开始脱耦

### 5. `ModelConcurrencyLimiter` 已开始做只读查询分离

已新增：

- [ModelConcurrencyQueryService.cs](../src/AITool.Web/Services/ModelConcurrencyQueryService.cs)

当前状态：

- 获取 / 释放并发许可逻辑不动
- 开发者并发面板读取改走只读查询门面

结论：
- 运行时控制与后台读取开始分离

### 6. 后台写操作缓存失效也已开始门面化

已新增：

- [AdminCacheInvalidationService.cs](../src/AITool.Web/Services/AdminCacheInvalidationService.cs)

当前已切换到该门面的写入口包括：

- [AccessKeysApiController.cs](../src/AITool.Web/Controllers/Admin/AccessKeysApiController.cs)
- [ModelsApiController.cs](../src/AITool.Web/Controllers/Admin/ModelsApiController.cs)
- [SiteCatalogApiController.cs](../src/AITool.Web/Controllers/Admin/SiteCatalogApiController.cs)
- [Settings.cshtml.cs](../src/AITool.Web/Pages/Admin/System/Settings.cshtml.cs)

结论：
- Admin 对运行时缓存对象的直接依赖，已经开始同时从**读**和**写**两个方向下降

---

## 六、最近已通过的验证（交接前可确认）

### `AITool.Web` 相关

最近多轮已实际跑通过的代表性命令包括：

- `dotnet build src/AITool.Web/AITool.Web.csproj`
- `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter "FullyQualifiedName~ProxyMetadataCacheTests|FullyQualifiedName~SystemSettingsCacheTests|FullyQualifiedName~DeveloperInvocationsPageTests|FullyQualifiedName~ChatApiTests|FullyQualifiedName~ClientSimulatorPageTests|FullyQualifiedName~ProxyFallbackFlowTests"`
- `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter "FullyQualifiedName~DeveloperInvocationsPageTests|FullyQualifiedName~ProxyMetadataCacheTests|FullyQualifiedName~SystemSettingsCacheTests|FullyQualifiedName~ChatApiTests|FullyQualifiedName~ProxyFallbackFlowTests"`
- `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter "FullyQualifiedName~SystemSettingsCacheTests|FullyQualifiedName~ProxyMetadataCacheTests|FullyQualifiedName~ProxyFallbackFlowTests|FullyQualifiedName~ClientSimulatorPageTests|FullyQualifiedName~SiteBulkDeleteTests|FullyQualifiedName~AdminAuthTests"`

最近一轮相关结果包括：

- `ProxyMetadataCacheTests` / `SystemSettingsCacheTests` / `DeveloperInvocationsPageTests` / `ChatApiTests` / `ClientSimulatorPageTests` / `ProxyFallbackFlowTests` 合计 **53/53 通过**
- `DeveloperInvocationsPageTests` / `ProxyMetadataCacheTests` / `SystemSettingsCacheTests` / `ChatApiTests` / `ProxyFallbackFlowTests` 合计 **51/51 通过**
- `SystemSettingsCacheTests` / `ProxyMetadataCacheTests` / `ProxyFallbackFlowTests` / `ClientSimulatorPageTests` / `SiteBulkDeleteTests` / `AdminAuthTests` 合计 **35/35 通过**

### `AITool.Admin` 相关

之前已经明确推进到：

- `dotnet build src/AITool.Admin/AITool.Admin.csproj` 通过
- `dotnet test tests/AITool.Admin.IntegrationTests/AITool.Admin.IntegrationTests.csproj` 已推进到 **5/5 通过**

> 注意：最近几轮主要在 `AITool.Web` 侧做宿主边界清理，所以 Admin 宿主测试没有在每一轮都重跑；但上一次已知结果是通过的。

### 已知非阻塞 warning

当前仍能看到的代表性既有 warning：

- `ChatApiTests` 的 `xUnit1031`
- 若干 `AITool.Infrastructure` 里的 nullability warning

这些都不是最近几轮边界清理新引入的问题。

---

## 当前还未完成的全部工作

下面是完整的剩余工作清单，按优先级和拆分逻辑组织。

---

## 一、`/Admin/UsageLogs` 还没有彻底完成

虽然它已经不是骨架，但还没到“彻底跑通并与真实数据完整联动”的终点。

还剩：

- 页面细节继续对齐 `AITool.Web`
- 更多字段展示完整度
- 更丰富的交互
- 更多真实链路场景验证
- 最终收口到可认为已经完全迁完这块

---

## 二、其他 `/Admin/*` 页面与接口基本都还没迁完

这部分仍是大头。

仍待迁的主线包括：

- `/Admin/Chat`
- `/Admin/Conversations`
- `/Admin/Detection`
- `/Admin/DetectionTasks`
- `/Admin/ModelHealth`
- `/Admin/Developer/*`
- `/Admin/Analytics`
- `/Admin/System/*`
- `/Admin/Sites/*`
- `/Admin/Models/*`
- `/Admin/Routes/*`
- `/Admin/AccessKeys/*`

当前只有 `/Admin/UsageLogs` 是推进得最深的一块真实迁移页面。

---

## 三、Admin 侧事件消费闭环远未补齐

现在真正打通最小消费闭环的主要还是：

- `usage-log`

还没补齐的事件消费链路包括：

- `conversation`
- `developer trace`
- `detection result`
- `route fallback`
- `circuit breaker`
- `health probe`

也就是说：

- Core 已具备事件产生能力
- 但 Admin 对这些事件的消费入库与 ack 闭环还没有全部接齐

---

## 四、宿主共享边界清理还没有彻底做完

虽然已经推进很多，但还没真正结束。

### 仍未彻底完成的重点

#### `ProxyRequestMetadataCache`

当前只是推进到了：

- 职责显式分区
- partial 拆分
- 查询模型抽离
- 查询门面出现
- 一部分调用方改走查询门面
- 一部分写入口改走失效门面

还没做到：

- 真正独立成 Admin 查询服务 + Core 运行时缓存服务分层
- 更明确的接口边界
- 更大范围替换遗留 Admin 直接依赖
- 最终物理下沉或迁入 `AITool.Admin`

#### `ModelConcurrencyLimiter`

当前只是：

- 后台只读快照已门面化

还没做到：

- 更完整的后台读取与运行时控制分层
- 后续 Admin 宿主通过 API / 快照读取，而不是进程内对象读取

#### `DeveloperInvocationTraceStore`

当前只是：

- 读写职责开始分离

还没做到：

- 更完整的查询服务边界
- 是否事件化 / 外部化仍未最终定型

---

## 五、`AITool.Core` 物理独立宿主还没真正开始落地

这是整个拆分中最关键但也最靠后的部分。

还剩：

- 新建真正的 `AITool.Core` 宿主工程
- 把 `/v1/*` 从 `AITool.Web` 迁出去
- 把 `/api/core/*` 从 `AITool.Web` 迁出去
- 让 `AITool.Web` 逐步退出核心代理角色

当前只是：

- 协议
- 配置快照
- 事件模型
- 可靠闭环

都已经具备迁出条件，但**物理拆分 هنوز没真正开始**。

---

## 六、patch 增量同步协议还没做

当前配置同步只有：

- `handshake`
- `full-sync`
- `noop / full-sync-required / admin-version-behind`

还没做：

- patch 数据结构
- `baseVersion` 校验
- patch 应用逻辑
- patch 失败自动回退全量同步

---

## 七、Core → Admin 实时事件流还没做

当前事件链路还是：

- publish → bus → spool → replay / ack

还没做：

- 长连接实时推送
- Admin 实时消费
- 实时流与 replay 的衔接

---

## 八、sequence / ack / spool 持久化增强还没做完

当前最小闭环已完成，但还没增强到更稳态：

还剩：

- sequence 元数据持久化
- ack 元数据持久化
- Core 重启恢复 sequence / ack 状态
- 更细的 spool 轮转策略

---

## 九、完整双宿主测试体系还没补齐

虽然现在已经有很多应用测试 / 集成测试，但离“宿主、协议、事件、页面链路全部稳定覆盖”还有距离。

还剩：

- 更多 Admin 宿主页面真实迁移测试
- 更多 Core / Admin 双宿主协同测试
- 更多事件消费 / ack / replay 场景测试
- 更完整的真实页面迁移回归测试

---

## 当前最合适的下一步（给下个会话的明确建议）

如果下一个会话希望无缝接上，**不要重新泛泛分析一遍**，而是建议直接从下面这条主线继续：

### 首选下一步
继续推进：

**扩大 `AdminCacheInvalidationService` 与 `AdminQueryMetadataService` 的覆盖面，继续把 Admin 页面 / 控制器对运行时对象的直接依赖剥离出去。**

### 最优先候选
先查这些地方还剩哪些直接依赖：

- `Pages/Admin/Models/*`
- `Pages/Admin/Sites/*`
- `Controllers/Admin/RouteRulesApiController.cs`
- 其他仍然直接握着 `ProxyRequestMetadataCache` 的后台写入口

### 建议动作顺序

#### 下一轮建议顺序

- 继续找出后台页面 / 控制器里仍直接依赖：
  - `ProxyRequestMetadataCache`
  - `DeveloperInvocationTraceStore`
  - `ModelConcurrencyLimiter`
- 优先把“只读查询”改走门面：
  - `AdminQueryMetadataService`
  - `DeveloperInvocationTraceQueryService`
  - `ModelConcurrencyQueryService`
- 再把“写后失效”改走：
  - `AdminCacheInvalidationService`

#### 暂时不要做的事

- 不要贸然整体迁走 `ProxyRequestMetadataCache`
- 不要贸然整体迁走 `ModelConcurrencyLimiter`
- 不要在还没拆清读写边界前就强行创建 `AITool.Core` 物理宿主

因为这些都直接牵涉 Core 稳定链路。

---

## 最后补充：新会话应如何开工

建议新会话开工时直接这样做：

### 第一步
阅读：

- [core-admin-split-progress.md](core-admin-split-progress.md)
- [core-admin-split-handoff.md](core-admin-split-handoff.md)
- [core-admin-split-design.md](core-admin-split-design.md)
- [core-admin-split-implementation-checklist.md](core-admin-split-implementation-checklist.md)
- [core-admin-split-communication-protocol.md](core-admin-split-communication-protocol.md)

### 第二步
先执行一次：

- `git status --short`

确认工作区是否仍干净。

### 第三步
不要重新讨论总目标，直接从：

**继续扩大 Admin 门面覆盖面，进一步剥离后台页面 / 控制器对运行时对象的直接依赖**

开始做下一轮。

---

## 交接总结一句话

当前双宿主拆分已经从“协议和运行时底座搭好”推进到了“真实 Admin 页面迁移启动 + 宿主共享边界开始门面化分离”的阶段；最值得下一个会话直接接手的主线，不是重新讨论设计，而是继续扩大 **Admin 读写门面服务** 的覆盖面，在不破坏 Core 稳定的前提下，把后台能力一点点从运行时对象上拆下来。
