# AI-Tool 发布脚本（Windows PowerShell）— split 双宿主版
# 用法：.\publish.ps1 [-Output <目录>]（默认 publish\）
# 效果：构建前端 → 发布 Admin（含前端静态产物）与 Core（代理运行时）到独立目录，可整体拷贝部署。

param(
    [string]$Output = "publish"
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

Write-Host "发布完成：" -ForegroundColor Green
Write-Host "  $adminOut  — Admin 宿主（默认 5030，托管前端，连接 SQLite）"
Write-Host "  $coreOut   — Core 宿主（默认 5029，/v1 代理入口，无数据库）"
Write-Host "部署：两目录放同一机器兄弟位置（Core 抓包目录指向 Admin 实现跨宿主可见），先启 Core 再启 Admin。"
