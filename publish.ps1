# AI-Tool 发布脚本（Windows PowerShell）
# 用法：.\publish.ps1
# 效果：构建前端 -> 构建后端 -> 停旧进程 -> 部署 -> 启动新进程 -> 脚本退出后程序保持运行
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
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    if (-not (Test-Path node_modules)) {
        Write-Host "未检测到 node_modules，执行 npm install..."
        npm install
        if ($LASTEXITCODE -ne 0) { throw "npm install 失败" }
    }
    npm run build
    $buildExitCode = $LASTEXITCODE
    $ErrorActionPreference = $prevEAP
    if ($buildExitCode -ne 0) { throw "前端构建失败" }
    Write-Host "[OK] 前端构建完成" -ForegroundColor Green
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
Write-Host "[OK] 后端发布完成" -ForegroundColor Green

# ==========================================
# 3. 停止正在运行的程序
# ==========================================
Write-Host ""
Write-Host "=== 3/5 停止旧程序 ===" -ForegroundColor Cyan
$wasRunning = $false
$procsToStop = @()

$exeProcs = Get-Process -Name "AITool.Web" -ErrorAction SilentlyContinue
if ($exeProcs) { $procsToStop += $exeProcs }

try {
    $dotnetProcs = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*AITool.Web.dll*" }
    foreach ($dp in $dotnetProcs) {
        $p = Get-Process -Id $dp.ProcessId -ErrorAction SilentlyContinue
        if ($p) { $procsToStop += $p }
    }
} catch {}

if ($procsToStop.Count -gt 0) {
    $wasRunning = $true
    foreach ($p in $procsToStop) {
        Write-Host "检测到程序正在运行（PID: $($p.Id), Name: $($p.ProcessName)），正在停止..."
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    # 等待进程完全退出，最多等 10 秒
    $waited = 0
    while ($waited -lt 10) {
        Start-Sleep -Seconds 1
        $waited++
        $remaining = @()
        foreach ($p in $procsToStop) {
            if (Get-Process -Id $p.Id -ErrorAction SilentlyContinue) {
                $remaining += $p
            }
        }
        if ($remaining.Count -eq 0) { break }
    }
    Write-Host "[OK] 旧程序已停止" -ForegroundColor Green
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

# 备份配置文件（发布后恢复，避免覆盖用户的端口、密码和数据库配置）
$appsettingsPath = Join-Path $TargetDir "appsettings.json"
$appsettingsDevPath = Join-Path $TargetDir "appsettings.Development.json"
$headerProfilesPath = Join-Path $TargetDir "client-header-profiles.json"
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
if (Test-Path $headerProfilesPath) {
    Copy-Item $headerProfilesPath (Join-Path $backupDir "client-header-profiles.json") -Force
}

# 复制程序文件
Write-Host "复制程序文件..."
Copy-Item (Join-Path $TempPubDir "*.dll") $TargetDir -Force
Copy-Item (Join-Path $TempPubDir "*.exe") $TargetDir -Force
Copy-Item (Join-Path $TempPubDir "*.pdb") $TargetDir -Force
Copy-Item (Join-Path $TempPubDir "model-vendor-catalog.json") $TargetDir -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $TempPubDir "client-header-profiles.json") $TargetDir -Force -ErrorAction SilentlyContinue
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

# 同步 SQL 迁移脚本（供 developer/invocations#developerSqlMigrationsPane 执行）
$sqlMigrationsSrc = Join-Path $ScriptDir "sql-migrations"
$sqlMigrationsDst = Join-Path $TargetDir "sql-migrations"
if (Test-Path $sqlMigrationsSrc) {
    if (-not (Test-Path $sqlMigrationsDst)) {
        New-Item -ItemType Directory -Path $sqlMigrationsDst -Force | Out-Null
    }
    Copy-Item (Join-Path $sqlMigrationsSrc "*.sql") $sqlMigrationsDst -Force
    Write-Host "[OK] 同步 SQL 迁移脚本（sql-migrations）" -ForegroundColor Green
}

# 恢复配置文件
if ($configBackedUp) {
    Copy-Item (Join-Path $backupDir "appsettings.json") $appsettingsPath -Force
    if (Test-Path (Join-Path $backupDir "appsettings.Development.json")) {
        Copy-Item (Join-Path $backupDir "appsettings.Development.json") $appsettingsDevPath -Force
    }
    if (Test-Path (Join-Path $backupDir "client-header-profiles.json")) {
        Copy-Item (Join-Path $backupDir "client-header-profiles.json") $headerProfilesPath -Force
    }
    Write-Host "[OK] 已恢复 appsettings.json 及环境配置（保留端口、密码和数据库配置）" -ForegroundColor Green
}

# 确认数据库文件保留
Get-ChildItem $TargetDir -Filter "*.db" | ForEach-Object {
    Write-Host "保留数据库：$($_.Name)" -ForegroundColor Yellow
}

Write-Host "[OK] 部署完成" -ForegroundColor Green

# 清理临时目录
Remove-Item $TempPubDir -Recurse -Force -ErrorAction SilentlyContinue

# ==========================================
# 5. 启动程序（独立进程，脚本退出后保持运行）
# ==========================================
Write-Host ""
Write-Host "=== 5/5 启动程序 ===" -ForegroundColor Cyan
$exePath = Join-Path $TargetDir "AITool.Web.exe"
if (Test-Path $exePath) {
    $proc = Start-Process $exePath -WorkingDirectory $TargetDir -PassThru -WindowStyle Normal
    Start-Sleep -Seconds 3
    if (-not $proc.HasExited) {
        Write-Host "[OK] 程序已启动（PID: $($proc.Id)），将持续运行" -ForegroundColor Green
    } else {
        Write-Host "[WARN] 程序启动后立即退出，请手动运行检查错误信息" -ForegroundColor Yellow
    }
} else {
    Write-Host "[WARN] 未找到 AITool.Web.exe，请检查部署" -ForegroundColor Yellow
}

# ==========================================
# 完成
# ==========================================
$port = "5029"
if (Test-Path $appsettingsPath) {
    try { $port = (Get-Content $appsettingsPath -Raw | ConvertFrom-Json).Server.Port } catch {}
}

Write-Host ""
Write-Host "=========================================" -ForegroundColor Green
Write-Host "  发布完成！" -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Green
Write-Host "目标路径：$TargetDir"
if ($proc) {
    Write-Host "程序 PID：$($proc.Id)（独立进程，脚本关闭后持续运行）"
}
Write-Host "访问地址：http://localhost:$port"
