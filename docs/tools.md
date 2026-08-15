# 构建脚本与工具链

> 本文是 [README.md](../README.md) 的工具链细节篇，覆盖仓库根目录的构建/发布脚本与 `tools/ProtocolSyncCheck`。

---

## 1. build.ps1 — 一键构建

```powershell
.\build.ps1
```

流程：`frontend/` 下 `npm install`（无 node_modules 时）→ `npm run build`（产物输出 `src/AITool.Web/wwwroot`）→ `dotnet build src/AITool.Web/AITool.Web.csproj`。
> 脚本结尾打印的访问地址文案是历史端口（5029），实际端口以 `appsettings.json` 的 `Server:Port`（当前默认 **15029**）为准。

## 2. publish.ps1 — 一键发布

```powershell
.\publish.ps1 [-TargetDir "D:\Tool\AiTool"]
```

五步流程：

1. **构建前端**：同 build.ps1（产物已在 wwwroot 内随后端一起发布）
2. **发布后端**：`dotnet publish src/AITool.Web/AITool.Web.csproj -c Release -o $env:TEMP\aitool-publish-{时间戳}`
3. **停旧进程**：检测 `AITool.Web.exe` 运行中则 `Stop-Process`（最多等 10s 退出）
4. **部署到目标目录**（保留数据库和配置）：
   - 备份 `appsettings.json` / `appsettings.Development.json` 到临时目录
   - 复制 dll/exe/pdb/json/config 与 `model-vendor-catalog.json`
   - 清空目标 `wwwroot/assets` + `index.html` 后复制新前端产物
   - **恢复 appsettings.json**（保留你的端口与密码配置）；`*.db` 数据库文件原地保留
5. **启动**：`Start-Process` 独立进程启动 `AITool.Web.exe`（脚本退出后保持运行），结尾从目标目录 appsettings 读取端口打印访问地址

---

## 3. tools/ProtocolSyncCheck — 协议同步检查工具

纯 net8.0 控制台程序（4 个源文件，无第三方依赖），用于离线比对 **AITool 与参考实现 CLIProxyAPI**（`reference-projects/CLIProxyAPI`，Go 语言）的协议端点与字段覆盖一致性。

### 命令行

```bash
dotnet run --project tools/ProtocolSyncCheck [--skip-pull] [仓库根目录]
```

- 第一个非 `--` 参数：仓库根目录路径（缺省从程序目录向上找含 `src/AITool.Web` 的目录）
- `--skip-pull`：跳过 `git pull`，只读 `reference-projects/CLIProxyAPI` 当前 HEAD（离线/无网环境）

### 工作流程（4 个阶段）

1. **拉取基准**（`GitPullHelper`）：对 `reference-projects/CLIProxyAPI` 执行 `git pull --ff-only`；失败回退从官方仓库 `https://github.com/router-for-me/CLIProxyAPI.git` 重试；记录基准 HEAD 短哈希与提交时间（写入报告「基准版本」）
2. **路由扫描**（`ProtocolScanner`）：正则扫描 AITool 三个代理控制器（`OpenAiProxyController.cs`、`OpenAiProxyController.Responses.cs`、`AnthropicProxyController.cs`）的 `[HttpXxx]` 特性，与 CLIProxyAPI 的 Gin 路由（`internal/api/server_routes.go`、`server.go`，Group 前缀拼接、`:modelId/:request_id/:task_id` 归一化）比对；内置 `ProtocolCatalog`（OpenAI 27 条 + Anthropic 4 条，分主协议/legacy/扩展）；**不在目录里的路由进入未分类路由扫描（UnclassifiedRoutes）**
3. **字段级对比**：`CpaFieldGroupBuilder` 从 Go handler/translator（gjson/sjson/字面量 map key 正则）提取 9 个字段基线分组（OpenAI Chat 请求/响应、legacy Completions、Responses 请求/响应、Anthropic Messages 请求/响应、Anthropic Models）；`CSharpFieldScanner` 扫描 `src/AITool.Protocol` 全部 .cs + `src/AITool.Web/Controllers/Proxy` + `ChatApiController.cs`（识别 indexer/Add/CopyIfPresent 透传/转换帮助方法/语义映射如 `reasoning_effort↔thinking`）；`FieldDiffEngine.ComputeDiffs` 产出每字段状态（Matched/PassThrough/BridgeHandled/SemanticHandled/DynamicHandled/Missing/TypeMismatch）
4. **生成报告**（`ProtocolReportBuilder.Build`）：写入 **`docs/protocol-sync-report.md`**（UTF-8 无 BOM）；控制台输出路由数、未跟踪路由数、字段分组/字段数、基准版本

> `docs/protocol-sync-report.md` 是生成物（`.gitignore` 显式忽略，不入库），每次运行覆盖。报告结构：运行信息 → 扫描前提异常 → 总览状态计数 → 协议接口状态表 → 双方未跟踪路由表 → 字段对比明细 → 排查结论（优先级判断）。

---

## 4. AITool.Desktop — Avalonia 桌面壳

`src/AITool.Desktop`（独立 csproj，在解决方案中）：基于 Avalonia 的桌面封装，通过 HTTP 调用与浏览器版完全相同的后端 API，不承载业务逻辑。开发计划背景见 `docs/avalonia-desktop-development-plan.md`。日常 Web 部署不涉及此项目。

---

## 5. 仓库其他目录速查

| 目录 | 用途 |
|------|------|
| `reference-projects/` | 参考实现（CLIProxyAPI 等），ProtocolSyncCheck 的比对基准；协议 URL 对照见 `docs/protocol-url-reference.md` |
| `src/AITool.Core/`、`src/AITool.Admin/`、`tests/AITool.Core.IntegrationTests/`、`tests/AITool.Admin.IntegrationTests/` | **历史构建残留**（只有 bin/obj，无源码、不在解决方案），split 分支双宿主架构实验遗留，可安全删除 |
| `.tmp-db/` | 一次 dotnet 构建故障排查的临时工作目录（构建诊断日志 + ProtocolSyncCheck 手工编译产物），与数据库无关，可安全删除 |
| `docs/codex-tasks/`、`docs/superpowers/` | Codex 开发任务归档与开发流程资料 |
