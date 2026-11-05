# 账号网络出口与会话隔离

> 账号网络出口与会话隔离的使用和运维说明。请在部署前备份数据库与密钥目录，并先在隔离环境验证。

## 1. 当前行为与边界

### 三态网络策略

`AccountNetworkPolicies` 使用 `(ProviderKey, AccountId)` 复合身份独立存储，不混入账号 token/额度缓存。Provider 为 `codex`、`google`、`kimi`、`xai`、`device-oauth`；通用厂商的账号以 `device-oauth + 本地账号 GUID` 区分，不用 workspace、邮箱或 token 充当账号主键。

| 模式 | 当前实现 | 失败与兼容边界 |
| --- | --- | --- |
| `Legacy`（0） | 无策略行的默认值；保留原有站点/密钥/映射选择以及系统/环境代理行为。 | **不等于强制直连，也不保证登录、额度、转发出口一致**。普通站点未指定出口可保留原默认 client。 |
| `Direct`（1） | `UseProxy=false`，账号显式选择优先于隐藏站点/密钥/映射出口。 | 不使用系统/环境代理，但仍受操作系统、DNS、网络部署影响。 |
| `ProxyProfile`（2） | 绑定方案稳定 Id；实际发送前读取启用状态、当前 endpoint、revision、凭据及代理子开关。 | 缺失、禁用、解密失败、无效配置、代理子开关关闭均阻断；不回退默认 client、Legacy、直连或无认证代理。 |

`Blocked` 通过结构化 `NetworkPolicyException.Code` 表达，并非第四个可保存的模式。策略 CAS revision 是字符串；代理方案 revision 是 Int64，二者不可混用。

- `AccountNetworkClientFactory` 的每次 send 重新解析账号策略；`ForwardNetworkPolicyResolver` 让显式账号 Direct/Profile 优先于旧出口选择。缓存携带账号身份/未解析 selector，不冻结代理地址。
- 进行中的登录流程固定服务端 opaque session 与 profile revision，版本改变或撤销要求重新开始；客户端不能用自己提交的 device code 替代该 session。
- 已获准发送的流/轮换 token 响应可完成并保留结果，不能撤销已发生的上游操作；后续 send/重试须重新检查。策略失败不是 refresh-token 失效，不应清空凭据/标记需要重新登录。
- `DeveloperEnabled` 管管理入口可见性；`DeveloperProxyProfilesEnabled` 是代理子开关。关闭后保留账号绑定并阻断 Profile 账号的新请求。普通站点 Legacy 的历史“总关后使用默认路径”兼容行为与此不同。
- 被账号引用的 profile 删除返回 409 和引用提示，不自动解绑。普通站点新选择保存 `profile:<GUID>`；旧 key/URL 保持兼容解析，失效明确引用不静默直连。

### 连接池、认证与 TLS

生产 `NetworkTransport` 默认最多 **64** 个存活 entry，按实际账号/待登录 session、用途、模式、profile Id 和 revision 分池，不按每个对话永久建池。活跃及 retired-but-leased entry 都计容量；容量不足会明确失败，不强行淘汰长流。空闲 entry 可 LRU 回收。

handler 创建后不改共享状态；关闭自动 Cookie 容器，不共享可变“当前账号/代理”或 transport client 的 DefaultRequestHeaders。代理认证使用显式 `NetworkCredential`，不依赖 URL userinfo。禁止自动重定向；显式后续跳转须作为新 send 再验证策略。产品保留正常 TLS 验证；本地传输测试的证书信任仅测试内部注入。

真实 loopback 测试证明的是 transport 协议与失败边界，不代表所有厂商都已逐操作跑过真实代理。`Testing` 宿主注册 `TestingNetworkTransport`，宿主测试也不能被当成生产真实出网证据。

### 已接入的操作、UI 与公共网络边界

源码接入已覆盖专用 OAuth client、通用 vendor factory、供给/删除事务、token/额度/credits/模型服务、最终转发器，以及网页 Chat、健康探测、目录、每密钥额度和诊断的凭据上下文传递；具体测试边界见第 5 节，不能从“有接入”推断所有组合已验证。

- 通用 factory 当前创建 MiniMax CN/global、WorkBuddy CN/global、Qoder/global+CN、Trae CN/global、Kiro、Copilot、Claude、ZCode 共 12 个绑定 vendor；registry 只用作定义，不给共享 vendor 写入当前账号。全部厂商×全部有效操作的验收不能仅靠 factory switch 存在来证明。
- 服务端 token 交换/轮询/账号 API 经过所选网络；用户浏览器打开的授权网页仍使用**浏览器自身网络**，此功能不会替浏览器设置代理，也不保证浏览器与服务端同出口。
- 公共非账号目录必须与携带账号 token 的模型请求区分。当前 Codex 账号 FetchModels 明确只拉账号模型，公共目录使用已有缓存，返回 `catalogRefreshed=false`；这不表示公共目录远端刷新本身已账号绑定。不能把账号 token 请求误归类为公共默认出口。
- Web `OAuthView.vue` 和 Desktop `CodexViewModel.cs` 都读取 `/api/admin/oauth/network-policies/capabilities`；Discovery 使用 OAuth 鉴权，不依赖开发者 CRUD 页可见。
- Web 有登录/导入/编辑/批量选择、模式/阻断摘要、凭据 Keep/Replace/Clear。Desktop 同步现有 Codex/Google，不扩充为所有厂商客户端。旧客户端缺字段/重复导入保留原策略，凭据导出不附本机 profile Id/代理秘密。编辑先保存策略、再更新账号资料/凭据，部分成功有明确提示，**不是两个 HTTP 请求间的原子事务**。

## 2. 会话隔离与有状态引用

### 稳定派生与兼容变化

所有已接入的托管 OAuth 账号统一应用隔离，没有为旧账号提供默认绕过。升级可能使部分旧会话/上游缓存不再续接，需开启新会话或提交完整历史；这也是获批的兼容边界。

派生器使用独立随机 256-bit 密钥、派生协议版本 1 和 HMAC-SHA256，对 `版本 + 可信客户端 scope + provider + 本地账号 GUID + 字段域 + 原始 seed` 采用长度前缀 strict UTF-8 编码。Proxy scope 来自 ProxyAccessKey.Id，管理 Chat 使用独立 admin scope；路由/号池/引用入口、workspace、token 不进入账号身份。

入口先捕获原始 seed，在选定账号、协议转换、emulation 和 extra headers 合并后改写：

| 协议/字段 | 当前处理 |
| --- | --- |
| Responses/Codex | `prompt_cache_key` 与 `Session-Id` 使用相同 session 派生；按已知语义处理 `Conversation_id`、`Thread-Id`。 |
| Codex metadata | 只处理结构化 `X-Codex-Turn-Metadata` 的 `session_id/sessionId/conversation_id/thread_id`；不遍历改写任意嵌套字段，无法安全识别的 opaque metadata 移除。 |
| Anthropic | 已有 `X-Claude-Code-Session-Id` 及 JSON 字符串 `metadata.user_id.session_id` 按已知语义隔离；opaque user_id、device_id 保持原样。 |
| Antigravity | 用原始客户端 seed 派生正 Int63 十进制 `request.sessionId`/`labels.trajectory_id`，替代桥接层第一条消息哈希。 |
| 客户端别名/设备身份 | `X-Session-Id`/`X-Conversation-Id` 可提供入口 seed，不凭空添加转发别名；WorkBuddy `X-Session-ID` 等设备头和 User-Agent 不随意重写。 |
| 普通非托管 API 站点 | 不应用账号会话字段改写；其上游引用仍受独立归属约束。 |

没有稳定 session 的 HTTP 请求用请求级随机 seed，因此仅保证**本请求及其重试**稳定，不承诺跨 HTTP 请求/进程重启自动重建同一会话。WebSocket 可以用连接级 seed；断线重连不能自动还原进程内 transcript。相同账号、未变更的会话密钥、客户端 scope 和显式 seed 才能保持派生结果跨重启稳定；当前 v2 密钥随数据库保存，不依赖 key ring 解密。

共用同一个访问密钥且不提供独立 session 的多个终端用户无法被自动区分。隔离不修改设备/安装指纹，不承诺隐藏服务商关联、解除限制、改善模型质量或将不同账号变成可共享上游状态的账号。

### Responses 引用归属

`UpstreamReferenceGuard` 在 normalization/桥接可能删除字段前读取原始 `previous_response_id`、conversation、item_reference。`UpstreamReferenceOwners` 保存标识摘要、客户端域、owner、origin+protocol namespace、native/synthetic、暴露准备状态、时间/冲突标记，不保存 transcript 或原始 handle。

- 已知归属在原授权路由集合内硬过滤至相同 owner 和 namespace；无合格路由返回错误，不向其他账号逐个探测。普通站点 owner 是保守的 SiteKeyId/站点身份，不宣称识别自然人账号。
- 未知、过期、冲突、多 owner/namespace、unsupported reference shape 明确拒绝。原生 handle 不伪造；合成 ID 不伪装成上游 native handle。不通过偷偷删引用或把 `store=false` 改为 true 来“续接”。
- 默认 TTL 24 小时，从首次记录起算；读取和同 owner 写入不延长，查询严格检查到期，清理有界。此本地窗口不保证上游仍有历史。
- HTTP JSON、SSE/桥接 chunk、下游 WS 输出前调用归属提交；先提交再可能暴露 ID。该提交与实际网络交付无法做原子事务，取消/部分写失败可能留下客户端没收到的归属，必须保守保留，不能因此允许跨账号回退。
- WS 安全 replay 仅限**本连接精确匹配最后完成响应且本地历史完整**的下一轮；错误引用、重连、外部旧引用或中断历史不套用上一轮 transcript。首版没有全量聊天历史数据库，也不支持任意跨协议透明续接。

这些是当前代码机制；各桥接方向、head buffer、取消/断线时序的专门安全验收仍须满足第 6 节，不可拿普通协议转换测试替代。

## 3. 旧库迁移、秘密与恢复

### 启动顺序与迁移范围

`Program.cs` 当前先运行 `SqlSugarSetup.InitializeDatabase`，再调用 `ProxyProfileSecretService.MigrateLegacyCredentialsAsync`，最后 `SessionIsolationKeyStore.EnsureInitializedAsync`。

1. CodeFirst 补 `ProxyProfiles.Revision/ProtectedUsername/ProtectedPassword/ConfigurationError/ProtectedLegacyConfiguration` 并创建 `AccountNetworkPolicies`、`SessionIsolationKeys`、`SessionIsolationKeyMetadata`、`UpstreamReferenceOwners`。补列本身不加密旧 URL。
2. 凭据迁移将合法 userinfo 拆分、按 profile Id/字段用途进行 Data Protection 保护，公开 endpoint 不含用户名密码；正常重复执行不再改写。
3. 不合法的旧 profile 禁用并记录固定错误码 `invalid_legacy_proxy_endpoint`；原始配置仅留在受保护的修复字段，不让单条坏配置阻断无关账号。无效 profile 被明确选择时仍失败。不得将隔离原文直接打日志或返回 API。
4. 默认兼容模式使用 v1 Data Protection 密文，供旧实例混合运行；所有实例必须使用相同的持久 key ring 和 application name。`SessionIsolation:EnableRawV2=true` 才启用数据库内 Base64 原始字节存储 v2，不再依赖外部 key ring；metadata 保存存储字符串的 SHA-256 指纹。存储版本与派生协议版本独立，派生仍使用版本 1。
5. **分阶段升级/回滚**：先以默认值（可用环境变量 `SessionIsolation__EnableRawV2=false`）部署本版本，并验证所有实例共享原 key ring。必须停止全部不支持 v2 的旧实例，且禁止旧实例再启动后，才可在所有新实例启用 v2；v1 行可解密时事务内升级，密钥和派生结果不变。启用 v2 后绝不可直接启动旧二进制（旧代码会轮换密钥）。回滚时先停止所有 v2 写入实例，以原 key ring 启动本版本的兼容模式，确认密钥和 metadata 均已转回 v1，再启动旧二进制。回滚转换失败会阻止启动；期间不得混用 v2 模式和旧二进制。默认兼容模式遇到现有 v2 行会先将其转为 v1，以支持已部署 v2 的受控回滚，但不能阻止已经运行的旧实例先轮换，须先停机。
6. 会话密钥或 metadata 缺失、不一致、损坏、版本未知或无法解密时，代码会尝试事务性生成新密钥，并将 `UpstreamReferenceOwners` 标为冲突直至过期；并发初始化/修复采用已成功持久化的有效密钥。v1 key ring 丢失也会触发轮换。密钥轮换会改变派生标识，旧引用可能无法续接，需要新建会话或提供完整历史。首次部署同样会初始化密钥；两表同时丢失时无法区分首次部署与数据损坏。
7. 无法解密的代理方案凭证不会通过生成密钥恢复：启动迁移会保留原密文、禁用相应方案并标记 `proxy_secrets_undecryptable`，其他有效方案继续迁移；需要恢复匹配的 key ring 或重新录入凭证并处理方案状态。该隔离不等于绕过网络策略回退直连。

自动恢复不保证所有启动故障都可忽略：数据库不可写、事务失败、密钥轮换后无法重新读取，或 key ring 目录配置/权限失败等仍可能导致操作或启动失败。

迁移前应备份数据库与 ring。历史数据库备份仍可能保有明文 userinfo；当前逻辑不承诺 SQLite 空闲页/WAL/旧备份已安全擦除，也没有将所有既有 OAuth token 改为密文存储。不要误称为“全库加密”。

### key ring 选址及保护边界

Data Protection 应用名固定为 **`AITool.NetworkSecurity.v1`**，不复用管理员 JWT signing key。`NetworkKeyRingConfiguration` 作为 `IConfigureOptions<KeyManagementOptions>` 读取最终 DI 的 `ISqlSugarClient` 连接配置，不仅看可能被替换的配置连接串。

- 文件 SQLite 默认 `<数据库目录>/.aitool-secrets/<数据库文件名>/keyring`。
- `NetworkSecurity:KeyRingPath` / `NetworkSecurity__KeyRingPath` 可覆盖，相对路径基于 `ContentRootPath`；相对数据库源的默认 ring 也基于该目录计算。生产建议使用绝对 DB/ring 路径，避免数据库 provider 的工作目录语义与宿主不同造成误配。
- 非 `Testing` 的 `:memory:` 或缺少有效数据源且未显式指定 ring 时失败。`Testing` 为每个 DI 容器选择独立临时目录，不具备生产恢复语义。
- 目录创建/写入或权限设置失败会影响启动；不能依赖 ephemeral 容器层、随发布覆盖的输出目录或重启即清空的临时卷。

**当前没有显式 XML key encryptor，文件系统 key ring 的 XML 可含未另行加密的 key bytes。** Data Protection 字段保护不等于本地文件系统失陷后仍安全；拿到数据库与可用 ring 的攻击者可能解密这些字段。ring 本身就是秘密。

- Unix 代码将最终 ring 目录设为 `0700`，不等于父目录/文件/挂载点/备份的完整权限审计；Windows 测试环境无法证明 Unix 路径已实际执行。
- Windows 必须由运维设置并检查 NTFS ACL，限服务身份和获授权管理员/备份身份必要访问，移除不必要继承及普通用户权限。当前代码/测试没有自动建立或验收 ACL。
- 不将 ring 放进版本库、静态资源、公开下载、日志或工单；保护数据库及备份目录。管理员或应用进程失陷不在此保护能力之外另有保证。
- 若需要 XML key 额外加密/外部密钥管理，须另行设计配置并验证恢复兼容性；本次不声称已提供。

### 备份、重启与多实例

一起保存一致性 SQLite 备份（正确处理 WAL，使用受支持备份/停机流程）、完整 ring（含历史 key）、应用名/路径与服务身份/权限配置；备份存储本身应加密并限权。当前 v2 会话密钥随数据库备份恢复；但代理凭据和尚未升级的旧 v1 会话密钥仍依赖匹配的 ring 和应用名。只恢复 DB、换 ring 或改应用名可能导致代理方案被隔离，或旧 v1 会话密钥自动轮换；应优先恢复配套材料，并检查启动日志及受影响方案状态。

Data Protection 的 key 生命周期、会话密钥存储版本 v2 与派生协议版本 v1 是不同概念。自动修复是故障恢复，不是无损的运维轮换流程；不要通过删除 ring 或会话密钥行来执行常规轮换。密钥轮换会清除持久引用归属，权限与配套备份仍必要。

多实例共享同一 DB 时会使用该库内的 v2 会话密钥；涉及代理凭据或尚未升级的 v1 密钥时，还需共享兼容的持久化 ring 和应用名。应验证文件共享/锁和 SQLite 部署拓扑受支持。不同 DB 各自生成会话密钥，即使共用 ring 也不会因此共用该密钥。现有独立 provider/并发测试不是任意网络文件系统、多机故障恢复或真实重启演练。

reference owner 的持久归属可跨 provider 重建恢复到原 TTL；WS transcript、待登录 session 与缺 seed 的随机上下文不能从该表还原。恢复 ring 也不能让服务商已丢失的历史重新出现。

## 4. 实现产物索引

以下路径均相对仓库根目录，矩阵中的测试写作 `类名.精确方法名`，对应同名测试文件。

| 范围 | 实现产物 |
| --- | --- |
| 身份/策略/事务 | `src/AITool.Domain/Operations/AccountNetworkPolicy.cs`、`src/AITool.Web/Services/AccountNetworkPolicyStore.cs`、各 `*AccountProvisioner.cs`、`DeviceOAuthServices.Core.cs`、`ProxyRequestMetadataCache.cs` |
| 秘密/启动 | `src/AITool.Web/Services/ProxyProfileSecretService.cs`、`NetworkKeyRingConfiguration.cs`、`SessionIsolationKeyStore.cs`、`SessionIsolationDeriver.cs`、`Program.cs`、`src/AITool.Infrastructure/Persistence/AppDbContext.cs` |
| 传输/最终发送 | `src/AITool.Infrastructure/Networking/NetworkTransport.cs`、`src/AITool.Web/Services/AccountNetworkClientFactory.cs`、`ForwardNetworkPolicyResolver.cs`、`NetworkBoundOAuthClientFactory.cs`、`NetworkBoundDeviceOAuthVendorFactory.cs`、`src/AITool.Infrastructure/Proxy/ProxyForwardService.cs` |
| 会话/引用 | `src/AITool.Web/Services/AccountSessionIsolationService.cs`、`UpstreamReferenceGuard.cs`、`UpstreamReferenceOwnerStore.cs`、`src/AITool.Web/Controllers/Proxy/OpenAiProxyController*.cs`、`AnthropicProxyController.cs`、`Controllers/Admin/ChatApiController.cs` |
| 辅助/管理 | `src/AITool.Infrastructure/Sites/SiteKeySelector.cs`、`OpenAI/OpenAiSiteCatalogClient.cs`、`Health/ModelHealthRequestService.cs`、`src/AITool.Web/Services/*SiteQuotaProvider.cs`、`SiteQuotaService.cs`、`Controllers/Admin/DeveloperInvocationsApiController.cs`、`AccountNetworkPoliciesApiController.cs`、`ProxyProfilesApiController.cs` |
| Web/Desktop | `frontend/src/api/accountNetwork.ts`、`oauth.ts`、`deviceOauth.ts`、`proxyProfiles.ts`、`frontend/src/components/AccountNetworkSelector.vue`、`views/OAuthView.vue`、`ProxyProfilesTab.vue`、`SitesView.vue`；`src/AITool.Desktop/Models/AccountNetworkModels.cs`、`Models/CodexModels.cs`、`ViewModels/CodexViewModel.cs`、`Views/CodexView.axaml` |

## 5. 获批要求—测试证据矩阵

矩阵是阅读得到的**已有断言与范围**，不是独立执行报告。Application 在 `tests/AITool.ApplicationTests/Networking/`；其余后端测试在 `tests/AITool.IntegrationTests/` 的指定子目录。

| 要求 | 精确测试入口 / 证据产物 | 证明范围与未覆盖边界 |
| --- | --- | --- |
| 实际 HTTP A/B 代理、认证、默认出口零调用 | `NetworkTransportTests.Profiles_A_and_B_use_explicit_proxies_and_do_not_share_headers_or_authentication`；`Proxy_407_and_connection_failure_never_fall_back_to_default_proxy_or_origin` | loopback 真实 sockets，含 absolute-form、Basic proxy auth、407/连接失败及默认出口计数。不是厂商级端到端。 |
| 强制直连与 Legacy 不混淆 | `NetworkTransportTests.Direct_ignores_controlled_default_proxy_while_legacy_uses_it` | 测试内部注入 controlled default proxy，不修改进程全局环境。 |
| CONNECT、HTTPS proxy、TLS 保持 | `NetworkTransportTests.HTTP_proxy_CONNECT_reaches_TLS_origin_with_test_only_certificate_trust`；`HTTPS_proxy_endpoint_supports_TLS_to_proxy_separately_from_CONNECT`；`Production_transport_does_not_trust_loopback_self_signed_TLS` | 分别覆盖 HTTP proxy→TLS origin 的 CONNECT、TLS-to-proxy 的 HTTP 请求、自签证书默认拒绝；不冒称一个测试覆盖所有双 TLS 组合。 |
| SOCKS 声称支持的握手 | `NetworkTransportTests.SOCKS_profiles_use_declared_protocol_and_send_target_to_proxy` | theory 为 socks4/4a/5；检查目标/用户名，socks5 有显式认证。并非 SOCKS4 密码认证保证。 |
| 隔离池、Cookie/认证、长流/容量 | `NetworkTransportTests.Pool_reuses_only_identical_opaque_scope_purpose_and_generation`；`Cookies_and_request_authorization_are_never_replayed`；`Capacity_never_evicts_active_clients_counts_retired_and_recovers_on_release`；`Active_stream_survives_revision_retirement_and_transport_disposal` | 参数化小容量验证算法及真实流生命周期；默认 64 来自生产构造参数，不是一次 64 活跃账号负载验收。 |
| 每 send 解析、body lease、失败不 fallback | `Services/AccountNetworkClientFactoryTests.Each_account_send_resolves_latest_policy_and_uses_provider_scoped_identity`；`Stream_EOF_or_disposal_releases_once_without_waiting_for_response_disposal`；`Send_failure_releases_lease_and_does_not_retry_or_fall_back`；`Real_transport_does_not_automatically_follow_loopback_redirect` | 以 recording handler 为主，redirect 有真实 loopback；覆盖取消/释放的其他方法同文件。 |
| 旧/新账号策略、provider GUID、撤销/版本 | `Services/AccountNetworkPolicyStoreTests.All_provider_tables_support_legacy_direct_and_delete_without_touching_credentials`；`Profile_binding_uses_stable_id_and_reads_current_endpoint_revision_and_protected_credentials`；`Bound_profiles_fail_closed_after_change_and_invalid_rebinding_rolls_back` | 五类账号行与配置失败断言；不是五类全部远端业务操作。 |
| 供给/删除原子性、编辑与刷新竞争 | `Services/AccountNetworkPolicyStoreTests.Caller_transaction_can_rollback_new_account_and_policy_together`；`Final_binding_revalidates_frozen_profile_inside_caller_transaction_without_leaking_policy`；`Assignment_and_actual_profile_delete_serialize_under_contention_in_both_orderings`；`Services/AccountNetworkLifecycleRaceTests.Linked_site_and_account_refresh_share_single_flight_and_return_persisted_token`；`Account_delete_failure_rolls_back_policy_account_and_entire_site_graph_without_invalidating_cache` | 临时 DB、事务回滚、受控竞争；覆盖的是指定操作交错，不是任意竞争穷举。 |
| API 探索不依赖开发者页、缺字段/批量失败 | `Services/AccountNetworkPoliciesApiTests.DiscoveryIsIndependentOfDeveloperUiAndNeverReturnsEndpointOrSecrets`；`MissingSelectionDoesNotClearAndBatchReportsPartialSuccess`；`NumericSelectionRoundTripsAndSummaryShowsDisabledBindingWithoutFallback` | 控制器合同与脱敏摘要，不能替代 GUI。 |
| secret CRUD、迁移、删除冲突、probe | `Developer/ProxyProfileCredentialTests.LegacyCrud_StillUsesSameResponseShapes_AndNeverReturnsCredentials`；`Update_ExplicitKeepReplaceClear_AndStaleRevisionAreSafe`；`Migration_QuarantinesInvalidLegacyProfilesWithoutBlockingStartup`；`Migration_LostRing_QuarantinesProtectedProfilesInsteadOfBlockingStartup`；`Delete_ReferencedProfileReturns409AndReferences_ThenSucceedsAfterUnbinding`；`Probe_ByProfileIdUsesStoredCredentials_AndOnly2xxIsSuccess`；`ProbeHandler_UsesExplicitNetworkCredentialAndNormalTlsVerification` | 合成凭据；probe 有本地上游，TLS 配置有 handler 检查。旧备份/空闲页擦除不在断言内。 |
| 日志/错误不含代理秘密 | `Services/HttpLogFormatterTests.Proxy_configuration_body_is_omitted_even_when_not_valid_json`；`Developer/ProxyProfileCredentialTests.InvalidUrls_AreRejectedWithoutEchoingCredentials` | 指定 body/错误路径验证，不是所有诊断/厂商异常的完整秘密扫描。 |
| key ring 跟最终 SQL DI、覆盖路径、memory | `Services/NetworkSecurityStartupTests.Default_key_ring_follows_effective_SQL_DI_replacement_not_configuration_connection_string`；`Configured_relative_or_absolute_override_is_resolved_against_content_root`；`Memory_database_without_override_fails_outside_Testing`；`Testing_memory_database_gets_stable_per_container_but_isolated_key_directory` | 生产 options 注册与真实 DP/临时文件，不启动 Web host；同文件覆盖相对 DB、不同 DB、显式 memory override。 |
| 生产旧库迁移、restart、Unix | `Services/NetworkSecurityStartupTests.Production_CodeFirst_upgrades_populated_legacy_profiles_and_preserves_account_credentials_and_cache`；`Restart_with_same_database_and_production_options_preserves_credentials_and_session_derivation`；`Key_directory_is_owner_only_on_Unix_including_preexisting_permissive_directory` | 旧 ProxyProfiles 物理 schema、Codex 凭据/缓存保留、重复初始化和重建 provider；不是 OS 进程崩溃演练；Unix 分支在 Windows 直接返回，ACL 需运维。 |
| HMAC 派生、兼容模式、v2 存储、v1 升级及损坏恢复 | `Services/SessionIsolationKeyTests.Derivation_matches_explicit_versioned_length_prefixed_hmac_contract`；`Initialization_persists_raw_v2_key_directly_in_the_database`；`Concurrent_instances_claim_one_key_and_all_use_the_persisted_winner`；`Legacy_v1_row_upgrades_in_place_to_raw_v2_without_rotating`；`Legacy_v1_row_under_lost_ring_or_wrong_purpose_rotates_to_v2_once`；`Corrupt_or_missing_secret_rotates_atomically_and_derivation_recovers`；`Deleted_key_rows_self_heal_and_full_deletion_reinitializes` | 临时 DB/独立 providers，验证原地升级、损坏轮换和并发初始化；不代表真实部署、多机灾备或所有故障均可自动恢复。 |
| 专用厂商登录/导入、opaque pin/single-flight | `OAuth/DedicatedOAuthLoginNetworkTests.Login_pins_profile_before_first_request_and_changed_profile_aborts_exchange`；`Duplicate_raw_and_multipart_imports_preserve_policy_and_use_exact_existing_account`；`Failed_explicit_policy_publication_rolls_back_credentials_for_final_existing_identity`；`Concurrent_successful_device_polls_exchange_once_then_replay_is_rejected` | Codex/Google/Kimi/xAI theory 范围按方法 InlineData；用 concrete controllers/clients 和 recording transport，不访问真实厂商。 |
| 专用厂商额度/刷新/credits、不误判失效 | `Services/DedicatedOAuthNetworkTests.Quota_services_send_real_requests_with_each_accounts_guid_and_selected_proxy`；`Concrete_oauth_clients_refresh_concurrently_without_mixing_headers_cookies_or_tokens`；`Background_refresh_uses_account_policy_and_persists_rotated_tokens_and_site_key`；`Quota_401_refreshes_credentials_and_retries_through_the_same_account_policy`；`Codex_reset_credit_query_and_consume_use_the_selected_account_proxy`；`Proxy_policy_failure_during_background_refresh_preserves_credentials_and_reauth_state` | 方法名虽含 real_requests，实际是生产请求构造送入 recording transport，不是真代理出网；额度/刷新四厂商，401 theory 为 Codex/Google/Kimi，credits 仅 Codex。 |
| 通用 vendor、无隐藏 Site、轮换响应撤销 | `OAuth/DeviceOAuthNetworkTests.Accounts_A_B_models_are_isolated_and_repeat_sends_resolve_current_policy`；`Cross_login_pending_polls_keep_distinct_fixed_context_and_revocation_fails_closed`；`No_site_credential_refresh_uses_exact_account_and_network_failure_is_not_reauth`；`Approved_rotating_refresh_persists_after_revocation_during_response_and_next_send_is_blocked`；`Every_factory_variant_preserves_region_timeout_and_account_or_pending_binding` | 使用 fake transport 验证 12 种厂商变体的绑定及代表性请求；不代表每个厂商的每种操作都已真实联网验证。 |
| 最终转发/401/路由 clone/总关 | `Services/ForwardNetworkPolicyTests.Account_direct_and_profile_override_invalid_mapping_and_policy_is_live`；`Global_off_preserves_ordinary_default_but_blocks_account_profile_and_keeps_direct`；`Revocation_after_401_blocks_refresh_callback_and_never_sends_default`；`Each_401_retry_re_resolves_policy_and_lease_outlives_response_body`；`Route_reference_clone_chat_and_fallback_preserve_owner_and_raw_profile_identity`；`Ambiguous_eligible_owner_blocks_cache_instead_of_choosing_first` | 发送边界与 clone/身份断言；不能仅以 clone 测试认定全部主路由/号池/引用业务组合已 E2E。 |
| 辅助目录/健康/每 key 额度/Chat/诊断 | `Services/AuxiliaryNetworkPolicyTests.Catalog_and_health_use_selected_key_owner_and_unresolved_selector`；`Quota_service_builds_context_separately_for_each_key`；`Quota_providers_preserve_selector_and_lease_through_body`；`Explicit_bad_selector_never_uses_default_client`；`Selected_chat_clone_preserves_account_egress_and_emulation`；`Chat_raw_stream_resolves_policy_and_holds_lease_until_SSE_is_consumed`；`Diagnostic_target_and_AI_model_clones_preserve_credential_policy` | fake handlers、部分 reflection/clone 检查；覆盖 selected key、三 quota providers 和 raw SSE，非每个入口真实 HTTP E2E。 |
| 会话原始 seed、双 scope、设备不改 | `Proxy/AccountSessionIsolationTests.Original_body_priority_and_post_merge_rewrite_are_stable_and_account_client_scoped`；`Metadata_only_codex_and_claude_sessions_survive_recapture_without_rewriting_device_or_opaque_identity`；`Antigravity_overwrites_bridge_first_user_hash_with_positive_int63_from_original_client_seed`；`Structured_metadata_only_rewrites_known_fields_and_preserves_workbuddy_identity`；`Missing_http_seed_is_request_local_while_websocket_seed_is_connection_local` | 主要是服务级测试；provider+GUID/HMAC 合约证明身份字段，不能替代 token 轮换/429/各号池入口的完整调用点证明。 |
| owner request 形状/冲突/TTL/重启/故障 | `Proxy/UpstreamReferenceOwnerTests.Supported_original_request_forms_resolve_same_owner_and_namespace`；`Implicit_item_references_require_known_ownership_and_pin_the_same_owner`；`Concurrent_two_store_writers_cannot_last_win_and_conflict_survives_restart`；`Fixed_TTL_survives_restart_reads_and_same_owner_writes_do_not_renew`；`Persisted_ownership_resolves_from_independently_recreated_service_provider`；`Output_is_not_exposed_if_store_fails_and_error_does_not_leak_json_or_sql` | 独立 store/guard 与临时 DB；同文件包含 synthetic collision、inline 完整历史和错误 shape；不自动证明所有输出控制器都正确接钩。 |
| HTTP/SSE/WS 输出 owner 及禁止误投 | `Proxy/AccountSessionIsolationTests.Unknown_references_fail_before_any_account_is_probed`；`Http_native_response_is_owned_before_exposure_and_known_owner_pins_followup`；`Interrupted_response_created_is_owned_before_exposure_and_continuation_never_probes_fallback_account`；`Commit_failure_exposes_no_id_and_does_not_try_another_account`；`Websocket_exact_completed_reference_replays_full_local_history_but_mismatch_does_not_probe` | 真实 TestServer HTTP/WS+fake forwarder，部分 native 路由实际 owner 为普通 `site`，不能称已穷举两个 OAuth 账号所有生命周期。 |
| 桥接输出、快速 WS/head buffer/取消 | `Proxy/AccountSessionIsolationTests.Bridged_stream_synthetic_id_is_committed_before_exposure_and_rejected_for_native_replay`；`Head_buffer_release_commit_failure_exposes_no_unowned_id_and_never_falls_back`；`Completed_event_ownership_survives_cancellation_after_stream_callback`；`Websocket_reconnect_known_reference_has_no_replay_history_and_pipelined_stale_reference_is_rejected` | 使用 TestServer 与 fake forwarder 验证关键时序；不代表穷举所有真实网络断线情况。 |
| Web 合同、未加载状态与编辑部分失败 | `frontend/src/api/accountNetwork.test.ts`：`loads capabilities, sanitized options and all summaries without developer endpoints`、`never treats unloaded state as Legacy or an incomplete profile as valid`；`frontend/src/views/OAuthView.setup.test.ts`：`repairs a disabled profile before refreshing replacement credentials`、`reports network-saved/credentials-failed and retries credentials without repeating policy save`、`refreshes summary/options on CAS conflict, preserves dirty fields and blocks retry until explicit rebase` | API mocks/setup 级断言，不是浏览器 GUI；oauth/deviceOauth/proxyProfiles API tests 另验证缺字段、multipart、opaque session、数字 mode 及 Keep/Replace/Clear。 |
| Desktop wire contract | `tests/AITool.Desktop.Smoke/AccountNetworkSerialization.ps1` | 协调者报告已通过：反射已构建 Desktop 的实际 `ApiService.DeserializeResponse`，检查 Int64 proxy revision、string policy revision、numeric mode、GUID/null/bool/envelope；policy-before-credential 仅源码顺序 guard，非完整 UI 生命周期验收。 |

## 6. 部署与使用限制

- 未知 Google refresh token 导入时必须明确选择网络出口，避免首次请求误用已有账号的出口策略；身份不能验证或与已有账号冲突时不会覆盖该账号。
- 上游已经轮换 token 后，如果响应丢失、进程崩溃或数据库无法写入，本地事务无法撤销上游操作，可能需要重新授权。
- 普通站点的引用归属会绑定配置凭证及身份相关请求头；更换凭证后旧引用可能失效，需要新建会话。对未知动态身份头采取保守拒绝策略。
- Responses 会生成 `Session-Id` 并与 `prompt_cache_key` 对齐；其他会话别名仅在已有时改写。WorkBuddy 的设备身份头保持原样。
- Windows ACL、备份恢复和多机部署兼容需在实际部署环境验收；测试环境中的服务重建不等同于完整灾备演练。
- 隔离浏览器已验证账号模式展示及策略保存、刷新后持久化；视觉布局未作人工审阅，文件上传及被隔离环境阻断的外部账号操作未进行 GUI 验收。
