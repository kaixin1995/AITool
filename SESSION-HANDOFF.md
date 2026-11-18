# AI-Tool 项目交接文档（SESSION-HANDOFF）

> 用途：新会话/新窗口快速接手。README 讲「项目是什么」，本文件讲「当前进行到哪 + 协作纪律 + 事故教训」。接手后先用 git 核实下方状态。

## 当前状态与接手要点

> 稳定项目知识见 README.md；函数级细节见 docs/。本文件含易变状态，接手后先用 git 核实。

### 当前状态快照（写于 2026-11-05，接手后先用 git 核实）

- **2026-11-05 历史重写**：feature 分支 156 笔未同步提交聚合为 **16 笔主题提交**并快进同步进 `master`（`b3078bc`），已推送 GitHub；远端旧 feature 分支已删；旧 156 笔完整历史保留在本地 `backup/pre-squash-route-entry-20261105`（顶端 3f38f58）。**每个聚合提交 body 末尾有「原提交」哈希映射**；本文件后文小节引用的 bec3c17/e8ad10b/25cc83d 等旧哈希一律以 backup 分支为准
- 旧哈希 → 新哈希对照（常用）：bec3c17→`985068b`、6d39f85/71bde58→`d7c8fd4`、b89c6aa/7048087→`44bf805`、2de903b→`b0d11bb`、e8ad10b→并入 `50a9bcf`、25cc83d→`c308701`、UI 批次→`404c891`
- feature 与 master 同指 `b3078bc`（feature 已 unset 上游）；此后新工作可直接在 master 或新分支进行
- 真实实例部署 **1.0.1.48**（2026-11-04 16:02 +0800 构建）＝含 985068b/d7c8fd4/44bf805/b0d11bb/50a9bcf 那批；**桥接 [DONE] 终止符 c308701 + 后续 1fef352/b3078bc 待用户部署**
- 工作区有 2 个未提交文件（`frontend/src/views/OAuthView.vue` + `src/AITool.Web/AITool.Web.csproj` = OAuth 卡片 UI 迭代 WIP，用户目测中）——提交前逐个确认归属，绝不盲提交
- 测试基线：ApplicationTests 393+ / IntegrationTests 1800+ / 前端 vitest 227+，全绿才提交

### 分支与部署模型

- **开发分支**：`feature/route-entry-references`（主干功能持续演进）；`master` 为稳定基线。用户会在会话间切换分支，**开工前先 `git log`/`git status` 核对当前分支与未提交文件**
- **双宿主架构**：`master` 与 `split-core-admin-architecture` 双轨（Web + 桌面端），同步模式 = 整文件替换 + 逐文件审计 split 专有功能是否丢失；两轨同步状态需定期核对
- **提交纪律**：功能完整验证全绿才提交；中文 conventional commit（`修复/功能/优化/测试(scope)：主题` + 详尽 body）；**绝不未经用户指令 push**
- **部署**：用户自行部署到真实实例（版本号见 `src/AITool.Web/AITool.Web.csproj`，逐版本 +1）；本地提交 ≠ 生效，需用户重新部署

### 近期新增子系统速查（2026-10 ~ 11，代码地图）

| 子系统 | 关键文件 | 要点 |
|---|---|---|
| 账号级网络策略 | `Domain/Operations/AccountNetworkPolicy.cs`、`Web/Services/AccountNetworkPolicyStore.cs`、`ForwardNetworkPolicyResolver.cs` | 三态 `NetworkMode`：0=默认（跟随站点/系统）、1=直连、2=代理方案（fail-closed）；UI 文案禁用实现术语；`capabilities` 接口 = `PipelineReady && DeveloperProxyProfilesEnabled`，代理池开关关闭时 OAuth 页网络 UI **全链路隐藏** |
| 会话隔离 | `Web/Services/SessionIsolationKeyStore.cs`、`SessionIsolationDeriver.cs`、`AccountSessionIsolationService.cs` | 密钥 base64 **裸存 SQLite**（Version=2，v1 启动原地升级）；丢失→**自动轮换自愈**并清 `UpstreamReferenceOwners`（绝不阻塞启动）；派生协议版本与存储版本解耦（`DerivationProtocolVersion=1` 固定 HMAC 首字段）。测试夹具含隔离两表必须同建 `UpstreamReferenceOwners` |
| 引用守卫 | `Web/Services/UpstreamReferenceGuard.cs`、`UpstreamReferenceOwnerStore.cs` | Responses 协议 `previous_response_id`/conversation/item 所有权校验（SHA256 digest），防跨账号串会话；每个 SSE 块写给客户端**之前**提交守卫；守卫拒绝 → 合成失败 outcome 照常收口（见遥测契约） |
| 号池 | `Web/Services/AccountPoolSyncService.cs`、`RouteAttemptTracker.cs` | 同组账号打包成对外路由入口（按模型名生成）；路由引用整体展开候选组；`RouteAttemptTracker` 请求内去重（仅组内启用） |
| 设备 OAuth 垂直 | `Infrastructure/OAuth/*OAuthVendor.cs`（注册制 `IDeviceOAuthVendor`） | 内置 Codex/Google(Antigravity)/Kimi/xAI(Grok) + 设备码系 WorkBuddy/ZCode/Trae/Qoder/Copilot 等；托管站点头启动自动回填（`SyncManagedSiteHeadersAsync`），升级无需重新扫码 |
| WorkBuddy 渠道校验 | `Domain/Sites/WorkBuddyClientConstants.cs`（版本单源）、`Infrastructure/Proxy/WorkBuddyChatAdapter.cs` | **11128 是内容审查不是请求头问题**：读 system/assistant 消息中官方客户端提示词整行 + 要求首消息 system。适配器改写请求体（指纹行替换 + 拼写修正 + 补 system），由内部标记 `X-AITool-Rewrite-Channel: workbuddy` 驱动（不透传）；WorkBuddy 预设与托管 ChatSiteConfig 均自动携带 `X-AITool-Force-Stream`（11101 强制流式），手动/直连站点选预设即生效，站点/模型/映射 ExtraHeaders 显式 false 可关闭 |
| 客户端指纹预设 | `Infrastructure/Proxy/ClientEmulationEngine.cs`、`HeaderProfileCatalogService.cs`、`client-header-profiles.json` | 内置 8 预设（OpenCode/ClaudeCode/CodexCli/CodexVsCode/ZCode/Antigravity/Kimi/WorkBuddy）；`${guid}` 等占位符全路径求值；新预设启动自动补齐到既有部署；「AI 查最新版」= `ClientReleaseFeedService` 官方源 + AI 归纳 |
| 调用追踪 | `Web/Services/DeveloperInvocationTraceStore.cs` | 内存环形 40 条/20 分钟；生命周期 `pending → success/fail/canceled`，**谁不调 Complete 谁泄漏**（等待卡死） |

### 遥测与取消契约（两次事故教训，勿回退）

- `ProxyUsageLog` **每次尝试一行**（`AttemptIndex`/`IsFinalResult`/`FallbackTriggered`）；`Total = Input(不含缓存) + Cached + Output`
- **客户端取消也必须写行**（2026-11-04 e8ad10b）：`IsCanceled` 早退曾导致整行缺失，CLI 客户端（DeepSeek Harness 等"收到完成事件即断开"）把上游已计费的 token 全部逃逸出统计——症状是"消耗巨大但 usage-logs 只有零头、列表全是成功"。现契约：canceled 行照常落库；流完成事件已收到后断开 → 翻转为 success 入账（`StreamForwardOutcome.UpstreamCompleted`）；中途断开 token=0 是流协议盲区（usage 只随完成事件下发）
- **守卫拒绝也必须收口**（bec3c17）：`UpstreamReferenceException` 曾穿透收口段 → 追踪永久卡「等待」；现三路径（Responses HTTP SSE / WS turn / Chat Completions）合成失败 outcome 走正常收口
- **钥匙圈事故教训**（2026-11-03）：会话隔离密钥原设计"DP 密文库内 + Data Protection 钥匙圈文件"双层锁，实例重部署丢钥匙圈目录 → 崩溃重启循环。根治 = 密钥裸存数据库（v2）+ 解不开自动轮换自愈；教训：库里 OAuth token 本就明文，对非敏感数据套双层锁是安全剧场，唯一实际效果是制造"不跟库走"的依赖
- 修复类任务的**测试锁定性实证**：`git stash push -- <修复源文件>`（保留测试）→ 新回归测试必须红且错误=原始症状 → `stash pop` → 绿。防止假绿
- **可用性优先**（用户裁决）：任何"持久化状态解不开→拒绝启动"的设计都是错的；能再生的状态自动轮换+清派生+响亮 Warning，只有真不可再生（代理凭证密文）才隔离/显式报错

### 协作纪律（用户确立，违反会挨骂）

- **修复类任务连环反审至零发现才停**：每轮修复后立即独立反审上一轮修复，有发现→修→再审
- 沟通用中文；用户截图驱动 UI 迭代，样式反馈立即落实
- UI 文案**用户视角**：禁 Legacy/Direct 等实现术语；标签一个词、解释放悬浮/tooltip
- 功能开关必须**全链路隐藏**（设置关了，衍生入口/列/提示全藏，后端 capabilities 联动）
- 每轮汇报未提交文件数，提交决策权在用户
- 改动前先自查"最近所有提交是否触碰该域"（不限于本次会话）——实例部署面 = 分支至某提交，用 `GET /api/auth/status`（免认证）的 version/buildTime 对表

### 真实测试环境（只读纪律）

`http://192.168.3.8:5029/`（LAN 另一台机，密码见记忆 `reference-real-test-env-5029`）：`POST /api/auth/login {password}` 取 Bearer；**登录/列表/fetch-models 预览等只读操作可做；删除/导入/批量/设置/凭证刷新未经用户同意不做**；临时 token 文件用完即删。实例上的用量/追踪数据可用于交叉验证（如上游配额窗口 vs 本地统计）。
