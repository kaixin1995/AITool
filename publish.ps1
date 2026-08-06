# AI-Tool 发布脚本（Windows PowerShell）
# 用法：.\publish.ps1
# 效果：构建前端 → 构建后端 → 停旧进程 → 部署 → 启动新进程 → 脚本退出后程序保持运行
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
Write-Host "=== 1/5 构建前端 ===" -ForegroundColor Cyan
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
Write-Host "=== 2/5 发布后端 ===" -ForegroundColor Cyan
dotnet publish (Join-Path $ScriptDir "src\AITool.Web\AITool.Web.csproj") -c Release -o $TempPubDir --nologo
if ($LASTEXITCODE -ne 0) { throw "后端发布失败" }
Write-Host "✓ 后端发布完成" -ForegroundColor Green

# ==========================================
# 3. 停止正在运行的程序
# ==========================================
Write-Host ""
Write-Host "=== 3/5 停止旧程序 ===" -ForegroundColor Cyan
$wasRunning = $false
$runningProc = Get-Process -Name "AITool.Web" -ErrorAction SilentlyContinue
if ($runningProc) {
    $wasRunning = $true
    Write-Host "检测到 AITool.Web.exe 正在运行（PID: $($runningProc.Id)），正在停止..."
    Stop-Process -Name "AITool.Web" -Force
    # 等待进程完全退出，最多等 10 秒
    $waited = 0
    while ($waited -lt 10) {
        Start-Sleep -Seconds 1
        $waited++
        $stillRunning = Get-Process -Name "AITool.Web" -ErrorAction SilentlyContinue
        if (-not $stillRunning) { break }
    }
    $stillRunning = Get-Process -Name "AITool.Web" -ErrorAction SilentlyContinue
    if ($stillRunning) {
        throw "无法停止 AITool.Web.exe，请手动关闭后重试"
    }
    Write-Host "✓ 旧程序已停止" -ForegroundColor Green
} else {
    Write-Host "未检测到正在运行的程序，跳过" -ForegroundColor Gray
}

# ==========================================
# 4. 部署到目标路径（保留数据库和配置）
# ==========================================
Write-Host ""
Write-Host "=== 4/5 部署到 $TargetDir ===" -ForegroundColor Cyan

# 创建目标目录（首次发布）
if (-not (Test-Path $TargetDir)) {
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
    Write-Host "创建目标目录：$TargetDir"
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

# 更新前端（wwwroot）
Write-Host "更新前端资源..."
$wwwrootPath = Join-Path $TargetDir "wwwroot"
if (-not (Test-Path $wwwrootPath)) {
    New-Item -ItemType Directory -Path $wwwrootPath -Force | Out-Null
}
Remove-Item (Join-Path $wwwrootPath "assets") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $wwwrootPath "index.html") -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $TempPubDir "wwwroot\*") $wwwrootPath -Recurse -Force

# 恢复配置文件
if ($configBackedUp) {
    Copy-Item (Join-Path $backupDir "appsettings.json") $appsettingsPath -Force
    Write-Host "✓ 已恢复 appsettings.json（保留端口和密码配置）" -ForegroundColor Green
}

# 确认数据库文件保留
Get-ChildItem $TargetDir -Filter "*.db" | ForEach-Object {
    Write-Host "保留数据库：$($_.Name)" -ForegroundColor Yellow
}

Write-Host "✓ 部署完成" -ForegroundColor Green

# 清理临时目录
Remove-Item $TempPubDir -Recurse -Force -ErrorAction SilentlyContinue

# ==========================================
# 5. 启动程序（独立进程，脚本退出后保持运行）
# ==========================================
Write-Host ""
Write-Host "=== 5/5 启动程序 ===" -ForegroundColor Cyan
$exePath = Join-Path $TargetDir "AITool.Web.exe"
if (Test-Path $exePath) {
    # 用 Start-Process 启动独立进程，不绑定到当前 PowerShell 窗口
    $proc = Start-Process $exePath -WorkingDirectory $TargetDir -PassThru -WindowStyle Normal
    Start-Sleep -Seconds 3
    if (-not $proc.HasExited) {
        Write-Host "✓ 程序已启动（PID: $($proc.Id)），将持续运行" -ForegroundColor Green
    } else {
        Write-Host "⚠ 程序启动后立即退出，请手动运行检查错误信息" -ForegroundColor Yellow
    }
} else {
    Write-Host "⚠ 未找到 AITool.Web.exe，请检查部署" -ForegroundColor Yellow
}

# ==========================================
# 完成
# ==========================================
# 读取端口号
$port = "5029"
if (Test-Path $appsettingsPath) {
    try { $port = (Get-Content $appsettingsPath -Raw | ConvertFrom-Json).Server.Port } catch {}
}

Write-Host ""
Write-Host "=========================================" -ForegroundColor Green
Write-Host "  发布完成！" -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Green
Write-Host "目标路径：$TargetDir"
Write-Host "程序 PID：$($proc.Id)（独立进程，脚本关闭后持续运行）"
Write-Host "访问地址：http://localhost:$port"
Write-Host ""
Write-Host "按任意键退出..." -ForegroundColor Yellow
[void][System.Console]::ReadKey($true)
