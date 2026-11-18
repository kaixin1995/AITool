# OAuth 账号托管与额度巡检

> 本文是 [README.md](../README.md) 的 OAuth 账号细节篇。当前内置 Codex 提供程序，把 chatgpt.com 的 Codex（OpenAI Responses 协议上游）以「OAuth 账号 → 隐藏站点」的方式接入网关，复用现有的路由/模型/对话测试/故障转移/并发控制全链路；额度巡检编排已抽象为通用账号提供程序，可扩展其它 OAuth 账号类型。
> 开发过程上下文见 `docs/codex-development-context.md`；任务拆解归档在 `docs/codex-tasks/`。

---

## 1. 核心机制：账号 ⇆ 隐藏站点

每个 `CodexAccount`（表 `CodexAccounts`）创建时由 `CodexAccountProvisioner.ProvisionFromTokensAsync`（`src/AITool.Web/Services/CodexAccountProvisioner.cs:49`）自动创建一个隐藏 `Site`（`ManagedSource="Codex"`、Responses 协议、`ExtraHeadersJson` 存 Originator/Chatgpt-Account-Id/User-Agent 特殊头、access_token 同步写 `Site.ApiKey`），账号与站点通过 `CodexAccount.LinkedSiteId` 关联。隐藏站点不出现在站点管理页，以 Responses 协议接入转发链路；删除账号时级联清理（`DeprovisionAsync`）。

**禁用状态矩阵**（恢复逻辑各自独立，避免误启用）：

| 字段 | 含义 | 恢复方式 |
|------|------|----------|
| `DisabledByFeatureToggle` | 因 OAuth 账号总开关关闭被禁用（记录原 IsEnabled） | 重开 `OAuthFeaturesEnabled` 时**仅**恢复此类 |
| `ManuallyDisabled` | 用户手动禁用 | 仅手动启用；巡检恢复跳过 |
| `IsQuotaCooling` / `QuotaCoolingUntil` | 命中上游额度限制被动冷却 | `CodexCooldownRecoveryService` 到期自动恢复（跳过手动禁用） |
| `IsEnabled=false`（其他） | 巡检额度耗尽自动禁用 | 手动或额度恢复 |

功能开关（`SystemRuntimeSettings`，`SystemRuntimeSettingsService.UpdateAsync` 联动）：
- `OAuthFeaturesEnabled` 总开关：false 时禁用全部已注册 OAuth 提供程序的托管站点与账号；控制器层 OAuth 功能过滤器整体 404；前端隐藏入口
- `OAuthInspectionEnabled` 巡检开关：账号巡检过滤器对巡检 action 级 404
- `OAuthInspectionIntervalSeconds`（下限 30s）、`OAuthQuotaMaxCacheHours`（默认 6）、`OAuthAutoDisableThresholdPercent`（默认 95）、`OAuthInspectionCacheEnabled`

---

## 2. OAuth / 凭证链路

**`CodexOAuthClient`**（`Infrastructure/Codex/CodexOAuthClient.cs`，实现 `ICodexOAuthClient`，Typed HttpClient 20s）：
- 端点：auth.openai.com 授权/`token`；ClientID `app_EMoamEEZ73f0CkXaXp7hrann`；回调 `http://localhost:1455/auth/callback`
- `CreateOAuthSession()`：state=32 随机字节、verifier=96 随机字节（base64url）
- `BuildAuthorizeUrlAsync`：S256 challenge，scope `openid email profile offline_access`，prompt=login、codex_cli_simplified_flow=true
- `ExchangeCodeAsync(code, verifier)`：authorization_code 换 token
- `RefreshTokenAsync(refreshToken)`：**single-flight**（同一 refresh_token 用 SemaphoreSlim 串行，空闲清理防 token 轮换泄漏）

**登录流程（前端）**：规范 API 为 `POST /api/admin/oauth/start-oauth` → 用户浏览器登录 → 粘贴回调 URL → `POST /api/admin/oauth/complete-oauth` 交换并建账号；旧 `/api/admin/codex` 仅作为兼容别名。也可上传 CPA 格式凭证 JSON（`import-credential`，多文件 multipart）——解析在 **`CodexCredentialParser.Parse`**（`Infrastructure/Codex/CodexCredentialParser.cs`）：type=codex 校验（缺失宽松推断）、JWT claims 权威回退顶层字段、过期时间优先 `expired` 再 JWT exp；`ParseMany` 单文件失败不中断。JWT 解析在 **`CodexJwtParser.Parse`**（`CodexJwtParser.cs`）：无验签解析 id_token → email/chatgpt_account_id/chatgpt_plan_type/订阅窗口（取自 `https://api.openai.com/auth` claim），按 token 内容缓存。去重身份键是 `AccountId`（chatgpt_account_id）+ `Email` 复合——chatgpt_account_id 标识 workspace 而非用户：同一邮箱可分属 plus/team 多个 workspace（同邮箱不同 account_id 共存），同一 team workspace 的多个成员（不同邮箱）又共享同一 account_id（同 account_id 不同邮箱同样共存），任一维度单键折叠都会互相覆盖（对齐 CPA 凭证文件名键 hash8(account_id)+email）。

**凭证导出**：`POST /api/admin/oauth/accounts/export-credentials` 勾选多账号导出 OAuth 凭证 JSON；管理端生成的文件名使用 `oauth_credential_` / `oauth_credentials_` 前缀。

## 3. Token 生命周期

| 场景 | 服务 | 行为 |
|------|------|------|
| 周期预防性刷新 | `CodexTokenRefreshService`（HostedService） | 每 5 分钟扫描；**到期前 1 天**即刷新 access_token，写回隐藏 `Site.ApiKey` 并失效路由缓存；同一账号 30 分钟内不重复刷新（防短有效期 token 刷新风暴，已过期账号不受限）；403 退避 1 小时 |
| 代理命中 401 实时刷新 | `CodexCredentialRefreshService.RefreshAsync`（Scoped） | 作为 `ProxyForwardRequest.RefreshTargetApiKeyAsync` 回调注入转发链（`CreateCodexCredentialRefreshCallback`，仅 `ManagedSource=="Codex"`）：401 → 刷 OAuth → 同步隐藏站点 → 重发一次 |
| 手动刷新 | `POST /accounts/{id}/refresh-token` | 同机制 |

## 4. 模型目录

- **静态目录 `CodexModelCatalog`**（Singleton，实现 `ICodexModelCatalog`）：进程内分层快照（pro/plus/team/free 各层 + builtin 图片模型 gpt-image-1.5/2），`GetModelsForPlan(planType)` 按 plan 选层去重缓存
- **动态拉取 `CodexModelFetcher.FetchAsync`**（Typed 30s）：GET `chatgpt.com/backend-api/codex/models?client_version=...`，带头 Bearer/Originator: codex_cli_rs/UA `codex_cli_rs/{CodexUpstream.ClientVersion}`（默认 0.133.0）/Chatgpt-Account-Id；兼容数组/`models`/`data` 包装。拉取同样通过**映射反查模型库**（自定义名作为对外路由名 ModelName 而非仅显示名）
- 导入：`GET /accounts/{id}/fetch-models` → 前端搜索/全选/别名编辑 → `POST /import-selected-models`

## 5. 额度（quota）

- **主动查询 `CodexQuotaService.QueryAsync`**（Typed 20s）：调 `wham/usage`，30s 结果缓存防抖 + single-flight；`POST /accounts/{id}/refresh-quota` 手动触发
- **解析 `CodexUsageParser.Parse`**：按窗口秒数 18000/604800 区分 5 小时/周窗口（含代码审查与 additional 限额），Window 含 usedPercent/重置剩余/UTC 重置时间；`limit_reached`/`allowed=false` 兜底 100%
- **DTO `CodexUsagePayload`**：snake_case/camelCase 双命名兼容
- **被动冷却 `CodexQuotaCooldownService.TryApplyCooldownFromErrorAsync`**：转发链命中 `usage_limit_reached` 时标记 `IsQuotaCooling`+`QuotaCoolingUntil` 并禁用隐藏站点；`ResetAsync` 重置。**冷却恢复 `CodexCooldownRecoveryService`**（HostedService）周期恢复到期账号
- **重置 credits `CodexResetCreditsService`**（Typed 30s）：`QueryResetCreditsAsync` 查询剩余次数/过期时间；`consume-reset-credit` 消耗一张执行真实重置

## 6. 巡检（Inspection）

**`AccountQuotaInspectionService`**（Singleton + HostedService，`src/AITool.Web/Services/AccountQuotaInspectionService.cs`）：每 10 秒检查任务，启用账号按 `OAuthInspectionIntervalSeconds` 执行常规巡检；额度自动禁用账号使用独立的持久化恢复日程。通过 `IAccountQuotaProvider` 枚举账号、查询额度和同步启停状态，新增 OAuth 提供程序无需复制巡检编排。
- **随机恢复复查**：每个额度自动禁用账号独立随机等待 **40–60 分钟**，绕过持久化额度快照与 provider 结果缓存；无论成功、失败、token 过期或需要重新授权，本次尝试结束后仍重新随机计时，不会每 10 秒重试。手动、功能开关、上游禁用及额度冷却中的账号不参与；自动任务仍受 OAuth 总开关与自动巡检开关控制。
- **持久化与并发**：`QuotaRecheckSchedules` 仅保存 provider/account ID、UTC 时间及认领令牌，不保存凭证。重启沿用已抽取的时间；历史已逾期或无检查时间的账号首次按 0–60 秒错峰补查。数据库原子认领避免多个巡检实例重复执行；崩溃后有效认领最多保留 10 分钟，正常恢复工作使用 2 分钟取消预算。
- **缓存复用 `QuotaCachePolicy.TryReuseQuota`**：仅用于常规巡检。`OAuthInspectionCacheEnabled` 开启且账号未被使用（`SiteUsageTracker`）、窗口未过期且未超 `OAuthQuotaMaxCacheHours` 时沿用上次快照；随机恢复复查不受缓存设置或常规周期影响。
- 按账号级/全局 `OAuthAutoDisableThresholdPercent` 阈值自动禁用额度耗尽账号；恢复沿用滞回规则：用量必须严格低于 `max(1, threshold - 8)`（默认阈值 95% 时低于 87%）。写库时再次核对当前禁用标志和总开关，并在同一事务内更新账号与关联站点，避免覆盖查询期间的手动停用。
- 只有成功取得可用额度窗口才判断恢复；仅返回占位信息、无法主动查询额度的厂商不能据此自动回启。确认恢复后仅解除该站点的额度耗尽路由休息，不清除独立限流休息或普通故障熔断。
- `RunManualAsync(force)`（force=忽略缓存真实刷新，可提前领取尚未到期的恢复任务，但不会抢占有效认领）/`GetLastRun`/`GetLogs`/`GetStatus`
- 手动接口：`POST /api/admin/oauth/inspection/run?force=`；结果页见 OAuth 管理页「额度巡检」页签（保留/禁用/启用/缓存命中/真实刷新计数 + 每账号动态额度窗口/动作/原因表）。当前 Codex 提供程序返回 5 小时和周窗口，其他提供程序可以返回日、月或自定义窗口。

## 7. 代理链路中的 Codex 特判

- `ProxyProtocolBridge.IsCodexTarget`（chatgpt.com/backend-api）→ `NormalizeResponsesBody` 剔除 `CodexUnsupportedParameters` 12 字段 + 强制 `stream=true` + `store=false`
- 非流式请求上游返回 SSE → `ProxyForwardService.ForwardAsync` 内 `TryExtractResponsesCompletion` 透明聚合（output 为空时用 delta 文本重建 message）
- 隐藏站点 `ExtraHeadersJson` 经 `MergeExtraHeaders` 注入转发头；`RefreshTargetApiKeyAsync` 注入 401 刷凭证回调
- 模型健康探测（`ModelHealthRequestService`）对 Codex 目标同样剔除不支持字段并写 UsageLog（Source 按调用入口）

## 8. 前端页面（OAuthView，requiresOAuth）

两个页签（URL `?tab=inspection` 同步）：
- **账号额度**：账号卡片网格（状态 tag 正常/已禁用/冷却中、planType、Token 过期 <1 天标红、额度窗口进度条 <20% error / <50% warning、重置信用入口）；右上角 OAuth 登录弹窗 / 凭证上传（失败明细）/ 导出凭证模式；卡片操作：刷新额度/启停/编辑（改名+换 refresh_token+刷新 access_token）/拉取模型导入/删除
- **额度巡检**：状态卡（巡检中/空闲、上次完成、下次计划、手动巡检/真实巡检 force）、上次结果卡、巡检日志；未开启（404）显示空态；10s 静默轮询 + requestId 防旧响应覆盖（`accountInspectionState.ts`），结果窗口按提供程序动态渲染
