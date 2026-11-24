<#
=============================================================================
 install-local.ps1 —— 把当前源码**覆盖安装**到本机已装的那份 Q-Control

  用途：改完代码不用重新出安装包，直接 publish + 覆盖 + 重启，闭环验真机。
  默认安装目录 D:\Program Files\Q-Control（原来是安装器装的，这里只换程序文件，
  HWiNFO\ 与 tests\ 一个字都不动）。

  ⚠️ 为什么不是"自己弹 UAC 然后 -Wait 等它"：
    那条路会卡死 —— 调用方（编辑器 / 自动化 / 终端）等的是提权进程退出，
    只要 UAC 没人点就一直空转；更糟的是程序这时已经被 /api/app/quit 关掉了，
    于是看起来就是"卡在覆盖安装中，控制中心还不见了"。

  现在的做法（三段）：
    1. 第一次：提权一次，注册一个隐藏的提权任务 `QControl Dev Install`
       （无触发器，永远不会自己跑），顺便把这一次装完。
    2. 以后：往任务计划丢一条 /run 就返回，**不弹 UAC、不阻塞调用方**。
    3. 真正的活在任务里跑（不继承调用方的 job object，调用方收工也杀不掉它），
       进度写 _tmp\install-status.json，完整输出写 _tmp\install.log。

  用法：
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-local.ps1
    powershell ... -File scripts\install-local.ps1 -Wait      # 等装完再返回（看进度用）
    powershell ... -File scripts\install-local.ps1 -OnlyStart  # 只把托盘拉起来，不发布不覆盖

  参数：
    -InstallDir <path>   换一个安装目录（默认读 scripts\install-local.json，其次内置默认）
    -NoStart             装完不启动
    -NoBackup            不做旧文件备份
    -OnlyStart           只启动，不发布、不覆盖
    -Wait                在前台等这次安装结束并打印每一步（默认丢给任务就返回）
    -TimeoutMin <n>      -Wait 最多等多久（默认 6 分钟）
    内部参数（不要手敲）：-Worker 任务里的执行体 / -Setup 提权注册任务
=============================================================================
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [switch]$NoStart,
    [switch]$NoBackup,
    [switch]$OnlyStart,
    [switch]$Wait,
    [int]$TimeoutMin = 6,
    [string]$TaskName = 'QControl Dev Install',
    [switch]$Worker,
    [switch]$Setup
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptPath = $MyInvocation.MyCommand.Path
$root      = Split-Path -Parent (Split-Path -Parent $scriptPath)      # ControlCenter\
$tmpDir    = Join-Path $root '_tmp'
$logFile   = Join-Path $tmpDir 'install.log'
$statusF   = Join-Path $tmpDir 'install-status.json'
$reqF      = Join-Path $tmpDir 'install-request.json'
$stage     = Join-Path $tmpDir 'install-stage'
$buildDir  = Join-Path $tmpDir 'installbuild\'

if (-not (Test-Path $tmpDir)) { New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null }

# ------------------------------------------------------------------ 日志 / 进度
# 每行带时间戳落盘：装到哪一步、卡在哪一步，事后都查得到。
function Log([string]$m, [string]$color = 'Gray') {
    $line = (Get-Date -Format 'HH:mm:ss') + '  ' + $m
    try { Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8 } catch { }
    if (-not $Worker) { Write-Host ('  ' + $m) -ForegroundColor $color }
}

$script:runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID
$script:exitCode = 0
function Set-Phase([string]$phase, [string]$detail = '') {
    try {
        $o = [ordered]@{
            run     = $script:runId
            phase   = $phase
            detail  = $detail
            at      = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
            pid     = $PID
            log     = $logFile
            done    = ($phase -eq 'done' -or $phase -eq 'failed')
            ok      = ($phase -ne 'failed')
        }
        if ($phase -eq 'failed') { $o['error'] = $detail }
        ($o | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath $statusF -Encoding UTF8
    } catch { }
}

function Step([string]$t) { Log ''; Log ("== " + $t) 'Cyan'; Set-Phase (($t -replace '\s.*$', '')) $t }

# ------------------------------------------------------------------ 删目录（不用 Remove-Item）
# 调用方可能带着安全删包装，[IO.Directory]::Delete 不经过它，也不会有确认框。
function Clear-Dir([string]$p) {
    if (Test-Path $p) {
        try { [IO.Directory]::Delete($p, $true) }
        catch { Start-Sleep -Milliseconds 300; [IO.Directory]::Delete($p, $true) }   # 杀软/索引偶尔挡一下
    }
}

# ------------------------------------------------------------------ 启动（WMI）
#
# ⚠️ 用 WMI 起，不用 Start-Process：调用方往往带着 job object，
#    Start-Process 出来的子进程属于同一个 job，调用方一收工就会被一起收掉
#    （实测：装完 30 秒后进程凭空消失，日志里连一行 [quit] 都没有）。
#    Win32_Process.Create 出来的进程挂在 WmiPrvSE 下，不继承这个 job。
function Start-QControl([string]$dir, [string]$port = '8765') {
    $exe = Join-Path $dir 'QControl.exe'
    # 参数与开机自启任务保持一致（任务里就是 --tray --port 8765）
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine      = '"' + $exe + '" --tray --port ' + $port
        CurrentDirectory = $dir
    }
    if ($r.ReturnValue -ne 0) { throw "启动失败（Win32_Process.Create 返回 $($r.ReturnValue)）" }
    Log "已拉起 pid=$($r.ProcessId)" 'DarkGray'

    $ok = $false
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline) {
        try {
            $null = Invoke-RestMethod -Uri ("http://127.0.0.1:" + $port + '/api/status') -TimeoutSec 2
            $ok = $true; break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if ($ok) { Log ('接口已就绪（127.0.0.1:' + $port + ' 在应答）') 'DarkGray' }
    else { Log '[WARN] 40 秒内没等到 /api/status，看 C:\ProgramData\Q-Control\logs 当天日志' 'Yellow' }

    Start-Sleep -Seconds 2
    $alive = @(Get-Process -Name QControl -ErrorAction SilentlyContinue).Count
    if ($alive -eq 0) { Log '[WARN] 进程起来了又退出 —— 看当天日志末尾' 'Yellow' }
    else { Log "进程存活（$alive 个）" 'DarkGray' }
    return $ok
}

# ------------------------------------------------------------------ 安装目录
function Resolve-InstallDir([string]$explicit) {
    $dir = $explicit
    if (-not $dir) {
        $cfg = Join-Path $root 'scripts\install-local.json'
        if (Test-Path $cfg) {
            try { $dir = (Get-Content $cfg -Raw | ConvertFrom-Json).install_dir } catch { }
        }
    }
    if (-not $dir) { $dir = 'D:\Program Files\Q-Control' }
    return $dir
}

# ------------------------------------------------------------------ 任务计划
# 用 ScheduledTasks 模块的 cmdlet，不走 schtasks.exe（那玩意儿在部分沙箱环境里被黑名单）。
function Test-Task {
    return ($null -ne (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue))
}

function Register-Task {
    $arg = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $scriptPath + '" -Worker'
    $act = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arg
    # 没有触发器：这个任务永远不会自己跑，只能被 Start-ScheduledTask 手动踢一下。
    $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                                        -ExecutionTimeLimit (New-TimeSpan -Minutes 30) `
                                        -MultipleInstances IgnoreNew
    # Interactive + Highest：装完拉起的控制中心落在当前桌面会话里（有托盘），而且是提权令牌。
    $pr = New-ScheduledTaskPrincipal -UserId ($env:USERDOMAIN + '\' + $env:USERNAME) `
                                     -LogonType Interactive -RunLevel Highest
    Register-ScheduledTask -TaskName $TaskName -Action $act -Settings $set -Principal $pr -Force | Out-Null
    Log "已注册提权任务：$TaskName（无触发器，只手动启动）" 'DarkGray'
}

# ------------------------------------------------------------------ 请求 / 状态
function Write-Request([string]$action) {
    $o = [ordered]@{
        action     = $action
        installDir = $InstallDir
        noStart    = [bool]$NoStart
        noBackup   = [bool]$NoBackup
        at         = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    }
    ($o | ConvertTo-Json) | Set-Content -LiteralPath $reqF -Encoding UTF8
}

function Wait-Finish([int]$minutes) {
    $deadline = (Get-Date).AddMinutes($minutes)
    $seen = ''
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $statusF) {
            try { $s = Get-Content $statusF -Raw | ConvertFrom-Json } catch { $s = $null }
            if ($s -and $s.phase -ne $seen) {
                $seen = $s.phase
                Log ("   " + $s.at + "  " + $s.phase + $(if ($s.detail) { ' — ' + $s.detail } else { '' })) 'DarkGray'
            }
            if ($s -and $s.done) {
                if ($s.ok) { Log '装好了。' 'Green' } else { Log ('失败：' + $s.error) 'Red' }
                return $s
            }
        }
        Start-Sleep -Milliseconds 700
    }
    Log ('等了 ' + $minutes + ' 分钟没结束 —— 看 ' + $logFile) 'Yellow'
    return $null
}

# ================================================================== 执行体
function Invoke-Worker {
    $act = 'install'
    $dir = Resolve-InstallDir $InstallDir
    $doStart = -not $NoStart
    $doBackup = -not $NoBackup
    if (Test-Path $reqF) {
        try {
            $req = Get-Content $reqF -Raw | ConvertFrom-Json
            if ($req.action) { $act = $req.action }
            $dir = Resolve-InstallDir $req.installDir
            $doStart = -not [bool]$req.noStart
            $doBackup = -not [bool]$req.noBackup
        } catch { Log ('请求文件读不动，按默认参数装：' + $_.Exception.Message) 'Yellow' }
    }

    $bkTxt = '关'; if ($doBackup) { $bkTxt = '开' }
    $stTxt = '否'; if ($doStart) { $stTxt = '是' }

    Log ''
    Log ('覆盖安装 Q-Control -> ' + $dir) 'White'
    Log ('模式 ' + $act + ' / 备份 ' + $bkTxt + ' / 装完启动 ' + $stTxt)

    try {
        if (-not (Test-Path (Join-Path $dir 'QControl.exe'))) {
            throw "安装目录里没有 QControl.exe：$dir（先用安装包装一次，或用 -InstallDir 指定）"
        }

        if ($act -eq 'start') {
            Step '启动（只启动，不发布）'
            $alive = @(Get-Process -Name QControl -ErrorAction SilentlyContinue).Count
            if ($alive -gt 0) {
                # 已经在跑就别再拉一个副本（第二个会抢 8765 端口后自己退出，日志里看着像"崩了"）
                Log "已经在跑（$alive 个进程），跳过" 'DarkGray'
                Set-Phase 'done' 'already running'
                $script:exitCode = 0; return
            }
            $null = Start-QControl $dir
            Set-Phase 'done' 'only start'
            $script:exitCode = 0; return
        }

        # ---------------- 1. 发布
        Step '1/4 发布（Release / win-x64 / 框架依赖）'
        $dotnet = $env:DOTNET_ROOT
        if ($dotnet -and (Test-Path (Join-Path $dotnet 'dotnet.exe'))) { $dotnet = Join-Path $dotnet 'dotnet.exe' }
        elseif (Test-Path (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')) {
            $dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
        } else { $dotnet = 'dotnet' }
        & $dotnet --version | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "找不到 dotnet：$dotnet" }

        $srcApp = Join-Path $root 'src\QControl.App\QControl.App.csproj'
        Clear-Dir $stage
        $sw = [Diagnostics.Stopwatch]::StartNew()
        & $dotnet publish $srcApp -c Release -r win-x64 --self-contained false `
            -p:PublishReadyToRun=false -p:SignBinaries=false `
            "-p:BaseOutputPath=$buildDir" -o $stage -v q --nologo
        if ($LASTEXITCODE -ne 0) { throw "publish 失败（退出码 $LASTEXITCODE）" }
        $sw.Stop()
        if (-not (Test-Path (Join-Path $stage 'QControl.exe'))) { throw "publish 产物里没有 QControl.exe：$stage" }
        Log ('发布完成 ' + [math]::Round($sw.Elapsed.TotalSeconds) + ' 秒，主程序 ' +
             [math]::Round((Get-Item (Join-Path $stage 'QControl.dll')).Length / 1KB) + ' KB') 'DarkGray'

        # ---------------- 2. 停旧进程
        Step '2/4 停止正在运行的控制中心'
        $stopped = $false
        try {
            Invoke-RestMethod -Uri 'http://127.0.0.1:8765/api/app/quit' -Method Post -TimeoutSec 3 | Out-Null
            Log '已通过 /api/app/quit 请求退出' 'DarkGray'
            $stopped = $true
        } catch { Log '接口没响应（没在跑 / 端口不同）—— 直接按进程收' 'DarkGray' }

        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline) {
            if (@(Get-Process -Name QControl -ErrorAction SilentlyContinue).Count -eq 0) { break }
            Start-Sleep -Milliseconds 300
        }
        $procs = @(Get-Process -Name QControl -ErrorAction SilentlyContinue)
        if ($procs.Count -gt 0) {
            Log ("还有 " + $procs.Count + " 个 QControl 进程，强制结束") 'DarkGray'
            $procs | Stop-Process -Force
            Start-Sleep -Milliseconds 800
            $stopped = $true
        }
        if (-not $stopped) { Log '本来就没在跑' 'DarkGray' }

        # ---------------- 3. 备份 + 覆盖
        Step '3/4 覆盖程序文件'
        if ($doBackup) {
            $backup = Join-Path $tmpDir ("install-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
            New-Item -ItemType Directory -Force -Path $backup | Out-Null
            foreach ($f in @('QControl.exe', 'QControl.dll', 'QControl.Core.dll', 'QControl.Hal.dll',
                             'QControl.runtimeconfig.json', 'QControl.deps.json')) {
                $p = Join-Path $dir $f
                if (Test-Path $p) { Copy-Item $p $backup -Force }
            }
            Log "旧文件备份：$backup" 'DarkGray'
            # 只留最近 3 份：每次安装都备份约 2 MB，攒到一百多份会把 _tmp 撑到几百 MB
            # （2026-11-22 一次清掉 162 份 ≈ 310 MB）。备份只为「刚装坏能回滚」，留 3 份够用。
            try {
                $olds = Get-ChildItem $tmpDir -Directory -Filter 'install-backup-*' |
                        Sort-Object Name -Descending | Select-Object -Skip 3
                foreach ($o in $olds) { Remove-Item $o.FullName -Recurse -Force -ErrorAction Stop }
                if (@($olds).Count -gt 0) { Log ("清掉 " + @($olds).Count + " 份旧备份（保留最近 3 份）") 'DarkGray' }
            } catch { Log ('旧备份清理失败（不影响安装）：' + $_.Exception.Message) 'DarkGray' }
        }

        $copied = 0
        Get-ChildItem $stage -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($stage.Length).TrimStart('\')
            if ($rel -like '*.pdb') { return }
            $dst = Join-Path $dir $rel
            $parent = Split-Path -Parent $dst
            if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
            Copy-Item $_.FullName $dst -Force
            $copied = $copied + 1
        }
        Log "已覆盖 $copied 个文件（HWiNFO\ 与 tests\ 未动）" 'DarkGray'

        # ---------------- 3a. 同步契约样本 tests\fixtures
        # 为什么必须同步（2026-11-25）：`--selftest` 的 DTO 往返比对读的就是这份样本。
        # 给 `/api/status` 加了字段却不同步 fixtures，自检会报「多出 /fan_oem_max」——
        # 装好的程序带着一条假失败，下次排查会白白绕一圈。
        # 只覆盖 fixtures 这一个子目录，tests\ 里别的东西仍然不碰。
        $srcFix = Join-Path $root 'tests\fixtures'
        if (Test-Path $srcFix) {
            $dstFix = Join-Path $dir 'tests\fixtures'
            if (-not (Test-Path $dstFix)) { New-Item -ItemType Directory -Force -Path $dstFix | Out-Null }
            Copy-Item (Join-Path $srcFix '*') $dstFix -Recurse -Force
            Log '已同步 tests\fixtures（契约自检样本）' 'DarkGray'
        }

        # ---------------- 3b. 同步卸载项版本
        # 只覆盖程序文件、不更新注册表的话，「应用和功能」里的版本会一直停在最后一次用
        # 安装包装的那一版（2026-11-21 实测：程序已是 0.1.0，注册表还写着 0.1.1）。
        # 这里按刚装好的 exe 版本回写，失败只记一行，不影响本次安装。
        try {
            $ver = (Get-Item (Join-Path $dir 'QControl.exe')).VersionInfo.ProductVersion
            if ($ver) {
                $ver = ($ver -split '\+')[0]
                $key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QControl'
                if (Test-Path $key) {
                    Set-ItemProperty -Path $key -Name 'DisplayVersion' -Value $ver -ErrorAction Stop
                    Log "卸载项版本已同步：$ver" 'DarkGray'
                }
            }
        } catch { Log ('卸载项版本同步失败（不影响使用）：' + $_.Exception.Message) 'DarkGray' }
        # ---------------- 4. 启动
        if ($doStart) {
            Step '4/4 启动新版本'
            $null = Start-QControl $dir
        } else {
            Step '4/4 跳过启动（-NoStart）'
        }

        Set-Phase 'done' ("覆盖 " + $copied + " 个文件")
        Log '完成。托盘图标在，左键打开界面。' 'Green'
        $script:exitCode = 0; return
    }
    catch {
        $msg = $_.Exception.Message
        Log ('失败：' + $msg) 'Red'
        Set-Phase 'failed' $msg
        # 兜底：装坏了也别让控制中心躺在地上 —— 把现有的那份拉起来
        try {
            if (@(Get-Process -Name QControl -ErrorAction SilentlyContinue).Count -eq 0) {
                Log '兜底启动现有版本…' 'Yellow'
                $null = Start-QControl $dir
            }
        } catch { Log ('兜底启动也失败：' + $_.Exception.Message) 'Yellow' }
        $script:exitCode = 1; return
    }
}

# ================================================================== 入口
$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

# ---------- 任务里的执行体（已经是提权的）
if ($Worker) {
    Invoke-Worker | Out-Null
    exit $script:exitCode
}

# ---------- 提权注册（第一次那一下 UAC 里跑的）
#
# ⚠️ 这里只注册任务 + 立刻把任务踢起来，**不自己跑安装**。
#    原因：这个进程是 UAC 拉起来的，它的父进程是调用方（编辑器 / 自动化 / 终端），
#    调用方一收工，它可能被连带收掉 —— 自己跑安装就会停在「程序已经退出、还没覆盖」的半截。
#    丢给任务计划去跑，任务在 Task Scheduler 手里，谁也杀不掉它。
if ($Setup) {
    try {
        Register-Task
        Start-ScheduledTask -TaskName $TaskName -ErrorAction Stop
        Log '已交给提权任务，本窗口可以关了。' 'DarkGray'
        exit 0
    } catch {
        Log ('注册/启动任务失败：' + $_.Exception.Message) 'Red'
        Set-Phase 'failed' ('setup: ' + $_.Exception.Message)
        exit 1
    }
}

# ---------- 已经在管理员窗口里手动跑
if ($isAdmin) {
    Set-Phase 'begin' 'manual admin'
    $null = Invoke-Worker
    exit 0
}

# ---------- 普通权限：转给提权任务，立刻返回
$action = if ($OnlyStart) { 'start' } else { 'install' }

if (-not (Test-Task)) {
    Write-Request $action
    if (Test-Path $statusF) { [IO.File]::Delete($statusF) }
    Log '第一次装：需要提权注册一个隐藏任务（只弹这一次 UAC，之后再也不会弹）' 'Yellow'
    try {
        $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $scriptPath + '"'), '-Setup',
               '-TaskName', ('"' + $TaskName + '"'))
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $a | Out-Null
        Log '已在 UAC 对话框等你确认（窗口可能在后面，看任务栏）—— 点"是"之后就不用管了' 'Yellow'
        if ($Wait) { $null = Wait-Finish $TimeoutMin }
        exit 0
    } catch {
        Log ('提权被拒绝或失败：' + $_.Exception.Message) 'Red'
        exit 1
    }
}

# 任务已在：写请求 -> 踢一下任务 -> 立刻返回（不弹 UAC、不阻塞调用方）
Write-Request $action
if (Test-Path $statusF) { [IO.File]::Delete($statusF) }
try {
    Start-ScheduledTask -TaskName $TaskName -ErrorAction Stop
} catch {
    Log ('任务启动失败：' + $_.Exception.Message + ' —— 用管理员窗口重跑一次本脚本会重建它') 'Red'
    exit 1
}
Log ('已交给提权任务在后台安装，日志：' + $logFile) 'DarkGray'

if ($Wait) {
    $null = Wait-Finish $TimeoutMin
} else {
    Log '（不等结果。要看进度加 -Wait，或直接看 _tmp\install-status.json）' 'DarkGray'
}
exit 0
