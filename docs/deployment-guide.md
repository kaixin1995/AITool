# AI-Tool 部署手册（双宿主 / 单进程 / AllInOne）

适用版本：split 分支 1.0.1.22+（含跨宿主共享密钥、可恢复流、中继、AllInOne）。

---

## 0. 三种部署形态速览

| 形态 | 进程 | 适应场景 | 更新方式 |
|------|------|----------|----------|
| **分离部署（推荐生产）** | Admin 本地 + Core 服务器 | 客户端直连 Core，跨境链路短 | 只更 Admin 不中断代理（Core 不动） |
| 中继模式 | Admin 本地 + Core 服务器 | 客户端全部就近连 Admin | 同上（加开 Relay:Enabled） |
| AllInOne 单进程 | 一个进程 | 单机全功能、懒人部署 | 整体重启（秒级） |

无论哪种形态，**先启 Admin（或 AllInOne）→ 再启 Core**（Core 首次启动等 Admin 下发全量配置；之后即使 Admin 长期离线，Core 独立服务不受影响——配置已落盘 `core-runtime/last-good-config.json`）。

---

## 1. 分离部署（Admin 本地 + Core 服务器）

### 1.1 产物准备（本机执行 publish.ps1）

```powershell
.\publish.ps1 -CoreLinux    # 产出 publish\AITool.Admin、publish\AITool.Core、publish\AITool.Core-linux-x64
```

- `AITool.Admin` / `AITool.Core`：本机/Windows 双进程形态（兄弟目录摆放，Core 抓包目录回退逻辑依赖）：
  放在**同一目录**下如 `C:\ai-tool\AITool.Admin` 与 `C:\ai-tool\AITool.Core`。
- `AITool.Core-linux-x64`：拷贝到 VPS（如 `/opt/aitool-core`），配合 `deploy/aitool-core.service.example` 开机自启。

### 1.2 Core 服务器配置

```bash
# 1) 安装 .NET 8 运行时 + systemd 单元
sudo apt install -y dotnet-runtime-8.0
sudo cp deploy/aitool-core.service.example /etc/systemd/system/aitool-core.service
sudo useradd -r -s /usr/sbin/nologin aitool   # 服务专用账户，收紧权限
sudo chown -R aitool:aitool /opt/aitool-core
sudo systemctl daemon-reload && sudo systemctl enable --now aitool-core

# 2) 共享密钥（必须！见第 3 节）：写入 /opt/aitool-core/appsettings.json
#   "CoreAuth": { "SharedSecret": "<随机长串>" }

# 3)（可选）反代 + HTTPS：deploy/Caddyfile 或 deploy/nginx.conf.example
#    Core 保持仅回环监听（127.0.0.1:5029）更安全；公网只暴露 443。
```

### 1.3 Admin 本地配置

```jsonc
// src/AITool.Admin/appsettings.json（或发布目录同名文件）
{
  "CoreServer": {
    "BaseUrl": "https://core.example.com/",   // 指向 Core（走 Caddy 的 https 域名；与 Core 同在本地则 http://127.0.0.1:5029/）
    "Port": 5029,
    "SharedSecret": "<与 Core 相同的随机长串>"
  }
}
```

### 1.4 客户端接入

- 默认：客户端直连 Core 域名（/v1/...，用原有 AccessKey）。
- 期望“代理从 Admin 走”：Admin `appsettings.json` 开 `"Relay": { "Enabled": true }`，客户端改指向 Admin（5030 端口）同一个 /v1 路径——断线自动续传（最多 3 次重连），客户端无感。

---

## 2. AllInOne 单进程部署

```powershell
.\publish.ps1 -AllInOne        # 产出 publish\AITool.AllInOne*
# 或服务端：拷贝 AITool.AllInOne-linux-x64，dotnet AITool.AllInOne.dll（端口默认 5030，可用 --AllInOneServer:Port 覆盖）
```

- 单端口同时服务 `/v1` 代理面 + 管理面 + 前端，无配对、无共享密钥（无跨宿主）。
- 事件与 OAuth 凭证刷新均为进程内直连：代理事件经内存总线直接入库（消费失败自动重试一次），
  凭证刷新经 DB-backed 配置提供器直写 SQLite。
- 数据库文件 `aitool.db` 落在程序目录，备份手册见第 5 节。

---

## 3. 跨宿主共享密钥（分离部署必配）

- Core 配置键：`CoreAuth:SharedSecret`；Admin：`CoreServer:SharedSecret`。**两端必须一致**。
- 生成：`openssl rand -hex 32`（64 字符）。
- 机制：Admin→Core 的 `/api/core/*` 全部要求 `X-Core-Auth` 头；SHA256+恒时比较。
- 未配置时的安全回落：`/api/core/*` 仅允许本机回环访问（同机部署开箱即用），远端一律 401。
- 密钥泄露处置：改两端配置 → 重启两进程。
- 传输安全：**跨公网必须走 HTTPS**（Caddy/Nginx 模板），密钥管认证、证书管加密，二者缺一不可。
- ⚠️ `appsettings.json` 中调试页默认 `Debug:KeyHash` 是公开默认值，任何公网部署前请更换（生成方式见文件内注释）。

---

## 4. 运维

### 4.1 健康探活

- `GET /health` 两宿主都有（匿名）。
- Admin 仪表盘首页显示 Core 在线/离线、配置版本、最近同步时间（每 10 秒轮询）。

### 4.2 升级

| 场景 | 操作 | 影响 |
|------|------|------|
| 只更新 Admin | 停 Admin → 替换目录 → 启 Admin | **代理零中断**（Core 独立服务；事件 spool 断线重放） |
| 只更新 Core | 停 Core → 替换 → 启 Core | 数十秒中断，建议低峰执行；客户端自动重试 |
| AllInOne | 整体重启 | 秒级 |

### 4.3 备份（最低清单）

- `aitool.db`（Admin/AllInOne 目录）——全部业务数据。
- `core-runtime/last-good-config.json`（Core 目录）——含全部上游 Key，**加密存放**。
- 可选：`frontend` 无独立数据。

### 4.4 敏感文件权限（Linux）

- Core 目录 `chmod 700` + `UMask=0077`（systemd 模板已配）：`last-good-config.json`（全部站点 Key）与事件 spool（完整对话报文）均为明文，仅服务账户可读。
- 数据库文件同理。

### 4.5 磁盘增长

- Core 事件 spool：按日轮转，最多保留 30 天或 60 文件（自动清理）。
- Admin 使用日志/对话记录：设置页可配保留天数与自动清理。

---

## 5. 故障排查

| 现象 | 排查 |
|------|------|
| Admin 仪表盘显示 “Core 离线（HttpRequestException）” | 1) 网络到 Core 是否通；2) 端口；3) 证书是否可信；4) Admin `CoreServer:BaseUrl` 拼写 |
| 仪表盘 “Core 离线（…401…）/ 配置推不上去” | 共享密钥不一致或未配置 |
| 客户端 401 `invalid_core_auth` | Core 侧密钥与 Admin 不一致 |
| 流式偶发中断、长时间无字断流 | SSE 心跳默认 15s 会兜住沉默期；若仍有断流检查反代 `proxy_read_timeout`/缓冲（模板已调优） |
| 升级后浏览器停留旧前端 | index.html 已改为协商缓存（no-cache），强刷一次即可 |
| Core 起不来、无配置 | Core 首次必须等 Admin 下发；检查 Admin 启动日志的握手/全量同步条目 |

---

## 6. 变更日志锚点（本手册对应功能）

- 跨宿主共享密钥（M1）
- SSE 直通头 / 心跳 / 可恢复流（X-Stream-Request-Id / X-Stream-Resume）（M3）
- Admin /v1 中继（Relay:Enabled）（M3）
- AllInOne 单进程宿主（M4）
- 仪表盘 Core 状态面板（M5）