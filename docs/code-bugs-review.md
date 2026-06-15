# 程序问题清单（bug 审查）

> 审查日期：2026-06-15
> 审查方法：4 个并行子任务分别深入代理转发链路、配置同步与事件、并发与资源、数据一致性与认证，关键 bug 均经行号抽样验证。
>
> **注意**：本文档只列**真实存在的程序 bug**（会导致错误行为/崩溃/数据丢失/资源泄漏），不含性能优化点。优先级按影响排序。

---

## 实施记录（2026-06-15 修复）

以下 bug 已全部修复并通过测试验证（ApplicationTests 190 / Core 集成 143 / Admin 集成 54，共 387 全绿，0 失败）。

| # | 优先级 | bug | 修复方式 |
|---|--------|-----|---------|
| 回归 | — | CoreEventSpoolBackgroundService 剪枝循环 | 整批只检查一次 + AddEventsSinceLastPrune |
| 1 | 🔴 | DetectionApiController._progressStore 内存泄漏 | ProbeModel/ProbeAll 发起时懒清理已完成任务 |
| 2 | 🔴 | ProxyForwardService HttpResponseMessage 无 using | 两处 SendAsync 改 using var |
| 3 | 🔴 | RouteCircuitStateStore 熔断参数可见性 | _blockDuration 改 long ticks + Volatile.Read/Write |
| 4 | 🔴 | ProxyUsageLogBatchWriter 停止无 Drain | 仿 ConversationLogBatchWriter 加 DrainRemainingEntriesAsync |
| 5 | 🔴 | RouteRulesApiController SiteId 校验 | SaveRules 前校验所有 SiteId 存在 |
| 6 | 🟠 | AdminCacheInvalidationService 状态码判断 | 改用 ex.StatusCode == HttpStatusCode.BadRequest |
| 7 | 🟠 | Anthropic 流式中断熔断 | HasStartedStreaming 时不触发 SafeBlockRoute |
| 8 | 🟠 | AttemptIndex 被跳过路由错自增 | attemptIndex++ 移到 eligibility 检查后 |
| 9 | 🟠 | SchemaMigrator 多语句 DDL | 拆为逐条执行 + 独立 try-catch |
| 10 | 🟠 | AdminAuthService 非恒定比较 | 改用 CryptographicOperations.FixedTimeEquals |
| 11 | 🟠 | ModelConcurrencyLimiter._states 内存泄漏 | ListRecent 中清理空闲 state |
| 14 | 🟡 | FileConversationLogStore 未 Dispose | 实现 IDisposable |
| 15 | 🟡 | WebSocket UTF-8 跨帧解码损坏 | 改用 MemoryStream 累积后整体解码 |

### 保留待后续处理（影响小或改动面大/风险高）

| # | 优先级 | bug | 保留原因 |
|---|--------|-----|---------|
| 12 | 🟡 | RouteCircuitStateStore.Block 与 IsBlocked 竞态 | 影响仅监控误报，修复需重构互斥逻辑，回归风险高 |
| 13 | 🟡 | ConfigVersion 快速重启场景 | 概率低，修复需统一版本号生成入口，涉及全量/增量两条链路 |
| 16 | 🟡 | CoreAdminEventBus DropOldest 丢事件无补救 | 极端积压才触发，修复需引入 dead-letter 机制 |
| 17 | 🟡 | CoreEventPullService ack 推进含失败事件 | 需改 ingestor 返回值契约，改动面较大 |

---

## 已在本轮修复

### ✅ CoreEventSpoolBackgroundService 剪枝循环（本次修复，我引入的回归）

- **文件**：`src/AITool.Infrastructure/CoreRuntime/CoreEventSpoolBackgroundService.cs:90`
- **问题**：我在 P1-1 批量写改动中，用 `for (var i = 0; i < batch.Count; i++)` 对每条事件调 `ShouldPrune()`，而 `ShouldPrune` 每次自增 `_eventsSinceLastPrune`。一批 64 条且达阈值时会连续触发最多 64 次剪枝扫描，造成磁盘 IO 风暴，反推 Channel 积压触发 DropOldest 丢事件。
- **修复**：整批只检查一次，新增 `AddEventsSinceLastPrune(batch.Count)` 按批累加，`ShouldPrune` 不再自增。

---

## 🔴 严重（建议尽快修复）

### 1. DetectionApiController._progressStore 静态字典永不清理（内存泄漏）

- **文件**：`src/AITool.Admin/Controllers/Admin/DetectionApiController.cs:85`
- **问题**：`static readonly ConcurrentDictionary<string, ProbeProgress> _progressStore` 在 `ProbeModel`（:140）和 `ProbeAll`（:209）每次探测写入，`GetProgress`（:265）只读不清，任务完成后（`IsCompleted=true`）没有任何清理逻辑（无 TTL、无后台清理、无 TryRemove）。
- **影响**：长时间运行下，每个探测任务的 `AllResults`（含全部映射探测明细）永远驻留进程内存，泄漏持续累积。用户反复点"全部检测"会放大。
- **修复建议**：`GetProgress` 检测到 `IsCompleted` 时 `TryRemove`；或改用带过期时间的 `IMemoryCache`。

### 2. ProxyForwardService 的 HttpResponseMessage 未 Dispose（连接泄漏）

- **文件**：`src/AITool.Infrastructure/Proxy/ProxyForwardService.cs:58`
- **问题**：第 58 行 `var response = await _httpClient.SendAsync(...)` **没有 using**（对比同文件第 57 行的 `httpRequest` 有 using）。`HttpResponseMessage` 不 Dispose 时，底层 HTTP 连接不能及时归还连接池。
- **影响**：高并发代理请求下连接池耗尽，表现为请求卡住或 SocketException。对"电脑配置有限"的环境尤其敏感。
- **修复建议**：改为 `using var response = await _httpClient.SendAsync(...)`。

### 3. RouteCircuitStateStore 熔断参数读取无 volatile（可见性问题）

- **文件**：`src/AITool.Infrastructure/Proxy/RouteCircuitStateStore.cs:112-113`
- **问题**：`_blockDuration`（:55）和 `_failThreshold`（:60）在 `UpdateOptions`（:90-97）的 `lock(_syncRoot)` 内写入，但 `Block`（:112-113）读取时**未持锁**且字段无 `volatile`。CLR/JIT 可能缓存读取值，导致动态更新后的熔断参数对新请求长时间不可见。
- **影响**：运行时（系统设置页）修改熔断阈值/恢复时长后，热路径仍用旧值，熔断行为与配置不一致。这在弱内存模型下理论上可能发生，x86 上通常可见但无保证。
- **修复建议**：读取处也进锁；或字段封装为不可变 record 用 `Interlocked.Exchange` 发布；或直接加 `volatile`（值类型字段不能直接 volatile，需用 `Volatile.Read`）。

### 4. ProxyUsageLogBatchWriter 停止时未排空剩余队列（数据丢失）

- **文件**：`src/AITool.Infrastructure/Proxy/ProxyUsageLogBatchWriter.cs:87-135`
- **问题**：`ExecuteAsync` 退出循环后**没有 Drain 逻辑**（对比 `ConversationLogBatchWriter` 有 `DrainRemainingEntriesAsync`）。`stoppingToken` 取消或通道关闭时，`buffer` 中已读出但未刷盘的条目、以及 channel 内剩余条目全部丢失。
- **影响**：Admin 宿主停机/重启时批量丢失代理使用日志。DropWrite 模式本就静默丢数据，停止时再丢一批。
- **修复建议**：仿照 `ConversationLogBatchWriter`，在 `ExecuteAsync` 末尾调用 `DrainRemainingEntriesAsync`。

### 5. RouteRulesApiController 路由规则可写入不存在的 SiteId（数据完整性）

- **文件**：`src/AITool.Admin/Controllers/Admin/RouteRulesApiController.cs:241-289`
- **问题**：`SaveRules` 保存规则时，`ProxyRouteRule.SiteId` 未校验是否存在，`AppDbContext` 也**未配置外键约束**（只有索引无 FK）。`Guid.Empty` 或不存在的 SiteId 会被静默写入。
- **影响**：规则保存了但运行时 `GetRouteTargetsAsync` 通过 join 过滤掉，表现为"规则保存了但路由不生效"，无错误提示，难排查。
- **修复建议**：显式校验 SiteId 存在且拒绝 `Guid.Empty`；或在 AppDbContext 配置 FK 约束。

---

## 🟠 中等（建议排期修复）

### 6. AdminCacheInvalidationService 用字符串匹配 "400" 判断状态码

- **文件**：`src/AITool.Admin/Services/AdminCacheInvalidationService.cs:384-389`
- **问题**：判断 Core 未初始化用 `ex.Message.Contains("400")`。会误匹配任何含 "400" 字样的错误（如端口号 4000、超时堆栈），或漏匹配不含 "400" 的 400 响应。
- **影响**：错误触发全量同步（频繁全量），或 patch 被拒后不回退（Core 永久拿不到配置）。
- **修复建议**：改用 `ex.StatusCode == HttpStatusCode.BadRequest`。

### 7. Anthropic 流式中断补发 message_stop 后仍被当失败熔断

- **文件**：`src/AITool.Core/Controllers/Proxy/AnthropicProxyController.cs:517-540`
- **问题**：`ForwardAnthropicStreamPassthroughAsync` 在流中断时补发 `message_stop` 给客户端，但 `result.Success` 仍为 `false`（:519）。回到 `Messages`（:314）后因 `streamResult.Success==false` 执行 `SafeBlockRoute`（:324）和 `SafeLogFailedProxyAttempt`（:322），把已成功发给客户端的调用当作失败计入熔断器。
- **影响**：上游只是流中途断（客户端已收到终止事件），但路由被错误熔断，长期会导致健康路由被拉黑。
- **修复建议**：客户端已收到终止事件且 `startedWriting==true` 时，视为部分成功，不触发 `SafeBlockRoute`。

### 8. AttemptIndex 在被跳过的路由上错误自增

- **文件**：`src/AITool.Core/Controllers/Proxy/OpenAiProxyController.cs:555` 等四处
- **问题**：`attemptIndex` 在每条路由（含被 `continue` 跳过的熔断/不可用路由）都会自增，但被跳过的路由从未被尝试。失败时 `RetryCount = attemptIndex - 1` 把"未尝试的路由"算成重试，统计偏高。
- **影响**：UsageLog 的 `RetryCount` 与实际重试次数不符，分析/统计偏差。
- **修复建议**：把 `attemptIndex++` 移到实际发起转发的紧前位置（路由检查通过之后）。

### 9. DatabaseSchemaMigrator 多语句 DDL 批处理风险

- **文件**：`src/AITool.Infrastructure/Persistence/DatabaseSchemaMigrator.cs:92-99`
- **问题**：一个 `DbCommand.CommandText` 塞了 4 条 `CREATE INDEX IF NOT EXISTS`。Microsoft.Data.Sqlite 对批处理 DDL 支持有限，任一条失败会使整个命令抛异常被吞掉，后续索引全部跳过。
- **影响**：旧库升级时索引可能未建，查询性能退化。
- **修复建议**：每条 `CREATE INDEX` 单独一个 `DbCommand`/`try-catch`。

### 10. AdminAuthService 用 MD5 + 非恒定时间比较（安全）

- **文件**：`src/AITool.Infrastructure/Hosting/AdminAuthService.cs:60, 100-105`
- **问题**：① 用 MD5（无盐、快）作密码哈希，与 AccessKey 用 SHA256 安全级别不一致；② `string.Equals(..., OrdinalIgnoreCase)` 比较哈希非恒定时间，存在理论 timing 侧信道。
- **影响**：密码库易被彩虹表/暴力破解。
- **修复建议**：改用 PBKDF2/BCrypt，用 `CryptographicOperations.FixedTimeEquals` 比对。

### 11. ModelConcurrencyLimiter._states 无限增长（内存泄漏）

- **文件**：`src/AITool.Infrastructure/Proxy/ModelConcurrencyLimiter.cs:112`
- **问题**：`_states` 以 `SiteId:RemoteModelName` 为 key，**完全没有任何清理路径**，每个曾出现过的 site+model 组合永久驻留。`_activeEntries` 在 `ListRecent` 会清理，但 `_states` 不会。
- **影响**：站点/模型组合数量大或动态变化时内存缓慢增长。
- **修复建议**：在 `ListRecent` 或定时清理中，对 `ActiveCount==0 && Waiters.Count==0` 且长时间未使用的 key `TryRemove`。

---

## 🟡 轻微（可后续处理）

### 12. RouteCircuitStateStore.Block 与 IsBlocked 竞态导致重复触发熔断事件

- **文件**：`src/AITool.Infrastructure/Proxy/RouteCircuitStateStore.cs:106-130`
- **问题**：`Block` 先调 `IsBlocked`（:109）判断，但两者非原子。熔断窗口刚过期时可能重复触发 `OnCircuitOpened` 事件。
- **影响**：熔断事件可能重复发布，造成监控误报。
- **修复建议**：判定"是否首次触发"合并到锁内。

### 13. ConfigVersion 快速重启场景下启动全量同步可能被 Core 忽略

- **文件**：`CoreConfigSyncHostedService.cs:108` vs `AdminCacheInvalidationService.cs:59-65`
- **问题**：启动同步用纯时间戳生成版本，运行时用 `Math.Max(counter, now)`。快速重启时启动版本可能 ≤ 上次 patch 版本，Core 会 `Ignored` 启动 full-sync。
- **影响**：快速重启场景下启动全量同步被忽略（Core 仍持有旧配置）。
- **修复建议**：统一用 `ConfigVersion()` 单一入口生成版本号。

### 14. FileConversationLogStore 的 SemaphoreSlim 未 Dispose

- **文件**：`src/AITool.Infrastructure/Conversations/FileConversationLogStore.cs:20`
- **问题**：类持有 `SemaphoreSlim _storageLock` 但未实现 `IDisposable`，进程关闭时不释放。
- **影响**：轻微资源泄漏（单实例单 Semaphore）。
- **修复建议**：实现 `IDisposable`。

### 15. WebSocket 接收缓冲区 UTF-8 跨帧解码损坏

- **文件**：`src/AITool.Core/Controllers/Proxy/OpenAiProxyController.Helpers.cs:27-42`
- **问题**：固定 16KB buffer + `Encoding.UTF8.GetString` 按固定边界解码，多字节字符跨帧时产生替换符，JSON 损坏。
- **影响**：长对话的 WebSocket 请求（>16KB）JSON 解析失败。
- **修复建议**：用 `Utf8JsonReader` 流式解码，或累积到 MemoryStream 后整体解码。

### 16. CoreAdminEventBus DropOldest 丢事件无补救

- **文件**：`src/AITool.Infrastructure/CoreRuntime/CoreAdminEventBus.cs:66-75`
- **问题**：DropOldest 模式丢弃最旧事件，被丢事件序号已分配但从未入 spool，Admin replay 永远拿不到。
- **影响**：极端积压下事件丢失且不可恢复，与"事件可靠性"目标冲突。
- **修复建议**：改为在丢弃时记录被丢 envelope 的序号到 dead-letter 文件。

### 17. CoreEventPullService ack 推进含部分入库失败的事件

- **文件**：`src/AITool.Admin/Services/CoreEventPullService.cs:109-130`
- **问题**：ingestor 内部若部分入库失败、部分成功（如批量写库时一条违反约束），ingestor 返回 max 但失败条目不会重试，ack 已推进，事件永久丢失。
- **影响**：个别事件静默丢失。
- **修复建议**：ingestor 返回"已成功处理的最大连续序号"而非"已见最大序号"。

---

## ✅ 已验证无问题（排除误报）

- **returnUrl 注入**：`Login.cshtml.cs` 用 `Url.IsLocalUrl` 校验，安全。
- **未认证 API 返回 401**：`AdminAuthenticationMiddleware` 正确。
- **SystemRuntimeSettings Id=1 单例**：`ValueGeneratedNever` + `GetOrCreateAsync` 兜底，可靠。
- **AccessKeyHash 大小写**：`Convert.ToHexString`（大写）+ Ordinal 比较，一致。
- **ModelConcurrencyLimiter 取消路径**：正确移除 waiter，无 double-release。
- **DeveloperInvocationTraceStore 事件触发**：锁外触发，Clone 在锁内拷贝，无死锁。
- **CoreEventSequenceProvider 序号恢复**：`Math.Max(meta, spool)` 正确，Interlocked 用法正确。
- **ack.meta/sequence.meta 原子写**：temp+move 正确。
- **ProxyCallRecorder**：所有外部调用 try/catch，不阻断主链路。
- **缓存失效类别**：Sites/Routes/Keys/Settings/Mappings 七类全覆盖。

---

## 修复建议优先级

1. **立即修**：#1（内存泄漏）、#2（连接泄漏）、#4（数据丢失）—— 这些会在正常运行中持续累积损害。
2. **排期修**：#3（熔断可见性）、#5（数据完整性）、#7（错误熔断）、#8（统计偏差）、#9（索引丢失）、#11（内存泄漏）。
3. **安全加固**：#10（MD5 密码）—— 取决于威胁模型。
4. **择机修**：#6、#12-#17。

建议先处理"立即修"的三项，它们的影响随运行时间放大。其余可按优先级排期。
