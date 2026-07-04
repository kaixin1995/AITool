# AITool - Codex OAuth 管理开发上下文

**文档用途**：在新会话中快速恢复开发上下文

**最后更新**：2026-07-03 22:30

---

## 当前工作分支

```
分支名：feature/codex-oauth-accounts
提交数：22 commits
远程同步：✅ 已推送（Gitea + GitHub）
状态：所有任务已完成，可合并到 main
```

---

## 最近完成的工作（今天）

### 1. 架构重构：巡检独立页面（da5551b）
- **问题**：巡检 Tab 布局错乱，CSS 冲突
- **解决**：
  - 新建独立页面：`Inspection.cshtml` + `Inspection.cshtml.cs`
  - 主页面改为 iframe 嵌入
  - 删除主页面 110+ 行代码
  - 完全隔离 CSS，避免样式冲突
- **路由**：
  - `/Admin/Codex` - 主页面
  - `/Admin/Codex?tab=inspection` - 主页面直接打开巡检 tab
  - `/Admin/Codex/Inspection` - 独立巡检页面

### 2. 导出凭证 API 补充（da5551b）
- **问题**：点击"导出凭证"报错 `Unexpected end of JSON input`
- **原因**：后端 API 端点不存在
- **修复**：
  - 新增端点：`POST /api/admin/codex/accounts/export-credentials`
  - 请求：`{ accountIds: [Guid] }`
  - 响应：`{ credentials: [{ account_id, email, access_token, ... }] }`

### 3. UI 优化：按钮改为图标（928ae70）
- 卡片按钮改为图标按钮（36×36px）
- 一行显示 5 个图标：刷新 / 启用禁用 / 编辑 / 拉取模型 / 删除
- hover 显示 title 文字提示
- 导航图标从 emoji 🔐 改为 SVG 图标

### 4. 修复巡检布局（2d0d319）
- 添加巡检 Tab 专属 CSS 样式
- 卡片、表格、hover 效果优化
- 暖色调背景 #f7f4ef / #fbf9f5

### 5. 彻底修复巡检被挤到最右边（最新）
- **真正根因**不是 iframe 本身，而是 `Pages/Admin/Codex/Index.cshtml` 里 Tab 结构存在多余的 `</div>`，导致主页面 DOM 结构错乱。
- 修复：删除账号额度 pane 末尾多余的两层闭合 `div`，恢复正确的 `tab-content -> tab-pane` 层级。
- 结果：巡检 iframe 正常显示在内容区内，不再被挤压到右侧窄列。
- 验证：编译 0 错误，集成测试 177/177 通过。

### 5. 其他优化
- 复选框移到卡片左上角（66b3a44）
- 进度条改为显示剩余额度（e683dc8）
- 卡片按钮布局重设计：Grid 自适应（773ee40）

---

## 项目架构

### 技术栈
- **后端**：ASP.NET Core 8.0, Entity Framework Core
- **前端**：Razor Pages, Bootstrap 5.3.0
- **数据库**：SQLite (开发) / PostgreSQL / MySQL (生产)
- **认证**：OAuth 2.0 (PKCE)

### 核心文件

#### 后端
```
src/AITool.Web/Controllers/Admin/CodexApiController.cs
├── OAuth 登录：start-oauth, complete-oauth
├── 凭证导入：import-credential
├── 账号管理：accounts, toggle, delete, update
├── 额度查询：refresh-quota, reset-quota
├── 模型拉取：fetch-models, import-selected-models
├── 巡检：inspection/run, inspection/status, inspection/last-run
├── Reset Credits：reset-credits, consume-reset-credit
└── 导出凭证：export-credentials ⭐ 新增

src/AITool.Application/Codex/
├── CodexAccountProvisioner.cs - 账号创建/更新/删除
├── ICodexQuotaService.cs - 额度查询（30s 缓存）
├── ICodexResetCreditsService.cs - 手动重置额度
└── CodexInspectionService.cs - 巡检服务

src/AITool.Infrastructure/Codex/
├── CodexOAuthClient.cs - OAuth 登录
├── CodexApiClient.cs - 上游 API 调用
└── CodexModelFetcher.cs - 模型拉取（性能优化：O(n²) → O(n)）

src/AITool.Domain/Codex/
└── CodexAccount.cs - 账号实体
```

#### 前端
```
src/AITool.Web/Pages/Admin/Codex/
├── Index.cshtml - 主页面（账号额度管理）
├── Index.cshtml.cs - 主页面 PageModel
├── Inspection.cshtml - 巡检独立页面 ⭐ 新建
└── Inspection.cshtml.cs - 巡检 PageModel ⭐ 新建

src/AITool.Web/Pages/Shared/
└── _Layout.cshtml - 导航（含 OAuth 管理入口）
```

#### 文档
```
docs/codex-tasks/
├── 17-export-and-ui-polish.md - 导出 + UI 精细打磨
└── (其他 16 个任务文档)
```

---

## 数据库模型

### CodexAccount 表
```csharp
public class CodexAccount : EntityBase
{
    public string DisplayName { get; set; }          // 显示名称
    public string? Email { get; set; }               // 邮箱
    public string? AccountId { get; set; }           // Codex account_id
    public string? PlanType { get; set; }            // free / pro / team
    public bool IsEnabled { get; set; }              // 是否启用
    
    // OAuth Token
    public string AccessToken { get; set; }          // 访问令牌
    public string RefreshToken { get; set; }         // 刷新令牌
    public string? IdToken { get; set; }             // ID 令牌
    public DateTimeOffset? TokenExpiresAt { get; set; }
    
    // 额度缓存（30s TTL）
    public bool IsQuotaCooling { get; set; }         // 是否冷却中
    public DateTimeOffset? QuotaCoolingUntil { get; set; }
    public string? LastQuotaRawJson { get; set; }    // 上游响应原文
    public DateTimeOffset? LastQuotaCheckedAt { get; }
    
    // 自动禁用
    public decimal? AutoDisableThreshold { get; set; } // 阈值（如 0.95）
    
    // 关联
    public Guid LinkedSiteId { get; set; }           // 隐藏 Site（复用路由）
}
```

---

## 功能清单

### ✅ 已实现功能

1. **OAuth 登录**
   - PKCE 流程（state + code_verifier）
   - 10 分钟会话超时
   - 自动创建隐藏 Site + 路由规则

2. **凭证导入**
   - 支持单文件 / 多文件上传
   - JSON 格式解析（access_token / refresh_token / id_token）
   - 自动提取 email / plan_type

3. **账号管理**
   - 列表展示（卡片布局）
   - 启用 / 禁用切换
   - 编辑（当前仅名称；自动禁用阈值已上移为系统级全局配置）
   - 删除（级联删除 Site + 映射 + 路由）

4. **额度查询**
   - 30 秒缓存（避免频繁请求）
   - 进度条显示（5 小时 / 周）
   - 反转颜色规则（剩余额度，越高越绿）

5. **模型拉取**
   - 预览上游模型列表
   - 可选导入（多选）
   - 性能优化：Dictionary Join（O(n²) → O(n)）

6. **巡检服务**
   - 手动巡检 / 真实巡检
   - 根据额度自动启用 / 禁用账号
   - 自动禁用阈值为系统级全局配置（Admin/System/Settings → Codex 巡检）
   - 巡检日志 + 账号明细
   - 5 秒自动轮询（页面隐藏时停止）

7. **Reset Credits**
   - 查询剩余次数 + 过期时间
   - 消耗 credit 执行真实重置

8. **导出凭证** ⭐ 新增
   - 批量导出选中账号的凭证
   - JSON 格式下载

---

## 待办事项

### 🎯 当前无待办

所有已知 Bug 和优化任务已完成。

### 📋 可选优化（低优先级）

1. **性能优化**（仅账号 > 100 时需要）
   - formatTime() 添加缓存
   - 卡片渲染使用 DocumentFragment
   - 虚拟滚动

2. **导出功能增强**
   - 使用 JSZip 打包成单个 ZIP 文件
   - 避免浏览器连续下载弹窗

3. **巡检页面优化**
   - 添加图表可视化（额度趋势）
   - 导出巡检报告

---

## 常见问题

### Q1: 如何运行项目？
```bash
cd C:/Users/kaikai.hao/Desktop/AI-Tool
dotnet run --project src/AITool.Web
```

### Q2: 如何运行测试？
```bash
dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj
```

### Q3: 如何切换分支？
```bash
git checkout feature/codex-oauth-accounts
```

### Q4: 如何查看最近提交？
```bash
git log --oneline -10
```

### Q5: 巡检布局还是错乱？
- **已修复**：使用独立页面 + iframe 嵌入，CSS 完全隔离

### Q6: 导出凭证报错？
- **已修复**：后端 API 已补充（`POST /api/admin/codex/accounts/export-credentials`）

---

## 下一步建议

### 如果继续开发：

1. **合并到 main 分支**
   ```bash
   git checkout main
   git merge feature/codex-oauth-accounts
   git push
   ```

2. **创建新分支（如有新功能）**
   ```bash
   git checkout -b feature/codex-<新功能名>
   ```

3. **查看性能分析报告**
   - 位置：`docs/codex-tasks/17-export-and-ui-polish.md`
   - 包含完整的性能分析和优化建议

### 如果新开窗口恢复工作：

1. **读取本文档**
   ```
   阅读 C:\Users\kaikai.hao\Desktop\AI-Tool\docs\session-context.md
   ```

2. **检查分支状态**
   ```bash
   git status
   git log --oneline -5
   ```

3. **告诉 AI 你要做什么**
   ```
   我要继续优化 Codex OAuth 管理功能
   ```

---

## 提交历史（最近 10 个）

```
da5551b - 架构重构：巡检独立页面 + 导出 API 补充 + URL 参数支持
2d0d319 - 修复巡检 Tab 页布局错乱
928ae70 - UI 优化：按钮改为图标 + 导航图标更换 + 重新添加导出 JS
773ee40 - 卡片按钮布局重设计：Grid 自适应 + 精致样式
3c309f6 - 文档：补充按钮布局重设计章节
51c3521 - 文档：补充复选框位置优化章节
66b3a44 - 导出模式：复选框移到卡片左上角
0fe319d - 文档：Task 17 - Codex 凭证导出 + UI 精细打磨（完整版）
562d3c8 - Merge branch 'feature/codex-oauth-accounts' of ...
c2f380b - 修复 newapi 流式 usage 中 output_tokens=0 和 cached_tokens 丢失
```

---

## 联系信息

- **仓库**：
  - Gitea: http://192.168.3.150:3000/kaixin1995/AI-Tool.git
  - GitHub: https://github.com/kaixin1995/AITool.git
- **用户**：kaixin1995
- **开发环境**：Windows 10, Git Bash, .NET 8.0

---

## 快速命令

```bash
# 编译
dotnet build src/AITool.Web/AITool.Web.csproj -v minimal

# 测试
dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj

# 运行
dotnet run --project src/AITool.Web

# 提交
git add -A
git commit -m "消息"
git push

# 查看状态
git status
git log --oneline -10
git diff
```

---

**✅ 文档已创建！在新窗口中，直接告诉 AI："读取 docs/session-context.md"，即可快速恢复上下文。**
