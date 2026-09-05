# AI-Tool 发布脚本（Windows PowerShell）— split 双宿主 + AllInOne 版
# 用法：
#   .\publish.ps1                    # 默认：Admin/Core（win-x64）
#   .\publish.ps1 -CoreLinux         # 额外发布 Core linux-x64（部署到国外 VPS）
#   .\publish.ps1 -AllInOne          # 额外发布 AllInOne 单进程宿主（win/linux）
#   .\publish.ps1 -Output <目录>     # 自定义输出目录（默认 publish\）
# 效果：构建前端 → 发布 Admin（含前端静态产物）→ Core（代理运行时）→ AllInOne。

param(
    [string]$Output = "publish",
    [switch]$CoreLinux,
    [switch]$AllInOne
)

$ErrorActionPreference = "Stop"

Write-Host "=== 1. 构建前端 ===" -ForegroundColor Cyan
Push-Location frontend
try {
    if (-not (Test-Path node_modules)) {
        npm install
    }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "前端构建失败" }
} finally {
    Pop-Location
}

$adminOut = Join-Path $Output "AITool.Admin"
$coreOut = Join-Path $Output "AITool.Core"

Write-Host "=== 2. 发布 Admin 宿主（管理端 + 前端静态产物）===" -ForegroundColor Cyan
dotnet publish src/AITool.Admin/AITool.Admin.csproj -c Release -o $adminOut
if ($LASTEXITCODE -ne 0) { throw "Admin 发布失败" }

Write-Host "=== 3. 发布 Core 宿主（代理运行时）===" -ForegroundColor Cyan
dotnet publish src/AITool.Core/AITool.Core.csproj -c Release -o $coreOut
if ($LASTEXITCODE -ne 0) { throw "Core 发布失败" }

if ($CoreLinux) {
    Write-Host "=== 4. 发布 Core linux-x64（VPS 部署）===" -ForegroundColor Cyan
    dotnet publish src/AITool.Core/AITool.Core.csproj -c Release -r linux-x64 --self-contained false -o (Join-Path $Output "AITool.Core-linux-x64")
    if ($LASTEXITCODE -ne 0) { throw "Core linux 发布失败" }
}

if ($AllInOne) {
    Write-Host "=== 5. 发布 AllInOne 单进程宿主（win-x64 / linux-x64）===" -ForegroundColor Cyan
    dotnet publish src/AITool.AllInOne/AITool.AllInOne.csproj -c Release -o (Join-Path $Output "AITool.AllInOne")
    if ($LASTEXITCODE -ne 0) { throw "AllInOne 发布失败" }
    dotnet publish src/AITool.AllInOne/AITool.AllInOne.csproj -c Release -r linux-x64 --self-contained false -o (Join-Path $Output "AITool.AllInOne-linux-x64")
    if ($LASTEXITCODE -ne 0) { throw "AllInOne linux 发布失败" }
}

Write-Host "发布完成：" -ForegroundColor Green
Write-Host "  $adminOut          — Admin 宿主（默认 5030，托管前端，连接 SQLite）"
Write-Host "  $coreOut           — Core 宿主（默认 5029，/v1 代理入口，无数据库）"
Write-Host "  publish\AITool.AllInOne* — 单进程形态（默认 5030，/v1 + 管理面同端口）"
Write-Host "部署提示：分离形态两目录放同一机器兄弟位置（Core 抓包目录指向 Admin 实现跨宿主可见），"
Write-Host "         先启 Admin 再启 Core（Core 首次等配置下发）；AllInOne 单进程无需配对。"
Write-Host "安全提示：跨机部署前必须在 Admin/Core 两侧配置一致的共享密钥（CoreAuth:SharedSecret / CoreServer:SharedSecret）。"