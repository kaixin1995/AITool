# AI-Tool 一键构建脚本（Windows PowerShell）— split 双宿主版
# 用法：.\build.ps1
# 效果：构建前端 → 构建解决方案（Admin/Core/Protocol/Desktop），前端产物输出到 src/AITool.Admin/wwwroot

$ErrorActionPreference = "Stop"

Write-Host "=== 1. 构建前端 ===" -ForegroundColor Cyan
Push-Location frontend
try {
    if (-not (Test-Path node_modules)) {
        Write-Host "未检测到 node_modules，执行 npm install..."
        npm install
    }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "前端构建失败" }
} finally {
    Pop-Location
}
Write-Host "前端构建完成，产物已输出到 src/AITool.Admin/wwwroot" -ForegroundColor Green

Write-Host "=== 2. 构建后端（双宿主 + 桌面端）===" -ForegroundColor Cyan
dotnet build AiTool.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw "后端构建失败" }

Write-Host "构建完成：Admin（管理宿主，静态托管前端）/ Core（代理运行时）/ Desktop（Avalonia 客户端）" -ForegroundColor Green
