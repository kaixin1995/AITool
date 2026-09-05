# Web API 内存优化计划

> 目标：从 ~500MB 降至 ~150-200MB，不破坏任何现有功能。
> 原则：每项优化必须可独立回滚，验证通过后再进下一项。

---

## 〇、实施记录（2026-09-04，未提交待验收）

全部 6 项已实施完毕，两处按核实后的事实修正了计划：

| # | 结果 | 说明 |
|---|---|---|
| 1 | ✅ 按计划 | Hangfire 全量移除；`LogRetentionPruneService`（每小时对表，本地 03:00 后当天首次触发；从 `LastUsageLogPrunedAt` 恢复当日标记防重启重复清理） |
| 2 | ✅ 按计划+补前端 | 后端 `FormatBody` 直存原始报文；**计划遗漏**：详情页 `<pre>` 面板原样渲染字符串，故前端 `bodyText` 增加 `prettyJsonText`（展示时解析缩进），显示效果不变 |
| 3 | ⚠️ 部分修正 | body/轮次响应默认 4/2→1/1MB 已做；**60→20 取消**：`_recentDumps` 实为元数据列表（无 body，60 条仅 ~10KB），降低上限只缩 UI 列表（页面请求 50 条）不省内存；另注：`MaxBodyLengthMb` 同时约束**落盘文件**内容，>1MB 的 dump 正文会被截断（需完整正文时在页面临时调高，范围 1-50 不变） |
| 4 | ✅ 按计划 | LRU 上限 16；被淘汰客户端进入 10 分钟宽限期后销毁（覆盖在途流式请求） |
| 5 | ✅ 按计划 | TTL 30s→300s；已复核全部写路径（Admin 控制器 + 15 个后台服务）均有显式 Invalidate，检测探针写的 `LastStatus` 字段缓存不读 |
| 6 | ⚠️ 单位修正 | 不用字节而按**条目数**限容（64 条 + 既有 20s TTL）：看板结果是聚合 DTO，条数封顶即可约束总量，且零测量开销；用**专用** MemoryCache 实例（共享实例设 SizeLimit 会让所有不带 Size 的 Set 抛异常） |

附带修复：`HeaderProfileCatalogService._fileLock` 实例级→**静态**。集成测试并行工厂共享同一
`client-header-profiles.json`，实例锁不互斥导致 Windows 文件锁 IOException（移除 Hangfire 后 host 启动加速使该既有竞态显性化，修复前复现率 ~2/8，修复后 6/6 全绿）。

二次复核补充（同日）：
- **桌面端调用追踪详情**同样直渲染 trace body，已补 `PrettyTraceBodies` 展示层缩进（Desktop 构建 0 错）；
  Chat 页与桌面模拟器经核实不经 `FormatBody`，不受影响。
- 新增 `LogRetentionPruneLogicTests`（5 用例：到点触发/未到窗口/当日去重/次日再触发/停机补做），
  ApplicationTests 214→220；并修复调度标记取"清理完成时刻"在跨午夜时会把次日错标为已清理的边界。
- 诊断默认值（4/2MB）无任何测试断言，收紧无冲突。

三次复核（找茬轮）修复的三个边界：
- **透传误报"已转换"**：网关透传时 `ReplaceOpenAiModelAndEnsureStreamUsage` 仍会压缩重序列化——客户端
  发带缩进 JSON 且模型名未被改写时，旧后端规范化存储使原文比较成立，改存原始报文后会误报。前端
  `hasConvertedRequestBody/Response` 改为**语义比较**（解析后规范化对比，256KB 上限回退原文比较），
  vitest 116→118。
- **超大报文展示卡顿**：Web/桌面端展示层格式化加 1MB 上限，超限原样显示（避免渲染路径反复 parse 多 MB JSON）。
- **长流式 vs 退役宽限期**：代理客户端退役销毁宽限期 10→30 分钟（持续输出的流式响应可远超请求级超时，
  空闲超时只在无数据时触发）。
- 源配置（appsettings/nlog）无 Hangfire 残留；bin/obj 旧产物中的引用在下次发布时自然消失。

验证：ApplicationTests 220 ✓；IntegrationTests 366 ✓×12；前端 vue-tsc+build ✓、vitest 118 ✓；
Debug/Release（含 Desktop）构建 0 错 0 警；真实启动冒烟（/health 200、未登录 admin 401、日志零异常）✓。

---

## 一、服务器实测与追加优化（2026-09-04 下午）

线上诊断（192.168.3.8，旧构建）：RSS 419→460MB 持续爬升，而**托管堆三次采样均 ~62MB 持平**——
增长全部来自非托管侧：12+ 个 glibc 线程 arena（~200MB+）+ Server GC 已提交不还的段。
据此追加五项：

| # | 优化 | 说明 |
|---|---|---|
| A | **glibc arena 限制**（`GlibcArenaLimiter`） | 代码内 `mallopt(M_ARENA_MAX, n)`（环境变量法进程内无效）；`appsettings NativeMemory:MallocArenaMax` 默认 2、0=关闭。服务器实测 mallopt 返回 1，8 线程压测 arena 15→7 |
| B | **Workstation GC** | csproj `ServerGarbageCollection=false`；实测该进程 CPU <1%（Server GC 吞吐优势需 8 核+/持续数百 MB/s 分配率），2 核机上只剩多吃内存的缺点 |
| C | **Trace body 捕获截断** | `CapBody` 128K 字符/报文（≈256KB 内存），封顶最后一个无界内存项（最坏 ~60MB）；完整报文有诊断 dump 落盘兜底 |
| D | **NLog** `keepFileOpen=true, concurrentWrites=false` | 免去每条日志的文件开关 |
| E | 连接池寿命对齐 + Desktop 序列化配置静态化 | 主 handler `PooledConnectionLifetime` 2min→15min；`IndentedJsonOptions` 静态复用 |

验证：构建 0 错 0 警（Debug/Release/Desktop）、runtimeconfig 确认 `System.GC.Server: false`、
220 + 366 测试全绿。

**待办：提交并发布新构建后，用 `ps -o rss` 观察半天；若仍偏高，下一步杠杆是 glibc
`MALLOC_TRIM_THRESHOLD` 调优或整机扩内存（4GB 已用 1.7GB swap）。**

---

## 二、配置化内存治理（2026-09-04 傍晚，已部署线上）

线上实测 369MB 的逐块拆解：glibc malloc ~113MB（arena 10 块 73MB + 主 heap 39MB）、共享运行时/系统库
~135MB（5 个 dotnet 进程分摊，PSS 324 vs RSS 369）、GC 已提交段 ~80MB（活对象仅 31MB）、JIT/栈 ~52MB、
应用 dll ~10MB。结论：两条棘轮（arena 滞留 + GC 段水位）由分配速率驱动。

按"配置随程序走、不依赖环境变量"的原则落地三件事并已部署：
- `GlibcArenaLimiter` 扩展为三旋钮（appsettings `NativeMemory` 节，启动即代码生效）：
  `MallocArenaMax=2` + `MallocTrimThresholdBytes=64K`（free 归还更积极）+
  `MallocMmapThresholdBytes=128K`（静态值禁用动态上调，大缓冲走 mmap 即用即还——治理 arena 棘轮最有效项）。
- **GC 堆硬上限 256MB**：SDK 不透传 MSBuild 属性，经 `runtimeconfig.template.json` 烘焙进构建产物
  （与 Workstation GC 同在 runtimeconfig.json，换机器零依赖）。活对象 31-62MB 的 4-8 倍余量。
- 部署后面板 stop/start 正常，health 200，RSS 启动 197MB（待观察长尾曲线）。

调试功能侧的评估结论（同日）：插件式改造对内存无效（可卸载部分仅 ~10MB，大头是进程级棘轮）；
`DeveloperFeaturesEnabled=1` 当前开启，其每请求采集推高分配速率——细粒度子开关为待办可选项。

---

## 一、优化项总览（按收益/风险比排序）

| # | 优化项 | 预估节省 | 风险 | 阶段 |
|---|---|---|---|---|
| 1 | 移除 Hangfire（只剩 1 个定时任务在用） | 50-80MB | 极低 | 一 |
| 2 | DeveloperInvocationTraceStore 去格式化 | 20-40MB | 极低 | 一 |
| 3 | ProxyDiagnosticService 内存上限收紧 | 30-100MB | 低 | 一 |
| 4 | _proxyClients 加 LRU 上限 | 10-50MB | 低 | 二 |
| 5 | ProxyRequestMetadataCache TTL 延长 | 20-40MB | 低 | 二 |
| 6 | AnalyticsBackgroundQueryExecutor 缓存限制 | 5-20MB | 极低 | 二 |

---

## 二、逐项详细方案

### 优化 1：移除 Hangfire（预估节省 50-80MB）

**现状**：Hangfire 注册了 InMemory 存储 + 默认 worker 数（= CPU 核数），但现在只剩一个 `log-retention-prune` 每日定时任务在用（检测调度已迁到自研 BackgroundService）。Hangfire 的 worker 线程栈、内存存储、仪表盘中间件全部常驻。

**改动**：
- 删除 `AddHangfire()` / `AddHangfireServer()` / `UseHangfireDashboard()` / `RecurringJob.AddOrUpdate`
- `log-retention-prune`（每日 3:00 清理过期日志）改到一个极简 BackgroundService 里：
  ```csharp
  // 每小时检查一次，到 3:00-4:00 窗口且当天未执行过则触发
  // 逻辑与原 RecurringJob 完全一致：调 ILogRetentionService.PruneAsync()
  ```
- 删除 `Hangfire.AspNetCore` / `Hangfire.InMemory` NuGet 包
- 删除 `HangfireDashboardAuthFilter`
- 前端无 Hangfire 仪表盘入口（确认无引用）

**为什么安全**：
- 唯一的定时任务语义完全等价替换（每日 3 点 → 每日 3 点）
- Hangfire 仪表盘只有本地访问才用到，移除后不影响 API 功能
- 检测任务调度已在前一轮改造中脱离 Hangfire

**验证**：
- 全量测试通过
- 手动确认日志清理在 3 点后正常执行（或手动调用 ILogRetentionService 验证）
- 确认前端无 /hangfire 路由引用

---

### 优化 2：DeveloperInvocationTraceStore 去格式化序列化（预估节省 20-40MB）

**现状**：`FormatBody()` 对每个请求/响应体做 `JsonSerializer.Serialize(document, WriteIndented)` —— 格式化缩进序列化把 body 膨胀 2-3 倍。40 条 trace × 3 个 body × 膨胀 2.5 倍 ≈ 20-40MB 额外开销。

**改动**：
- `FormatBody()` 直接返回原始 body 字符串（去掉 `JsonDocument.Parse` + `WriteIndented` 重序列化）
- 前端 `JsonTreeView` / `JsonDiffView` 组件已有 `JSON.stringify(obj, null, 2)` 格式化展示能力，原始数据到前端再格式化

**为什么安全**：
- 存储的数据内容完全一致（只是缩进格式不同）
- 前端展示层已有格式化能力（`JsonTreeView.vue` 内部用 `JSON.parse` + `stringify` 缩进展示）
- 调试页的 diff 对比基于 JSON 结构而非缩进格式

**验证**：
- 前端开发者调试页正常展示请求/响应详情
- JSON diff 对比功能正常

---

### 优化 3：ProxyDiagnosticService 内存上限收紧（预估节省 30-100MB）

**现状**：
- `MaxRecentDumpsInMemory = 60`（内存中最多保留 60 条 dump 摘要）
- `MaxBodyLengthMb = 4`（单条 dump 的请求/响应体最大 4MB）
- 最坏情况：60 × 多个 body × 4MB = 数百 MB

**改动**：
- `MaxRecentDumpsInMemory`: 60 → **20**（够用：调试页默认显示 50 条，翻页场景极少）
- `MaxBodyLengthMb`: 4 → **1**（够用：绝大多数协议调试 body < 100KB）
- `MaxRoundResponseMb`: 2 → **1**（AI 自愈调试的响应体限制同步收紧）

**为什么安全**：
- 文件 dump（落盘复现文件）不受影响，只是内存中的摘要列表变小
- 超过 1MB 的 body 仍会完整落盘到 JSON 文件，只是内存快照截断
- 20 条内存摘要仍满足"最近失败 + 成功采样对比"的调试需求

**验证**：
- 诊断抓包页正常显示最近 dump
- 失败请求自动落盘功能正常
- AI 自愈调试闭环正常

---

### 优化 4：_proxyClients 加 LRU 上限（预估节省 10-50MB）

**现状**：`ConcurrentDictionary<string, HttpClient> _proxyClients` 无上限。每个唯一代理 URL 创建一套独立 `SocketsHttpHandler` + 连接池，永不清理。

**改动**：
- 加上限 `MaxProxyClients = 16`
- 超过时淘汰最久未使用的（用 `ConcurrentDictionary<string, (HttpClient Client, DateTime LastUsed)>` + 定期清理）
- 清理时安全 Dispose HttpClient（停止新请求，等现有连接完成）

**为什么安全**：
- 被清理的代理会在下次使用时通过 `GetOrAdd` 自动重建
- 实际配置超过 16 个出口代理的场景极少
- 连接池生命周期缩短不影响正确性（只是首次请求稍慢）

**验证**：
- 配置代理的站点转发正常
- 代理池满后新代理仍能创建（最旧的被清理）

---

### 优化 5：ProxyRequestMetadataCache TTL 延长（预估节省 20-40MB）

**现状**：`CacheDuration = TimeSpan.FromSeconds(30)`。20 个缓存键每 30 秒全表重建（含新增的 GoogleAccounts、ProxyProfiles、HeaderProfiles 加载），制造大量短期对象 → LOH 碎片化 → 工作集居高不下。

**改动**：
- `CacheDuration`: 30 秒 → **300 秒（5 分钟）**
- 前提确认：所有写路径（增删改站点/模型/路由/密钥/设置等）**已有显式 Invalidate 调用**——实际配置变更即时生效，TTL 只是兜底

**为什么安全**：
- **功能性零影响**：配置变更走显式失效（已逐文件确认所有写路径都有 Invalidate 调用），TTL 从不承担"让变更生效"的职责
- 唯一变化：如果某处遗漏了显式失效（理论上不应该），变更最多延迟 5 分钟而非 30 秒
- 内存收益：全表加载频率从每 30 秒 1 次降为每 5 分钟 1 次，对象图制造量减少 90%

**前置验证（必须做）**：
```
grep -rn "Invalidate" src/AITool.Web/Controllers/Admin/ --include=*.cs | wc -l
# 确认每个写操作（POST/PUT/DELETE）都调用了对应的 Invalidate
```

**验证**：
- 修改站点配置后立即请求，确认生效（不等待 5 分钟）
- 修改系统设置后代理行为立即变化
- 30 秒内无异常全表加载（通过日志确认）

---

### 优化 6：AnalyticsBackgroundQueryExecutor 缓存限制（预估节省 5-20MB）

**现状**：分析页查询结果缓存在 `IMemoryCache`，无大小限制。月度/全量分析可能返回大数据集。

**改动**：
- 给缓存条目加 `Size` 属性（字节数），`IMemoryCache` 设全局大小上限（如 50MB）
- 超限时自动淘汰最旧条目

**为什么安全**：
- 缓存 miss 只导致重新查询（稍慢），结果不变
- 当前 20 秒 TTL 很短，大小限制只是额外保险

**验证**：
- 分析页各维度查询正常
- 并发查询时无异常

---

## 三、明确不动的部分（避免破坏功能）

| 组件 | 不动原因 |
|---|---|
| 11 个 BackgroundService | 合并/删减会引入复杂时序风险，单项开销小（<5MB each），收益不值得 |
| MemoryMaintenanceService | 已经在做 LOH 压缩，是正向的 |
| ProxyRequestMetadataCache 全表加载模式 | 是有意的设计选择（简单可靠），只调 TTL 不改加载方式 |
| 检测任务 5 秒轮询 | DB 查询极轻（索引查询），不是内存瓶颈 |
| Google/Kimi OAuth 服务结构 | 各自的 ConcurrentDictionary 都很小（Guid → 时间戳/Token），不是问题 |

---

## 四、实施顺序与验证流程

```
阶段一（低风险，预期节省 100-220MB）
  ├── 优化1: 移除 Hangfire
  ├── 优化2: FormatBody 去格式化
  └── 优化3: 诊断内存上限收紧
      → 全量测试 + 前端功能验证 + 启动运行 10 分钟观察内存

阶段二（低风险，预期节省 35-110MB）
  ├── 优化4: 代理连接池 LRU
  ├── 优化5: 缓存 TTL 延长
  └── 优化6: 分析缓存限制
      → 全量测试 + 显式失效路径逐项验证 + 运行 30 分钟观察内存

最终验证
  ├── 后端全量测试 ≥ 580 绿
  ├── 前端 vue-tsc 0 错 + vitest 116 绿
  ├── Debug/Release 构建 0 错误
  └── 部署到测试环境运行 1 小时，内存稳定在目标范围
```

---

## 五、回滚方案

每项优化独立提交。如果某项引起问题：
```bash
git revert <该优化项的提交>
```
不影响其他优化项。
