# Avalonia 桌面前端开发计划

## 一、决策汇总

| 决策项 | 选择 |
|---|---|
| 部署模式 | 纯客户端（HTTP API 连接远程后端，后端零改动） |
| 功能范围 | 全功能管理端（图表页面 Analytics/ModelHealth 暂不做，用占位页） |
| UI 组件库 | Semi.Avalonia |
| MVVM 框架 | CommunityToolkit.Mvvm |
| 对话 SSE | 原生 HttpClient 手动解析（HttpClient + ReadAsStream + 双换行切块） |
| .NET 版本 | net8.0（与现有后端项目一致） |

---

## 二、项目结构

新建 `src/AITool.Desktop/`，加入 `AiTool.slnx`。

```
src/AITool.Desktop/
├── AITool.Desktop.csproj             # net8.0 + Avalonia
├── Program.cs                        # Avalonia 入口
├── App.axaml / App.axaml.cs          # 应用入口 + DI 容器 + 全局样式
├── ViewModels/
│   ├── ViewModelBase.cs              # ObservableObject 基类
│   ├── MainWindowViewModel.cs        # 导航容器（侧边栏 + 内容区）
│   ├── AuthViewModel.cs              # 登录/首次设置
│   ├── DashboardViewModel.cs
│   ├── SitesViewModel.cs
│   ├── ModelsViewModel.cs
│   └── ...（每个页面一个 ViewModel）
├── Views/
│   ├── MainWindow.axaml              # 主窗口（侧边栏导航 + 内容区）
│   ├── LoginView.axaml
│   ├── DashboardView.axaml
│   └── ...（每个 ViewModel 一个 View）
├── Services/
│   ├── ApiService.cs                 # HttpClient 封装（baseURL + token + ApiResponse 解包 + 401 刷新）
│   ├── TokenStore.cs                 # token 持久化（本地设置文件）
│   ├── SseClient.cs                  # SSE 流式客户端（HttpClient + ReadAsStream）
│   └── NavigationService.cs          # 页面导航
├── Models/
│   ├── ApiResponse.cs                # 统一响应信封 {success, data, message, errorCode}
│   ├── AuthModels.cs                 # TokenPair / AuthStatus / LoginRequest / SetupRequest
│   └── ...（各 API 模块的 DTO，从 frontend/src/api/*.ts 映射）
├── Converters/                       # 值转换器
├── Assets/                           # 图标/图片
└── appsettings.json                  # 客户端配置（默认服务端地址等）
```

**依赖关系**：`AITool.Desktop` 只引用 Avalonia + Semi.Avalonia + CommunityToolkit.Mvvm 相关 NuGet 包。**不引用任何后端项目**（纯客户端模式，通过 HTTP API 通信）。

---

## 三、技术栈与 NuGet 包

| 包 | 版本 | 用途 |
|---|---|---|
| Avalonia | 11.x | 框架主体 |
| Avalonia.Desktop | 11.x | 桌面平台支持（Windows/macOS/Linux） |
| Avalonia.Themes.Fluent | 11.x | 基础主题 |
| Semi.Avalonia | 11.x | UI 组件库（按钮/输入框/表格/对话框等） |
| CommunityToolkit.Mvvm | 8.x | MVVM（源生成器 ObservableObject + RelayCommand） |
| Microsoft.Extensions.DependencyInjection | 8.x | DI 容器 |

---

## 四、核心基础设施（第一批实现）

### 4.1 ApiService —— HTTP 客户端封装

复刻 `frontend/src/api/http.ts` 的全部逻辑：

- **可配置 BaseUrl**：从本地设置文件读取（首次启动让用户输入服务端地址，如 `http://192.168.1.100:15029`，持久化到本地）
- **Token 自动注入**：每个请求自动加 `Authorization: Bearer {accessToken}`
- **ApiResponse 自动解包**：检测 `{success, data, message, errorCode}` 格式，成功返回 `.data`，失败抛 `ApiException(message, errorCode, statusCode)`。非标准格式原样返回。
- **401 自动刷新**：access token 过期（15 分钟）时用 refresh token 调 `POST /api/auth/refresh`，成功后重发原请求。
  - **并发保护**：多个 401 共享同一次刷新（模块级 `SemaphoreSlim` 或 `Lazy<Task>`）
  - 刷新失败：清除 token，导航到登录页
- **skipErrorNotify 选项**：部分请求（轮询、可选功能探测）不弹全局错误提示

### 4.2 TokenStore —— Token 持久化

- AccessToken / RefreshToken 存到本地设置文件
  - Windows: `%AppData%/AITool/settings.json`
  - Linux/macOS: `~/.config/aitool/settings.json`（用 `Environment.GetFolderPath(SpecialFolder.ApplicationData)`）
- 启动时自动恢复

### 4.3 SseClient —— SSE 流式客户端

复刻 `frontend/src/api/chat.ts` 的 `sendChatStream`：

- `HttpClient.PostAsync` + `HttpCompletionOption.ResponseHeadersRead`
- 逐行读取 `ReadAsStream`，按双换行（`\n\n`）切分 SSE block
- 每个 block 解析 `event:` 行（确定事件类型）+ `data:` 行（JSON payload）
- 事件类型：`token`（增量文本）、`reasoning`（推理过程）、`meta`（元数据）、`done`（完成）、`error`（错误）
- 通过回调/IObservable 分发事件

### 4.4 NavigationService —— 页面导航

- 维护当前页面 ViewModel
- 功能开关：根据 `/api/auth/status` 返回的 `features.{codexEnabled, codexInspectionEnabled, developerEnabled}` 控制菜单可见性
- 页面切换时正确清理/初始化

### 4.5 MainWindow —— 主窗口布局

复刻 `frontend/src/layouts/MainLayout.vue` 的侧边栏 + 内容区布局：

- **侧边栏**：菜单项列表（可折叠），选中态高亮
- **内容区**：根据选中菜单切换 View + ViewModel
- **功能开关**：Codex 菜单仅在 `codexEnabled` 时显示，开发者工具仅在 `developerEnabled` 时显示

---

## 五、认证流程（复刻 frontend/src/stores/auth.ts + views/LoginView.vue）

1. 启动时调 `GET /api/auth/status` 获取 `AuthStatus { hasPassword, isAuthenticated, features }`
2. `hasPassword === false` → 显示首次设置页（输入密码 + 确认密码）
3. `hasPassword === true && !已登录` → 显示登录页（输入密码）
4. 登录/设置成功 → `POST /api/auth/login` 或 `/api/auth/setup` → 获取 `TokenPair` → 存 token → 进入主界面
5. token 过期 → ApiService 自动刷新 → 刷新失败 → 回登录页

---

## 六、页面实现计划（按复杂度分四批）

### 第一批：基础设施 + 认证

| # | 页面 | 对应前端 | 复杂度 | 说明 |
|---|---|---|---|---|
| 1 | Program / App | — | — | Avalonia 入口 + DI 容器 |
| 2 | ApiService / TokenStore / SseClient | http.ts / chat.ts | — | HTTP 基础设施 |
| 3 | NavigationService | router/index.ts | — | 页面导航 |
| 4 | MainWindow | MainLayout.vue | 中 | 侧边栏 + 内容区 |
| 5 | LoginView | LoginView.vue | 易 | 登录/首次设置 |

### 第二批：简单页面（CRUD + 表格）

| # | 页面 | 对应前端 | 复杂度 | 说明 |
|---|---|---|---|---|
| 6 | DashboardView | DashboardView.vue | 中 | 统计卡片 + 核心同步状态 |
| 7 | AccessKeysView | AccessKeysView.vue | 中 | 密钥 CRUD + 路由勾选 |
| 8 | SystemSettingsView | SystemSettingsView.vue | 中 | 19 字段设置表单 |
| 9 | UsageLogsView | UsageLogsView.vue | 难 | 24 字段大表格 + 筛选 + 详情 |
| 10 | DetectionTasksView | DetectionTasksView.vue | 中 | cron 任务 CRUD |

### 第三批：中等复杂度页面

| # | 页面 | 对应前端 | 复杂度 | 说明 |
|---|---|---|---|---|
| 11 | SitesView | SitesView.vue | 难 | 站点 CRUD + fetch-all 异步轮询 |
| 12 | ModelsView | ModelsView.vue | 难 | 厂商分组卡片 + 映射管理 |
| 13 | RoutesView | RoutesView.vue | 难 | 路由规则优先级 + 时间段编辑 |
| 14 | CompatibilityView | CompatibilityView.vue | 中 | profile CRUD + JSON 编辑 |
| 15 | DetectionView | DetectionView.vue | 中-难 | 模型×站点矩阵 + 探测轮询 |

### 第四批：复杂页面

| # | 页面 | 对应前端 | 复杂度 | 说明 |
|---|---|---|---|---|
| 16 | ChatView | ChatTestPane.vue | 难 | SSE 流式对话 + Markdown 渲染 |
| 17 | DeveloperInvocationsView | DeveloperInvocationsView.vue | 难 | trace 分页 + 详情 + 并发监视 |
| 18 | CodexView | CodexView.vue | 极难 | OAuth loopback + 账号管理 + 巡检 |

### 暂不做（占位页，后续补）

| 页面 | 原因 |
|---|---|
| AnalyticsView | 依赖 ECharts 图表，后续用 LiveCharts2/OxyPlot 重写 |
| ModelHealthView | 依赖 ECharts 时间线图表 |
| RouteFallbackView | 依赖汇总数据 |
| CircuitBreakerTab | 调试子页 |
| ClientSimulator | 调试子页 |

---

## 七、API 契约（从 frontend/src/api/*.ts 映射）

### 认证（`/api/auth`）
- `GET /status` → AuthStatus（无需 token）
- `POST /login` body `{password}` → TokenPair
- `POST /setup` body `{password, confirmPassword}` → TokenPair
- `POST /logout` body `{refreshToken}` → void

### 管理后台（`/api/admin/*`，JWT 认证）

| 模块 | 路由前缀 | 主要操作 |
|---|---|---|
| Dashboard | `/api/admin/dashboard` | GET stats |
| Analytics | `/api/admin/analytics` | GET dashboard / options（暂不做） |
| Chat | `/api/admin/chat` | GET models/targets, POST send/send-stream（SSE） |
| Sites | `/api/admin/sites` | CRUD + import/export + fetch-models |
| SiteCatalog | `/api/admin/site-catalog` | fetch-all-models + progress + import-selected |
| Models | `/api/admin/models` | CRUD + vendor-catalog + mappings |
| RouteRules | `/api/admin/route-rules` | entries + list/save + discover-sites |
| RouteFallback | `/api/admin/route-fallback` | list + summary |
| AccessKeys | `/api/admin/access-keys` | CRUD + toggle + update-routes |
| Detection | `/api/admin/detection` | matrix + probe + progress |
| DetectionTasks | `/api/admin/detection-tasks` | CRUD + toggle + execute |
| ModelHealth | `/api/admin/model-health` | dashboard + monitor（暂不做） |
| Codex | `/api/admin/codex` | accounts CRUD + OAuth + inspection + import/export |
| Developer | `/api/admin/developer/invocations` | init + list + detail + concurrency + circuit-breaker |
| CompatibilityProfiles | `/api/admin/compatibility-profiles` | CRUD + toggle |
| UsageLogs | `/api/admin/usage-logs` | filters + list + summary + request-detail |
| SystemSettings | `/api/admin/system` | GET/PUT settings + clear-usage-logs |

### 统一响应格式

```json
{
  "success": true,
  "data": { ... },
  "message": null,
  "errorCode": null
}
```

成功时 ApiService 自动返回 `data`；失败时抛 `ApiException(message, errorCode, statusCode)`。

### TokenPair

```json
{
  "accessToken": "...",
  "refreshToken": "...",
  "accessTokenExpiresAt": "2026-08-05T...",
  "refreshTokenExpiresAt": "2026-08-12T..."
}
```

### AuthStatus

```json
{
  "hasPassword": true,
  "isAuthenticated": false,
  "features": {
    "codexEnabled": true,
    "codexInspectionEnabled": false,
    "developerEnabled": false
  }
}
```

---

## 八、SSE 协议（对话流式）

后端用命名事件（非 OpenAI 的 `[DONE]`），每个 SSE block 格式：

```
event: token
data: {"content":"你好"}

event: reasoning
data: {"content":"思考中..."}

event: meta
data: {"attempts":[...]}

event: done
data: {}

event: error
data: {"message":"上游错误","attempts":[...]}
```

SseClient 解析逻辑：
1. 按 `\n\n` 切分 block
2. 每个 block 内：`event:` 行取事件类型，`data:` 行取 JSON payload
3. 分发：token→onToken(content)、reasoning→onReasoning(content)、meta→onMeta(payload)、done→onDone()、error→onError(message)

---

## 九、后端不改

纯客户端模式，后端代码完全不改。桌面端通过 HTTP API 连接已部署的 Web 后端（默认端口 15029）。

---

## 十、配置

桌面端首次启动需要用户配置服务端地址（如 `http://192.168.1.100:15029`），存到本地设置文件。后续启动自动恢复。

设置文件结构：

```json
{
  "serverUrl": "http://192.168.1.100:15029",
  "accessToken": "...",
  "refreshToken": "..."
}
```

---

## 十一、发布

Avalonia 支持跨平台发布：

```bash
# Windows 单文件
dotnet publish src/AITool.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# Linux 单文件
dotnet publish src/AITool.Desktop -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true

# macOS
dotnet publish src/AITool.Desktop -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
```

---

## 十二、参考文件清单

| 参考文件 | 用途 |
|---|---|
| `frontend/src/api/http.ts` | HTTP 封装逻辑（token 注入、ApiResponse 解包、401 刷新、skipErrorNotify） |
| `frontend/src/api/chat.ts` | SSE 流式解析逻辑 |
| `frontend/src/api/auth.ts` | 认证 API 契约 |
| `frontend/src/stores/auth.ts` | 认证状态管理逻辑 |
| `frontend/src/types/api.ts` | 核心类型定义 |
| `frontend/src/layouts/MainLayout.vue` | 侧边栏导航布局 |
| `frontend/src/views/LoginView.vue` | 登录/首次设置页面 |
| `frontend/src/router/index.ts` | 路由表 + 功能开关守卫 |
| `AiTool.slnx` | 解决方案文件（需加入新项目） |
