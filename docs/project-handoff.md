# 项目接手与协作约束

本文保留跨会话共享的契约和故障教训；具体分支、HEAD、未提交文件、部署版本、测试数量及个人环境状态会变化，接手时应重新核实，不作为仓库事实固化。个人交接快照可留在已忽略的根目录 `SESSION-HANDOFF.md`，不应放入凭据。

## 开工、验证与部署

- 先用 `git status`、`git log` 核对当前分支、工作区及最近触及相关领域的提交；可能有其他会话的改动，逐项确认归属，勿盲目提交。沟通使用中文，每轮汇报未提交文件数，提交决策权在用户；未经用户指令不 push。
- 功能完整并通过适用的构建与测试后才提交；提交使用中文 conventional commit（如 `修复(scope)：主题`，正文说明原因和验证）。修复后做独立反审，有发现则修复并再次反审，直至无发现。
- 回归测试应锁定原始症状：在不影响他人改动的隔离工作区中验证“去掉修复、保留测试”时因原问题失败，再恢复修复验证通过；不要对共享工作区盲目 stash。
- UI 文案从用户视角出发，避免 Legacy、Direct 等实现术语；简短标签的解释放在提示中。功能开关关闭时，入口、列、提示及后端 capabilities 应全链路一致隐藏。
- 本地提交不等于真实实例已生效，真实实例由用户部署。版本号见 `src/AITool.Web/AITool.Web.csproj`，发布逐版本递增；可用免认证的 `GET /api/auth/status` 返回的 version/buildTime 与构建版本对照。构建与发布步骤见 [tools.md](tools.md)。涉及 `master` 与 `split-core-admin-architecture` 双轨同步时，先核对当前分支与同步状态；整文件替换后逐文件审计 split 专有功能，避免丢失。
- 对真实测试环境遵守只读纪律：登录、列表和 fetch-models 预览等只读操作可做；删除、导入、批量操作、设置变更及凭证刷新须先获用户同意。临时 token 文件用完即删，不将地址、密码或 token 写入共享文档。

## 遥测、取消与引用守卫契约

- `ProxyUsageLog` 按尝试记录（`AttemptIndex`、`IsFinalResult`、`FallbackTriggered`）；`Total = Input + Cached + Output`，其中 Input 不含缓存。客户端取消也要记录尝试，不得因 `IsCanceled` 直接跳过落库；取消不触发路由回退。
- 流完成事件已收到后客户端断开，按 success 入账（`StreamForwardOutcome.UpstreamCompleted`）。过去客户端收到完成事件即断开时，取消早退导致已计费用量整行缺失；中途断开若上游尚未发送 usage，token 为零是协议可观测性限制，不代表没有消耗。更多口径见 [proxy-pipeline.md](proxy-pipeline.md) 和 [testing.md](testing.md)。
- 流式输出前须先提交引用归属；守卫拒绝时，Responses HTTP SSE、WebSocket turn 和 Chat Completions 的 `UpstreamReferenceException` 必须进入失败收口，完成追踪与用量记录，不能留下永久「等待」。追踪的创建方负责完成生命周期（`pending → success/fail/canceled`）。引用安全和取消时序边界见 [account-network-isolation.md](account-network-isolation.md)。

## 会话密钥故障教训

曾因会话隔离密钥依赖数据库内密文及外部 Data Protection 钥匙圈，重部署丢失钥匙圈后发生重启循环。现会话密钥 v2 随数据库保存；旧密钥无法读取或状态损坏时可事务性轮换并清除引用归属，发出警告而非因可再生状态拒绝服务。轮换改变派生标识，旧引用可能无法续接；数据库不可写、事务失败等仍可能导致失败。不要把这一自愈机制推广为所有密钥都可丢失：代理凭据及未升级的 v1 会话密钥仍依赖匹配的钥匙圈，应按 [备份与恢复说明](account-network-isolation.md) 一起备份和检查。

## 子系统入口

- 账号网络策略、会话隔离及引用归属：[account-network-isolation.md](account-network-isolation.md)；号池及路由引用：[route-references.md](route-references.md)。
- 代理链路、客户端指纹和用量：[proxy-pipeline.md](proxy-pipeline.md)；调用追踪：[debug-tools.md](debug-tools.md)；OAuth 账号：[codex.md](codex.md)。
- WorkBuddy 的 11128 是内容审查而非单纯请求头问题：客户端提示词及首条 system 消息由 `WorkBuddyChatAdapter` 适配；强制流式标记与预设行为应连同显式覆盖一并验证。具体实现以当前代码为准。
