# 前端工程（frontend/）

> 本文是 [README.md](../README.md) 的前端细节篇。管理后台是 Vue 3 SPA，构建产物输出到 `src/AITool.Web/wwwroot`，与后端同进程同源部署（无 CORS）。

---

## 1. 工程配置

**package.json**：`vue` 3.5、`naive-ui` 2.40、`pinia` 2.3、`vue-router` 4.5、`axios` 1.7、`echarts` 5.5、`marked` 12 + `xss` + `highlight.js`（聊天 Markdown 渲染与防护）。

scripts：`dev`（Vite 5173）、`build`（`vue-tsc --noEmit` 类型检查 + 构建）、`build:only`、`preview`、`test`（vitest run）、`type-check`。

**vite.config.ts**：
- dev proxy：`/api`、`/v1`、`/health` 三前缀转发到 `VITE_API_TARGET`（默认 `http://127.0.0.1:15029`），`changeOrigin: true`
- `base: '/'`（多级路由下绝对资产路径，防白屏）
- `define` 注入 `__APP_VERSION__`
- build：`outDir: '../src/AITool.Web/wwwroot'`、`emptyOutDir: true`、无 sourcemap

## 2. src 结构

```
src/
├── main.ts / App.vue           # 入口；NConfigProvider(主题/中文) + Message 桥
├── api/                        # 20 个 API 模块（http.ts 为 axios 封装核心）+ 5 个 .test.ts
├── views/                      # 25 个视图组件 + 13 个伴随 *State.ts 纯函数模块（另有 usageSource.ts 等辅助模块）
├── layouts/MainLayout.vue      # 侧边栏 + 顶栏 + RouterView
├── router/index.ts             # 路由表 + 守卫
├── stores/auth.ts              # 唯一 Pinia store
├── composables/                # useTheme / useEcharts / useFormat / useVersion
├── components/                 # JsonDiffView / JsonTreeView / PageHeader / SourceIcon
├── utils/jsonDiff.ts           # JSON 递归 diff（协议诊断字段对比用）
├── types/api.ts                # ApiResponse / TokenPair / AuthStatus
└── styles/main.css             # Tailwind 入口 + 亮/暗 CSS 变量 + Naive UI 覆写
```

## 3. 路由与导航

路由懒加载，`createWebHistory('./')`。守卫逻辑：公开页放行 → 拉取 auth status → 未设密码强制 `/login`（setup 模式）→ 未登录跳登录（returnUrl）→ 功能开关（`requiresOAuth`/`requiresDeveloper` 不满足重定向 dashboard）。

**侧边栏分区**（`MainLayout.vue` navGroups，按 features 动态过滤）：

```
概览       仪表盘(/) · 可视化分析(/analytics) · 对话(/chat)
资源管理   站点管理(/sites) · [OAuth 管理(/oauth)] · 模型库(/models)
代理配置   路由管理(/routes) · 访问密钥(/access-keys)
监控运维   模型检测(/detection) · 检测任务(/detection-tasks) · 模型健康(/model-health)
           · [调试工具(/developer/invocations)] · 使用日志(/usage-logs) · 系统设置(/system/settings)
```

- `[OAuth 管理]` 仅 `oauthEnabled`；`[调试工具]` 仅 `developerEnabled`
- **侧边栏左下角 footer**：`AI Tool v{version}` + `Build {编译时间}`（数据来自 `/api/auth/status` 的 version/buildTime，`composables/useVersion.ts`，回退 `__APP_VERSION__`；折叠时隐藏）
- 旧地址兼容重定向：`/Admin/ClientSimulator`、`/Admin/Developer/Invocations`、`/Admin/ModelHealth`、`/Admin/Analytics`、`/route-fallback`（→ model-health?tab=fallback）、`/compatibility`（→ routes?tab=compatibility）

## 4. 页面功能明细

| 视图 | 功能要点 |
|------|----------|
| **DashboardView** | 统计卡（可点击跳转）+ 快捷操作（`?action=create` 直开新建弹窗） |
| **AnalyticsView**（1629 行） | 时间/粒度/协议/模型/来源/站点/密钥多维筛选；趋势图（请求/成败/Tokens/缓存命中/耗时）、分布图（站点/模型）；细分 Tabs（来源/密钥/协议/失败原因/状态码/回退链路/延迟分位数）；图表条目点击反向下钻；Pending/429 按 retryAfterMs 轮询。状态逻辑在 `analyticsState.ts`，格式化在 `analyticsFormat.ts` |
| **ChatView + ChatTestPane** | 模型/目标选择、思考开关+等级（low/medium/high/xhigh/max）、流式/停止/清空；SSE 命名事件解析（`parseSseBlock`）；meta 事件可展开每段尝试（站点/状态/耗时/token/请求响应体） |
| **SitesView**（1215 行） | 站点 CRUD/批量删除/启停；**站点密钥管理弹窗**（多 Key 增删/行内编辑/启停/优先级上移下移，交换 priority 失败回滚，请求序号防竞态）；**导入导出**（JSON/TSV 粘贴解析预览、勾选导入、导出含完整 keys）；**远端模型拉取**（单站 + 全站异步 taskId 轮询 1.2s、别名编辑、勾选导入）。解析逻辑在 `sitesState.ts` |
| **OAuthView**（动态额度窗口，requiresOAuth） | **两个页签**（URL `?tab=inspection` 同步）：「账号额度」— 账号卡片（状态 tag/planType/Token 过期 <3 天预警/额度窗口进度条 <20% error <50% warning/重置信用入口）、OAuth 登录弹窗、凭证上传/导出、编辑（改名/换 refresh_token/刷新 access_token）、拉取模型导入；「额度巡检」— 状态卡、上次结果（保留/禁用/启用/缓存命中/真实刷新计数 + 每账号动态窗口明细表）、巡检日志，10s 轮询 + requestId 防旧响应 |
| **ModelsView**（1379 行） | 两页签：「模型分组」按厂商分组卡片（SVG 图标 + 渐变背景，`vendorCatalogState.ts` 处理 lobe-icons id 冲突）；「厂商规则」目录编辑（matchType exact/wildcard/regex + pattern + priority，脏检查）。编辑弹窗两页签：basic（名称/显示名/启停/OverrideReasoningEffort/兼容规则集绑定）+ mappings（站点映射增删/启停/调并发） |
| **RoutesView**（路由管理） | 左：路由入口列表；右：**候选实例队列**（搜索/NSelect 添加/拖拽排序/移除；每行显示站点名 + 上游模型（优先显示**对外名**）+ 实例名；**改动即自动保存**，保存期间再变更排队）。**时间规则**：行内「时间规则」按钮展开 popover，编辑写入草稿，**点「确定」才保存**（取消/收起丢弃）；模式（全天/仅指定时间可用/指定时间不可用）+ HH:mm 区间。纯函数在 `routeEditorState.ts` |
| **CompatibilityView**（兼容规则集） | 规则集列表 + 编辑器：规则行 op（strip/rename/default/**keep_reasoning**）+ scope（all/passthrough/bridge）。序列化在 `compatibilityState.ts` |
| **AccessKeysView** | 列表/新建（路由入口多选，加载失败禁用创建防误配「允许全部」）/明文一次性展示与按需复制/启停/删除/编辑路由权限 |
| **DetectionView** | 检测矩阵 + 单点探测/按模型/全量（taskId 轮询退避重试 `shouldRetryDetectionProgress`） |
| **DetectionTasksView** | 任务创建（Cron 默认 `*/30 * * * *`、「全部模型」归一 null）/启停/立即执行/删除/执行历史 |
| **ModelHealthManagementView** | 两页签：「模型健康」监控表（成功率色阶 + 每站点展开时间线条）；「路由回退」回退事件表（样本近 5000 条说明、源站点→目标站点、原因筛选、5s 自动刷新） |
| **DeveloperInvocationsView**（requiresDeveloper） | **六个页签**，详见 [debug-tools.md](debug-tools.md)：调用调试 / 客户端模拟 / 当前模型并发数检测 / 熔断监控 / 协议诊断 / SQL 迁移（hash 深链 `#developerInvocationsPane` 等） |
| **UsageLogsView**（944 行） | 可折叠筛选（时间/站点/密钥/**来源**/状态/模型模糊 300ms 防抖）+ 4 汇总卡；表格：来源（SourceIcon：claude-code/codex/open-code/deepseek-harness 内联 SVG、zcode PNG，未知文字）、**模型列 `路由入口名 -> 对外模型`**（chat 来源只显示对外模型）、状态（成功/失败/回退后成功/流中断）、用时/首字/流 chip、输入/缓存/输出/总 token、「查看链路」抽屉（RequestId 全部尝试）；**5s 增量刷新**（按 id+JSON 签名 diff 只替换变化行）；自制分页条。来源选项统一在 `views/usageSource.ts`（含 DeepSeek Harness） |
| **SystemSettingsView** | 分组卡：检测/代理（含并发策略与排队超时）/日志/开发者功能/账号额度巡检；数字项 Tooltip + 保存前整数校验（`systemSettingsState.ts`）；危险操作：按来源/时间清空日志 |
| **LoginView** | 单密码登录/首次设置双模式（isSetupMode），returnUrl 回跳 |

## 5. stores / composables / api 层

**stores/auth.ts**（唯一 Pinia store）：`accessToken`（localStorage）、`status`（hasPassword/features{oauthEnabled, oauthInspectionEnabled, developerEnabled}/version/buildTime）、actions：`fetchStatus`/`login`/`setup`/`logout`/`isAuthenticated`。

**composables**：

| 文件 | 作用 |
|------|------|
| `useTheme.ts` | 亮/暗双主题（localStorage `aitool.theme`，默认跟随系统）、`data-theme` 属性、Naive UI darkTheme + 完整 GlobalThemeOverrides（主色 #6C9EFF、圆角 12px） |
| `useEcharts.ts` | ECharts 按需注册（Line/Pie/Bar + Canvas，省 ~800KB）、`initChart`、`darkChartOverrides()` |
| `useFormat.ts` | `formatCompact`（1.2K/3.4M）、`formatDuration`（350ms/1.2s） |
| `useVersion.ts` | version/buildTime/buildTimeDisplay（auth store 优先，回退 `__APP_VERSION__`） |

**api/http.ts**（axios 封装核心）：30s 超时；请求拦截注入 Bearer；响应拦截严格识别 `ApiResponse` 四键（success/data/message/errorCode）并解包，业务失败抛 `ApiError(message, errorCode, status)`；**401 自动刷新 token**（refreshPromise 并发保护，refresh 走裸 axios 防递归）+ 重试一次 + 失败清 token 跳登录；`skipErrorNotify` 配置自处理错误；全局消息回调由 `App.vue` 的 MessageBridge 注入。导出 `httpGet/httpPost/httpPut/httpDelete`（自动 unwrap `.data`）。

**api 模块**（基路径即控制器路由）：`auth`、`accessKeys`、`analytics`、`chat`（含 `sendChatStream` fetch SSE + `parseSseBlock`）、`circuitBreaker`、`codex`、`compatibility`、`dashboard`、`detection`、`detectionTasks`、`developer`、`modelHealth`、`models`、`protocolDiagnostics`、`routeFallback`、`routes`、`sites`、`sqlMigrations`、`system`、`usageLogs`。函数与端点一一对应（见 [admin-api.md](admin-api.md)）。

## 6. UI 规范与约定

- **Naive UI + Tailwind 共存**：Tailwind **关闭 preflight**（防 reset 覆盖组件库样式），content 仅扫 `src`
- **主题**：CSS 变量双套（`:root` 与 `[data-theme='dark']`：--bg-page/--bg-card/--border-color-global/--status-* 等），页面统一消费变量；顶栏 🌙/☀️ 切换
- **布局**：侧边栏 260px 可折叠 72px（localStorage `aitool.sidebarCollapsed`）；移动端抽屉 + 遮罩 + Esc；`PageHeader` 组件统一页头
- **视图层工程约定**：复杂页面把可测逻辑抽同目录 `*State.ts` 纯函数（15 个）+ vitest 单测（约 20 个 `*.test.ts`）；列表页普遍采用「5s 轮询 + document.visibilityState 守护」「请求序号/AbortController 防竞态」
- **组件**：`JsonDiffView`（协议诊断字段 diff，上限 800 行）、`JsonTreeView`（折叠树，单节点子项上限 100）、`SourceIcon`（来源品牌图标）

## 7. 构建与联调

```bash
cd frontend
npm install
npm run dev        # http://localhost:5173，/api /v1 /health 代理到 15029
npm run build      # vue-tsc 类型检查 + vite build → src/AITool.Web/wwwroot
npm run test       # vitest
npm run type-check # 仅类型检查
```

后端端口非 15029 时：`VITE_API_TARGET=http://127.0.0.1:<端口> npm run dev`。
