# 双宿主增强开发计划（共享密钥 / 可恢复流 / AllInOne / 运维配套）

> 状态：✅ 全部里程碑已完成（M1 安全 / M2 守恒 / M3 流式韧性 / M4 AllInOne / M5 运维），2026-09-05 收敛；
> 执行记录见附录 A/B，部署与运维见 deployment-guide.md，最终回归 291+151+328+118 全绿。
> 分支：split-core-admin-architecture（已与 master 功能对齐，基线 317+143+271+118 全绿）
> 版本基线：1.0.1.22

---

## 0. 总目标与三条铁律

**总目标**：让 split 双宿主架构安全地支持「Core 部署在国外服务器（HTTPS 证书）、Admin 跑在本地」的长期使用形态，同时提供单机/单进程部署形态；并消除跨境 SSE 流中断。

**三条铁律（每个任务验收前必须自检）：**

| # | 铁律 | 守护手段 |
|---|------|----------|
| L1 | **Core 独立运行绝不失效**：Admin 下发过一次配置后，Admin 彻底离线，Core 必须照常完成所有 /v1 代理交互（流式/非流式/协议桥/401 即刷） | T5.1 自动化守恒测试 + 每个工作流验收必跑 |
| L2 | **/v1 线上字节格式永不改变**：所有增强只允许新增响应头/新增可选请求头，响应正文逐字节与现在一致 | 快照级对比测试（现有客户端零感知） |
| L3 | **存量测试永不回退**：317 单元 + 143 Admin 集成 + 271 Core 集成 + 118 前端，任何任务完成后必须全绿 | 每任务验收门 + 最终回归门 |

**显式兼容规则：**
- 鉴权在 Testing 环境全放行（沿用 JWT 的既有模式），存量集成测试不需要改。
- `CoreAuth:SharedSecret` 未配置时，`/api/core/*` 退化为"仅允许本机回环访问"——同机部署开箱即用，独立服务能力不受影响。
- 所有新能力挂配置开关，默认值取"行为最接近现状"的一档。

---

## 1. 范围总览与依赖关系

```
M1 安全与直通保险 ──┐
（WS1 密钥鉴权      ├─→ M3 可恢复流（依赖 M1 的密钥客户端）
 WS2 直通头+心跳）──┘
M2 独立守恒（WS5）──→ 贯穿始终，每阶段末跑
M4 AllInOne（WS4）──→ 独立于 M3，可并行
M5 运维配套（WS6）──→ 依赖 M1（文档里要写密钥配置）
```

| 里程碑 | 内容 | 预估 | 前置 |
|--------|------|------|------|
| M1 | 跨宿主共享密钥鉴权 + SSE 直通头 | 2~3 天 | 无 |
| M2 | Core 独立运行守恒测试 + OAuth 凭据离线持久化 | 2~4 天 | 无（建议紧随 M1） |
| M3 | SSE 心跳 + 可恢复流 + Admin 中继 | 5~8 天 | M1 |
| M4 | AllInOne 单进程宿主 | 4~6 天 | 无强依赖 |
| M5 | 可观测性 / 打包 / 文档 | 2~3 天 | M1 |
| — | 最终回归与实机冒烟 | 1~2 天 | 全部 |

总计约 3.5~5 周单线程。M2/M4 可与 M1/M3 并行穿插。

---

## 2. 工作流 1（WS1）：跨宿主共享密钥鉴权

### 需求
- Core 的全部管理端点 `/api/core/*`（config 同步、事件流/replay/ack、handshake、developer 查询、runtime 状态）要求调用方携带共享密钥。
- `/v1/*` 维持 AccessKey 自校验，**不加**密钥（客户端零改动）；`/health` 保持匿名。
- Admin 侧所有服务端→Core 的调用自动注入密钥；浏览器直连 Core 的调用改为经 Admin 后端代理（密钥不出服务端）。
- 传输加密由部署层 HTTPS 证书承担（已定），密钥只管认证，二者互补。

### 设计要点
- 密钥头：`X-Core-Auth`；比较用 SHA256 后 `CryptographicOperations.FixedTimeEquals` 防时序侧信道。
- Core 配置：`CoreAuth:SharedSecret`（appsettings，空 = 未启用）。
- 未启用时回退策略：`/api/core/*` 仅允许回环地址（IPv4/IPv6 loopback），启动时打 Warn 日志。保证同机部署开箱即用。
- Admin 配置：`CoreServer:SharedSecret`。
- Testing 环境中间件直接放行（沿用 JWT 的 Testing 匿名模式，存量测试零改动）。

### 任务拆解

| ID | 任务 | 验收标准 | 规模 |
|----|------|----------|------|
| T1.1 | 盘点全部 Admin→Core 调用点（CoreConfigSyncHostedService、CoreEventPullHostedService、handshake/status 检查、Chat/Developer 相关），产出清单 | 清单入 docs，无遗漏（grep CoreServer BaseUrl 全覆盖） | S |
| T1.2 | Core：`CoreAuthOptions` + 鉴权中间件（路径作用域 /api/core/*、时序安全比较、Testing 放行、空密钥回环回退、启动告警） | 无密钥 401；错密钥 401；对密钥 200；Testing 全通；回环无密钥 200 | M |
| T1.3 | Core：appsettings 增加 `CoreAuth` 节 + 注释；两个宿主各一份 | 配置可读、缺省回退正确 | S |
| T1.4 | Admin：`CoreServer:SharedSecret` 配置 + 名为 `"CoreClient"` 的 HttpClient + DelegatingHandler 统一注入头；T1.1 清单全部迁移到该客户端 | 抓包/测试证明每个调用点都带 `X-Core-Auth` | M |
| T1.5 | 浏览器直连 Core 的调用改造为经 Admin 后端代理（同源 `/api/admin/core-proxy/{**path}` 转发，服务端注入密钥；SSE 透传）——为 T3.4 中继复用同一套转发内核 | 前端页面（聊天/调试）功能不变；浏览器请求中不出现密钥 | L |
| T1.6 | 集成测试：鉴权矩阵（4 例）+ `/v1` 不受影响回归（2 例）+ 回环回退（1 例） | 全绿 | M |
| T1.7 | 文档：docs/core-admin-split-communication-protocol.md 增补鉴权章节 | — | S |

---

## 3. 工作流 2（WS2）：SSE 直通保险与心跳

### 需求
- 反代（Caddy/Nginx）后面的 SSE 流绝不因代理缓冲/空闲超时被破坏或掐断。
- 上游长时间静默（推理模型的思考间隙）时，向下游注入 SSE 注释帧防止中间设备回收连接。

### 设计要点
- `X-Accel-Buffering: no`：Core 的 Admin 事件流端点已有，**代理转发的流式端点没有**——补齐（Nginx 见此头自动对该响应关闭缓冲；Caddy 无此需要但加了无害）。
- 心跳：流式写循环挂空闲计时器，静默超过阈值写 `: ping\n\n`（SSE 注释帧，所有标准客户端忽略）；任何真实数据写入即重置计时器；随请求取消联动销毁，防计时器泄漏。
- 铁律 L2：ping 是注释帧，不进入正文语义；快照对比测试确保非静默路径字节不变（静默路径本来就没有字节可对比）。

### 任务拆解

| ID | 任务 | 验收标准 | 规模 |
|----|------|----------|------|
| T2.1 | 6 个流式端点（OpenAI 主/Responses×2/Anthropic/Gemini×2）响应补 `X-Accel-Buffering: no` + `Cache-Control: no-cache` | 头存在；响应字节与改造前一致（快照测试） | S |
| T2.2 | 心跳注入器：包装 SSE 写流（空闲阈值触发 `: ping`、数据重置、CT 联动取消），挂 `ProxyForwarding:SseHeartbeatSeconds`（默认 15，0=关） | 慢速 mock 上游测试：静默 > 阈值出现 ping；有数据无 ping；客户端断开无计时器泄漏 | M |
| T2.3 | 心跳同样覆盖 Admin 中继流（T3.4）与 AllInOne 的 SSE 出口（T4.2 集成时验证） | 中继静默期有 ping | S |
| T2.4 | 测试：SSE 帧完整性（ping 前后事件解析无损）、非流式不受影响 | 全绿 | S |

---

## 4. 工作流 3（WS3）：可恢复流 + Admin 中继

### 需求
- Admin↔Core 之间（未来还有中继↔Core）的 SSE 流中途断开时，断点续传，下游客户端完全无感。
- 不支持续传的既有客户端（直连 Core）行为零变化（铁律 L2）。

### 设计要点（线格式零侵入是硬约束）
- **不改普通响应的正文格式**：SSE 帧边缓存（按 `\n\n` 切帧存原始字节），正常流程不加 `id:` 行。
- Core 为每个活跃流式请求建侧缓冲：响应头新增 `X-Stream-Request-Id` 供中继识别；帧序号内部维护。
- 恢复协议：请求头 `X-Stream-Resume: {requestId}:{lastSeq}` → Core 从 seq+1 起重放缓存帧，再无缝衔接实时流；边界帧去重（重放止于缓存末帧，实时流从下一帧起）。
- 内存护栏：每请求缓冲上限 2MB（超限标记不可恢复，中继放弃重连）；完成后保留 5 分钟供迟到重连；并发缓冲流上限 50。
- 开关：`ProxyForwarding:StreamResumeEnabled`（默认 true——纯增量、不改线格式）+ 上述容量参数。
- Admin 中继（T3.4）：`/v1/*` 同路径反向代理，AccessKey 由 Admin 直接查库校验（Admin 是库的拥有者），上游断线时用 `X-Stream-Resume` 重连（最多 3 次，指数退避），客户端连接全程保持。
- **铁律 L1 自检**：中继是纯增量端点；Core 直连路径不经过中继，Admin 离线时直连照常。

### 任务拆解

| ID | 任务 | 验收标准 | 规模 |
|----|------|----------|------|
| T3.1 | Core：SSE 帧切分器 + 侧缓冲存储（容量/时间/并发三重上限、完成后的宽限保留、Dispose 联动清理） | 单元测试：切帧无损、超限降级、TTL 回收 | L |
| T3.2 | Core：流式端点挂缓冲 + `X-Stream-Request-Id` 头 + `X-Stream-Resume` 重入（重放→实时衔接、边界去重） | 集成测试：断在任意帧重连，客户端字节序列 = 不断开的字节序列 | L |
| T3.3 | Core：配置项 + 文档；直连（无 Resume 头）路径回归（零字节差异） | L2 快照测试全绿 | M |
| T3.4 | Admin：`/v1` 中继端点（同路径、AccessKey 本地校验、密钥注入、断线重连×3、心跳复用 T2.2；开关 `Relay:Enabled` 默认关） | 端到端测试：中途掐断 Admin↔Core，客户端流无感继续；直连客户端不受中继启停影响 | L |
| T3.5 | Admin：中继的并发/背压（单连接失败不影响其他；Core 不可达时明确报错文案） | 压测脚本 + 断 Core 时的错误语义 | M |
| T3.6 | 测试：重放去重/缺帧/超限/过期矩阵（≥8 例）；L1 守恒测试一并跑 | 全绿 | M |

---

## 5. 工作流 4（WS4）：AllInOne 单进程宿主

### 需求
- 一个进程 = Admin 管理面 + Core 代理面（对齐 master 的使用习惯）；同一套代码保持"可分离、可单机双进程、可单进程"三种形态。

### 设计要点
- 新项目 `AITool.AllInOne`（引用 Application/Infrastructure，并经 ApplicationParts 挑选控制器），避免 Admin 与 Core 的控制器路由冲突（两边都有 `api/admin/chat` 等——**显式白名单**：Admin 全部管理控制器 + Core 的 `Proxy/*` 与 `Core/*` 控制器，排除 Core 的 `Admin/ChatApiController`）。
- 配置源：**不注册** `ICoreRuntimeConfigProvider` → 元数据缓存自动走 DB 模式（现有双模设计，零新代码）。
- 事件链路：进程内直连——`DeveloperInvocationTraceStore.OnTraceCompleted → CoreUnifiedProxyEventPublisher → CoreAdminEventBus（内存 Channel）→ 进程内排空循环 → AdminUnifiedProxyEventIngestor`，绕过磁盘 spool 与 HTTP 拉取（磁盘 spool 代码保留给分离形态）。
- 不注册 CoreConfigSync/CoreEventPull 两个 HostedService（无跨宿主可同步）。
- 路由回退/熔断事件同进程内循环消费。
- 单端口（默认 5030）同时服务 `/v1` 与管理面。

### 任务拆解

| ID | 任务 | 验收标准 | 规模 |
|----|------|----------|------|
| T4.1 | 控制器清单与冲突矩阵（Admin vs Core），产出 AllInOne 的 ApplicationParts 白名单 | 矩阵入 docs；无路由二义 | S |
| T4.2 | `AITool.AllInOne` 项目：DI 组合（Admin 基础设施 + 代理运行时 + 进程内事件排空循环）、launchSettings 单端口 | 进程启动无异常；`/health`、`/v1`、管理 API 同端口可用 | L |
| T4.3 | 事件进程内化：排空循环复用 Admin 的 ingestor（日志/追踪/对话记录三条落地点全部走现有代码） | 代理一次请求 → UsageLog 落库 + Invocations 页可见 + 对话记录可见 | M |
| T4.4 | 集成测试工厂 + 关键 E2E：/v1 回合、管理端改配置即时生效（无同步延迟）、检测调度执行、聊天页、追踪可见 | 全绿 | L |
| T4.5 | 桌面端（Avalonia）连 AllInOne 冒烟（应只连 Admin API，理论零改动，实测确认） | 冒烟清单通过 | S |
| T4.6 | 发布脚本补 AllInOne 目标（win/linux ×3 宿主形态矩阵） | 脚本产出 6 类产物 | S |

---

## 6. 工作流 5（WS5）：Core 独立运行守恒（铁律 L1 的执行体）

### 任务拆解

| ID | 任务 | 验收标准 | 规模 |
|----|------|----------|------|
| T5.1 | **守恒自动化测试**：预置 last-good-config 启动 Core（无 Admin），跑全量代理矩阵——流式/非流式 × OpenAI/Anthropic/Gemini 桥 × 401 即刷（mock 上游） | 全部成功；此测试进入 CI 作为每个里程碑的门禁 | L |
| T5.2 | OAuth 凭据离线一致性验证：代码走读 + 实验（Admin 离线 → Core 401 刷新 → Core 重启）确认刷新后凭据是否会丢 | 结论入 docs；若会丢 → 执行 T5.3 | S~M |
| T5.3 | （条件触发）Core 刷新后凭据落盘：`core-runtime/credential-overrides.json` 原子写，启动时叠加到快照之上，Admin 推送更新版本后按账号清除 | 重启后仍用新 token；Admin 上线后正常收敛 | M |
| T5.4 | 守恒清单文档化：哪些能力依赖 Admin（改配置/看报表/检测/巡检）、哪些绝不依赖（/v1 全部），写入部署文档 | — | S |

---

## 7. 工作流 6（WS6）：可观测性 / 打包 / 文档

| ID | 任务 | 验收标准 | 规模 |
|----|------|----------|------|
| T6.1 | Admin 后端 `/api/admin/core-status`：服务端带密钥调 handshake（复用 T1.4 客户端），返回在线/离线、Core 版本与启动时间、配置版本一致性、事件积压、最近同步时间/错误 | Core 停止 10s 内面板变红 | M |
| T6.2 | 前端仪表盘 Core 状态卡片（绿/黄/红 + 明细） | UI 可见、轮询 10s、不阻塞页面 | S |
| T6.3 | 发布脚本：`publish.ps1`（Core linux-x64 / Admin win-x64 / AllInOne 双平台，版本戳注入） | 产物可直接运行 | M |
| T6.4 | 部署模板：`Caddyfile`（自动 HTTPS + SSE 直通）、`nginx.conf` 参考版（buffering off / read timeout 600s / WS 升级 / body size）、`systemd` 单元（Restart=always） | 模板可直接套用 | M |
| T6.5 | 部署文档：端口与 DNS、证书、`CoreAuth` 密钥配置、启动顺序（先 Admin 后 Core）、升级手册（只更 Admin 不动 Core；更新 Core 的排水窗口）、备份（last-good-config）、敏感文件权限 | 按文档可从零部署 | M |
| T6.6 | README 修正（技术栈段落仍写 Razor Pages + EF Core）+ 架构图补 AllInOne 与中继 | — | S |
| T6.7 | 清单收尾：master 旧 stash 处置建议、`.zcode` 忽略确认 | — | S |

---

## 8. 显式不做（Non-Goals）

- `/v1` 不加 IP 白名单/限速（个人工具，AccessKey 足够；公网暴力破解面由密钥长度兜底）。
- 跨宿主不引入 JWT/YARP 等新框架——共享密钥 + 手写转发，依赖零增长。
- 不做多 Core 实例/负载均衡（单实例 + systemd 拉起）。
- master 分支不改动、不删除（归档与否是用户决策）。
- 不改数据库 Schema 以外的存量行为；新列继续走 SqlSugar CodeFirst 自动建列。

---

## 9. 全局验收标准（Definition of Done）

1. 存量测试全绿：单元 317+ / Admin 集成 143+ / Core 集成 271+ / 前端 118+（只增不减）。
2. **T5.1 守恒测试在 CI 常驻**：每个里程碑合入前必跑。
3. L2 快照对比测试：随机真实流量回放，`/v1` 响应字节与基线一致（仅允许新增响应头）。
4. 手工冒烟清单：双宿主分离部署（Admin 本地 + Core https 远端）、单机双进程、AllInOne 三形态各过一遍核心路径（代理/管理/检测/聊天/开发者追踪/桌面端）。
5. 文档三件套齐：通信协议（含鉴权）、部署手册、AllInOne 说明。
6. 版本号递增推送两个远端。

---

## 10. 风险与对策

| 风险 | 影响 | 对策 |
|------|------|------|
| 可恢复流的缓冲内存顶穿 256MB 堆上限 | OOM | 三重上限（单请求 2MB / 并发 50 / TTL 5min）+ 超限降级为不可恢复而非报错 |
| Admin 中继成为新的故障点 | 可用性 | 中继默认关闭（`Relay:Enabled=false`）；直连路径永远保留且不经过中继 |
| AllInOne 控制器路由冲突 | 启动失败 | T4.1 白名单矩阵先行；启动时路由去重断言 |
| 共享密钥误配置导致 Admin 无法连接 Core | 管理面失联 | 未配置=回环放行；握手失败时 Admin 端明确报错文案（区分 401 与网络不可达） |
| 心跳帧被非标准客户端当数据处理 | 客户端兼容 | SSE 注释帧是规范行为；对不支持的场景提供 `SseHeartbeatSeconds=0` 关闭 |
| CodeFirst 新列在存量库上自动建列 | 升级异常 | 沿用既有 CodeFirst 通道（7414eb0/ca96d92 已验证），升级前备份 db 文件写入部署文档 |

---

## 附录 A：T1.1 盘点结果（Admin→Core 调用点清单）

**服务端注入点（2 个）：**
1. `CoreAdminClient`（Infrastructure/CoreRuntime）——8 个方法：handshake / full-sync / patch-sync / events-ack / events-replay / developer-concurrency / developer-metadata / developer-invocations / developer-invocation-detail（9 个端点）。DI 注册：Admin/Program.cs:366 `AddHttpClient<CoreAdminClient>`；使用方：AdminCacheInvalidationService、CoreConfigSyncHostedService。
2. `CoreEventPullHostedService`（Admin/Services）——自建无超时 HttpClient 连 `/api/core/events/stream`（SSE 长连接）。

**Core 侧受保护端点（`/api/core/*`，7 个控制器）：**
CoreConfigController(status)、CoreConfigHandshakeController(handshake)、CoreConfigSyncController(full-sync/patch-sync)、CoreDeveloperQueryController(developer/*)、CoreEventAckController(ack/replay)、CoreEventStreamController(stream)、CoreRuntimeStatusController(runtime)。

**浏览器直连 Core：不存在。** 前端全部走 `/api/admin/*`（dashboard 的 coreBaseUrl 只是服务端下发的展示文本）；ClientSimulator 的 baseUrl 是用户自填目标（与 Core 无关）。→ **T1.5 归档为“无需执行”**（密钥不出服务端的目标天然满足；core-proxy 取消，等 T3.4 中继时按需再议）。
**桌面端直连 Core：不存在**（LoginView 的 5029 仅为旧地址水印）。

---

## 附录 B：M4 执行记录（AllInOne 控制器白名单与 DI 组合）

**控制器矩阵（冲突 → 采用）**
- 采用：Admin 全部管理控制器（除外） + Core `Controllers/Proxy/*` + `Controllers/Core/*`（分离形态保留）
- AllInOne 排除：Admin `ChatApiController`（聊天真转发链路由 Core 版提供）、`V1RelayController`（/v1 由 Core 控制器直接提供）
- 实现：`AllInOneControllerFilter : IApplicationFeatureProvider<ControllerFeature>`（ApplicationParts 追加两宿主程序集后按类型移除）

**DI 组合**
- Admin 服务段 → `AdminProgramServices.AddAdminHostServices`（Admin/AllInOne 复用）
- Core 独有段 → `CoreProgramServices.AddCoreProxyHostServices`（Core/AllInOne 复用）
- 代理运行时 → `AddProxyRuntimeInfrastructure(useCoreRuntimeConfigProviderForCache:false, enableEventSpooling:false)`（DB 模式 + 无磁盘 spool）
- 事件闭环 → `InProcessCoreEventConsumerHostedService`（总线 → CoreEventPullService.ProcessEnvelopesAsync，与分离形态同消费逻辑）
- 凭证刷新回写 → `DbBackedRuntimeConfigProvider`（AllInOne 专用，满足 CoreCredentialRefreshEngine 的快照读写面）

**验证**：AllInOneHostTests 4 例（健康/管理面、/v1 回合、事件进程内落库可见、聊天转发）全绿。

---

## 附录 C：M5 补充执行记录

- 探测逻辑抽取：`CoreStatusProbe`（握手 + 8s 内存缓存，TTL 内复用结果不发请求），
  DashboardApiController 仅做委托；配 `CoreStatusProbeTests`（缓存命中/离线降级 2 例）
- Admin 集成测试的进程型用例（真实 Core 子进程 + 本地端口）纳入串行集合，
  消除端口探测释放后的并行抢占竞态（RelaySerialized）
- 最终回归：Admin 153 / Core 291 / 单元 328 / 前端 118 全绿；
  性能门 2 例（100 次转发均值 <100ms、连续流式后宿主健康）通过
