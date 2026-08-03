# AI-Tool 发布脚本（Windows PowerShell）
# 用法：.\publish.ps1
# 效果：构建前端 → 构建后端 → 发布到指定路径（保留数据库和配置）
# 默认发布路径 D:\Tool\AiTool，可通过参数覆盖：.\publish.ps1 -TargetDir "D:\Other\Path"

param(
    [string]$TargetDir = "D:\Tool\AiTool"
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$TempPubDir = Join-Path $env:TEMP "aitool-publish-$(Get-Date -Format 'yyyyMMddHHmmss')"

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "  AI-Tool 发布脚本" -ForegroundColor Cyan
Write-Host "  目标路径：$TargetDir" -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""

# ==========================================
# 1. 构建前端
# ==========================================
Write-Host "=== 1/4 构建前端 ===" -ForegroundColor Cyan
Push-Location (Join-Path $ScriptDir "frontend")
try {
    if (-not (Test-Path node_modules)) {
        Write-Host "未检测到 node_modules，执行 npm install..."
        npm install
        if ($LASTEXITCODE -ne 0) { throw "npm install 失败" }
    }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "前端构建失败" }
    Write-Host "✓ 前端构建完成" -ForegroundColor Green
} finally {
    Pop-Location
}

# ==========================================
# 2. 发布后端（Release 模式，输出到临时目录）
# ==========================================
Write-Host ""
Write-Host "=== 2/4 发布后端 ===" -ForegroundColor Cyan
dotnet publish (Join-Path $ScriptDir "src\AITool.Web\AITool.Web.csproj") -c Release -o $TempPubDir --nologo
if ($LASTEXITCODE -ne 0) { throw "后端发布失败" }
Write-Host "✓ 后端发布完成" -ForegroundColor Green

# ==========================================
# 3. 部署到目标路径（保留数据库和配置）
# ==========================================
Write-Host ""
Write-Host "=== 3/4 部署到 $TargetDir ===" -ForegroundColor Cyan

# 创建目标目录（首次发布）
if (-not (Test-Path $TargetDir)) {
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
    Write-Host "创建目标目录：$TargetDir"
}

# 停止正在运行的程序（如果有的话）
$runningProc = Get-Process -Name "AITool.Web" -ErrorAction SilentlyContinue
if ($runningProc) {
    Write-Host "停止正在运行的 AITool.Web.exe ..."
    Stop-Process -Name "AITool.Web" -Force
    Start-Sleep -Seconds 2
}

# 备份配置文件（发布后恢复，避免覆盖你的端口和密码）
$appsettingsPath = Join-Path $TargetDir "appsettings.json"
$appsettingsDevPath = Join-Path $TargetDir "appsettings.Development.json"
$backupDir = Join-Path $env:TEMP "aitool-config-backup"
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

$configBackedUp = $false
if (Test-Path $appsettingsPath) {
    Copy-Item $appsettingsPath (Join-Path $backupDir "appsettings.json") -Force
    $configBackedUp = $true
}
if (Test-Path $appsettingsDevPath) {
    Copy-Item $appsettingsDevPath (Join-Path $backupDir "appsettings.Development.json") -Force
}

# 复制程序文件
Write-Host "复制程序文件..."
Copy-Item (Join-Path $TempPubDir "*.dll") $TargetDir -Force
Copy-Item (Join-Path $TempPubDir "*.exe") $TargetDir -Force
Copy-Item (Join-Path $TempPubDir "*.pdb") $TargetDir -Force
Copy-Item (Join-Path $TempPubDir "model-vendor-catalog.json") $TargetDir -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $TempPubDir "*.json") $TargetDir -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $TempPubDir "*.config") $TargetDir -Force -ErrorAction SilentlyContinue

# 复制运行时依赖（e_sqlite3.dll 等）
Get-ChildItem (Join-Path $TempPubDir "*.dll") | ForEach-Object {
    Copy-Item $_.FullName $TargetDir -Force
}

# 更新前端（wwwroot）
Write-Host "更新前端资源..."
$wwwrootPath = Join-Path $TargetDir "wwwroot"
if (-not (Test-Path $wwwrootPath)) {
    New-Item -ItemType Directory -Path $wwwrootPath -Force | Out-Null
}
# 清除旧的前端资源
Remove-Item (Join-Path $wwwrootPath "assets") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $wwwrootPath "index.html") -Force -ErrorAction SilentlyContinue
# 复制新的前端资源
Copy-Item (Join-Path $TempPubDir "wwwroot\*") $wwwrootPath -Recurse -Force

# 恢复配置文件
if ($configBackedUp) {
    Copy-Item (Join-Path $backupDir "appsettings.json") $appsettingsPath -Force
    Write-Host "✓ 已恢复 appsettings.json（保留你的端口和密码配置）" -ForegroundColor Green
}

# 确保数据库文件不被覆盖（发布产物里不包含 .db，但以防万一）
Get-ChildItem $TargetDir -Filter "*.db" | ForEach-Object {
    Write-Host "保留数据库：$($_.Name)" -ForegroundColor Yellow
}

Write-Host "✓ 部署完成" -ForegroundColor Green

# ==========================================
# 4. 验证启动
# ==========================================
Write-Host ""
Write-Host "=== 4/4 验证启动 ===" -ForegroundColor Cyan
Write-Host "尝试启动程序（5秒后自动关闭，仅验证能否正常启动）..."
$exePath = Join-Path $TargetDir "AITool.Web.exe"
if (Test-Path $exePath) {
    $proc = Start-Process $exePath -PassThru
    Start-Sleep -Seconds 5
    if (-not $proc.HasExited) {
        Write-Host "✓ 程序启动成功" -ForegroundColor Green
        Stop-Process -Id $proc.Id -Force
    } else {
        Write-Host "⚠ 程序启动后立即退出，请检查日志" -ForegroundColor Yellow
    }
} else {
    Write-Host "⚠ 未找到 AITool.Web.exe" -ForegroundColor Yellow
}

# 清理临时目录
Remove-Item $TempPubDir -Recurse -Force -ErrorAction SilentlyContinue

# ==========================================
# 完成
# ==========================================
Write-Host ""
Write-Host "=========================================" -ForegroundColor Green
Write-Host "  发布完成！" -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Green
Write-Host "目标路径：$TargetDir"
Write-Host "启动：双击 AITool.Web.exe 或在终端运行 cd '$TargetDir'; .\AITool.Web.exe"
$port = if (Test-Path $appsettingsPath) {
    (Get-Content $appsettingsPath | ConvertFrom-Json).Server.Port
} else { "5029" }
Write-Host "访问：http://localhost:$port"
Write-Host ""
Write-Host "按任意键退出..." -ForegroundColor Yellow
[void][System.Console]::ReadKey($true)
