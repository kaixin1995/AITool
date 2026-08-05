# AI-Tool 一键构建脚本（Windows PowerShell）
# 用法：.\build.ps1
# 效果：构建前端 → 构建后端，产物输出到 src/AITool.Web/wwwroot

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
Write-Host "前端构建完成，产物已输出到 src/AITool.Web/wwwroot" -ForegroundColor Green

Write-Host ""
Write-Host "=== 2. 构建后端 ===" -ForegroundColor Cyan
dotnet build src/AITool.Web/AITool.Web.csproj
if ($LASTEXITCODE -ne 0) { throw "后端构建失败" }
Write-Host "后端构建完成" -ForegroundColor Green

Write-Host ""
Write-Host "=== 构建全部完成 ===" -ForegroundColor Green
Write-Host "运行：cd src/AITool.Web; dotnet run"
Write-Host "访问：http://localhost:5029"

Write-Host ""
Write-Host "按任意键退出..." -ForegroundColor Yellow
[void][System.Console]::ReadKey($true)
