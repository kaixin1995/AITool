# T14 — Codex 巡检 + 功能总开关

> 状态：已完成 ✅（编译 0 错误；集成测试 177/177 通过）
> 前置依赖：T01～T12（Codex 账号体系已完成）
> 关联总览章节：横切性能原则 P1 / P7

## 实施记录

### 块 1 — SystemRuntimeSettings +5 字段
- 实体加 `CodexFeaturesEnabled`(默认 false)、`CodexInspectionEnabled`(false)、`CodexInspectionIntervalMinutes`(30,下限5)、`CodexQuotaMaxCacheHours`(6)、`CodexAutoDisableThresholdPercent`(95,全局阈值)。
- 同步 DTO(`UpdateSystemRuntimeSettingsRequest`) + Service(`UpdateAsync` clamp + 总开关联动) + PageModel LoadAsync + Settings.cshtml。
- `Settings.cshtml` 中：
  - 开发者功能 card 加「启用 Codex 功能」开关
  - 新增「Codex 巡检」card，包含 4 项：自动巡检、巡检周期、缓存最大小时数、自动禁用阈值
  - 全部说明改为问号 tooltip 风格，不再直接铺大段文字
- `CachedProxyRuntimeSettings` 同步加 5 字段 + `GetRuntimeSettingsAsync` 映射。
- `CodexAccount` 加 `DisabledByFeatureToggle`（区分被总开关禁用 vs 额度/手动禁用）。
- **自动禁用阈值语义调整**：从“账号级配置”改为“系统级全局配置”，对所有 Codex 账号统一生效。账号编辑弹窗已移除该输入。

### 块 2 — 总开关禁用联动
- `_Layout.cshtml`：Codex 导航包进 `@if (runtimeSettings.CodexFeaturesEnabled)`。
- `Codex/Index.cshtml.cs` OnGet：关闭时重定向到系统设置。
- `SystemRuntimeSettingsService`：true→false 调 `ApplyCodexFeatureToggleOffAsync`（禁用所有 Codex Site + 标记 `DisabledByFeatureToggle`）；false→true 调 `ApplyCodexFeatureToggleOnAsync`（仅恢复被总开关禁用的账号）。
- `Settings.OnPost`：保存后失效 runtime + 路由 + 模型缓存。
- `CodexApiController` 加 `[ServiceFilter(typeof(CodexFeatureToggleAttribute))]`（关闭时 API 返回 404）。
- `CodexTokenRefreshService`/`CodexCooldownRecoveryService` 每轮读 `CodexFeaturesEnabled`，关闭时跳过。

### 块 3 — 巡检后端
- 新建 `QuotaCachePolicy`（移植 codex-patrol TryReuseQuota + 新增 TTL 兜底：距上次刷新 ≥ maxCacheHours 强制真实刷新）。
- 新建 `CodexInspectionService`（BackgroundService）：周期循环（读设置 `CodexInspectionIntervalMinutes`）；逐账号判定缓存 vs 真实刷新。
  - **使用检测**：查 `ProxyUsageLogs` 该账号 `LinkedSiteId` 自 `LastQuotaCheckedAt` 后是否有记录（AITool 自身是代理，比 codex-patrol 的 usage-queue 更准）。
  - **自动禁用**：周窗口 used% ≥ `AutoDisableThreshold`(默认95) → 禁用；已禁用(非冷却/非总开关禁用)且周额度恢复 → 启用。
  - 内存状态：`_lastRun`(InspectionRunResult) + `_logs`(200 条环形) + `_nextScheduledAt` + `_running` 重入保护。
  - 注意：singleton 服务，`ICodexQuotaService` 从 scope 解析（避免从 root 解析 scoped AppDbContext）。
- `CodexQuotaWindow` 加 `ResetAtUtc` + `LimitWindowSeconds`；Parser/QuotaService/ToSummary 同步填充。
- 巡检 API：`POST inspection/run?force`、`GET inspection/status`、`GET inspection/last-run`、`GET inspection/logs`。
- Program.cs 注册 `CodexInspectionService`(singleton) + hosted。

### 块 4 — 前端 Tab UI
- Codex 页改为 Bootstrap nav-tabs：「账号额度」(原卡片网格不变) + 「巡检」。
- 巡检 Tab：手动巡检/真实巡检/刷新按钮；巡检状态卡(运行中/调度/上次完成)；上次结果汇总卡；账号明细表(账号/动作/5h/周/缓存/原因)；运行输出(操作日志)。
- 切到巡检 Tab 启动 5s 轮询，切回额度 Tab 停止。

### 验证
- 编译 0 错误（-t:Compile 绕过 VS 文件锁）。
- 集成测试 177/177 通过（修复了 singleton 解析 scoped 服务的启动错误）。
- 关闭总开关 → 导航/页面/API 隐藏，所有 Codex Site 禁用（路由/对话测试不命中）；重开仅恢复被总开关禁用的账号。

## 目标

移植 codex-patrol 的「巡检」(inspection) 能力，并加 Codex 功能总开关：
1. 巡检设置存放在 `Admin/System/Settings`（新增板块）。
2. 额度刷新策略：被使用过的账号才真实刷新，否则用缓存；但每隔指定小时强制真实刷新一次（codex-patrol 缺失的兜底，本任务补上）。
3. Codex 功能 + 巡检可被总开关禁用；禁用后 Codex 页面不显示，所有 Codex 托管 Site/模型显示禁用状态。
4. 巡检在 Codex 独立页面内用 Tab 分开（额度 / 巡检）。

## 关键架构差异（vs codex-patrol）

- codex-patrol 走 CPA 中转 + 内存 ConcurrentDictionary + 纯静态前端。
- AITool 直接持有 token + SqlSugar DB + Razor Pages。
- **使用检测简化**：codex-patrol 用 `UsageQueueMonitor` 轮询 CPA usage-queue；AITool 自身就是代理，`ProxyUsageLogs`（`TargetSiteId` 带索引）已记录每条调用 → 巡检时直接查「该账号隐藏 Site 自上次额度刷新后是否有新日志」即可，更准更简单。
- **补 TTL 兜底**：codex-patrol 无「每隔 N 小时强制刷新」；本任务新增 `CodexQuotaMaxCacheHours`。

## 实施记录

（逐块完成后填写）

### 块 1 — SystemRuntimeSettings +4 字段
（待）

### 块 2 — 总开关禁用联动
（待）

### 块 3 — 巡检后端
（待）

### 块 4 — 前端 Tab
（待）
