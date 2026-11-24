# 关闭 USB 选择性暂停 / USB 设备省电关闭 —— 缓解「空闲或睡眠唤醒后 USB 全部掉线」
#
# 现象：闲置一段时间（或睡眠唤醒）后，所有 U 盘 / 鼠标 / 键盘 / 手机全部识别不到，
#       只能用笔记本内置键盘和触摸板重启才恢复。典型原因是 xHCI 控制器或某个 USB 集线器
#       在选择性挂起后没有正确恢复，把整条总线拖死。
#
# 用法：管理员 PowerShell 执行  powershell -ExecutionPolicy Bypass -File fix-usb-suspend.ps1
#       改完重启一次生效。想恢复默认：把下面三个注册表值改回 1、选择性暂停改回 1。

$ErrorActionPreference = 'Stop'

$logDir = Join-Path $PSScriptRoot '..\_tmp'
if (Test-Path $logDir) {
    try { Start-Transcript -Path (Join-Path $logDir 'usb-fix.log') -Force | Out-Null } catch { }
}

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$pr = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host '需要管理员权限：请从“以管理员身份运行”的 PowerShell 里执行本脚本。' -ForegroundColor Red
    exit 1
}

# ---- 1. USB 选择性暂停（当前电源计划，交流 + 直流都关）----
$sub = '2a737441-1930-4402-8d77-b2bebba308a3'
$set = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'
powercfg /setacvalueindex SCHEME_CURRENT $sub $set 0 | Out-Null
powercfg /setdcvalueindex SCHEME_CURRENT $sub $set 0 | Out-Null
powercfg /setactive SCHEME_CURRENT | Out-Null
Write-Host 'USB 选择性暂停：已禁用（交流 + 直流）' -ForegroundColor Green

# ---- 2. 每个 USB 设备参数键：关掉“允许计算机关闭此设备以节约电源” ----
$count = 0
Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\USB' -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -eq 'Device Parameters' } |
    ForEach-Object {
        foreach ($name in 'EnhancedPowerManagementEnabled', 'SelectiveSuspendEnabled', 'AllowIdleIrpInD3') {
            try {
                New-ItemProperty -Path $_.PSPath -Name $name -Value 0 -PropertyType DWord -Force -ErrorAction Stop | Out-Null
            } catch {
                # 某些设备的键是只读的 / 由驱动接管，跳过即可
            }
        }
        $count++
    }
Write-Host "USB 设备参数键：已处理 $count 个" -ForegroundColor Green

Write-Host ''
Write-Host '改完了，重启一次生效。' -ForegroundColor Yellow
Write-Host '如果重启后仍然出现 USB 全部掉线，下一步是改“合盖 = 休眠”并检查 BIOS 的 USB 相关设置。' -ForegroundColor Yellow

try { Stop-Transcript | Out-Null } catch { }
