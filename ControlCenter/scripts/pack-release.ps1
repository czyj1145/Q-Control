<#
=============================================================================
 pack-release.ps1 —— 出两个可分发产物（安装版 + 免安装版）

   dist\<ver>\QControl-<ver>-setup-win-x64.exe       安装包（约 2 MB）
   dist\<ver>\QControl-<ver>-portable-win-x64.zip    免安装绿色版（约 1 MB）
   dist\<ver>\SHA256.txt                            两个产物的校验和
   dist\<ver>\pack.log                              这次打包的完整日志

★ 一个版本一个文件夹（2026-11-24）：产物按版本落在 dist\<ver>\，旧版本文件夹
   不删不改。以前所有版本平铺在 dist\ 根上，翻哪个是当前版只能看时间戳。

调用方式：跑 src\pack-release.cmd（它会先解析 dotnet 再进来）。
参数：
   -SkipPublish   复用 dist\portable\Q-Control（已经发布过就省一次发布）
   -SkipSign      跳过签名（没证书 / 只想快速验证打包链路时）

★ 2026-10-28 起：**框架依赖**发布，两个产物都不再自带 .NET 运行时。
   之前是自包含（138 MB 安装 / 132 MB 安装包 / 62 MB 绿色版），体积是原厂
   NL5\ControlCenter（18 MB）的七八倍；真正的原因是 .NET 运行时 + WPF 约 135 MB，
   调参数（R2R / 裁剪 / 压缩）最多省 25 MB，压不动。
   现在：主程序 2 MB，缺 .NET 10 桌面运行时由安装器自动下载安装（见 Runtime.cs）。
   安装器本身是 net48（Windows 自带），所以它自己也不带运行时。

★ 2026-10-29 起：绿色版也"会自己补运行时"了（见第 2d / 5 步）。
   用户报的是"双击绿色版直接打不开" —— 因为 zip 里那两个 exe 都是框架依赖，
   缺运行时的时候只会一闪而过，连缺什么都看不到。现在绿色版里多了一个 net48 的
   引导器 `Q-Control.exe`（源码 src\QControl.Boot），主程序整套收进 app\：
   根上只有一个 exe 可点，缺运行时它会提示并**自动下载安装**，装完再拉起主程序。

顺序很重要，别随手调换：
   发布 → 去掉 pdb → 编引导器 → 签名（主程序 + 引导器）→ 打 payload
   → 组装绿色版 → 打绿色版 → 编安装包（嵌 payload）→ 签安装包 → 算哈希
签名必须在打包**之前**：zip 里的 exe 一旦压进去就签不动了。
=============================================================================
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [switch]$SkipSign
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)   # ControlCenter\
$src = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'portable\Q-Control'
$bootOut = Join-Path $dist '_boot'
$bundleRoot = Join-Path $dist '_bundle'
$bundle = Join-Path $bundleRoot 'Q-Control'
$payloadDir = Join-Path $dist '_payload'
$payloadZip = Join-Path $payloadDir 'payload.zip'
$setupOut = Join-Path $dist 'setup'

$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION 必须写成三段版本号（如 0.1.0），现在是：'$version'"
}

$outDir = Join-Path $dist $version                    # dist\<ver>\：本版本自己的文件夹
$portableZip = Join-Path $outDir "QControl-$version-portable-win-x64.zip"
$setupExe = Join-Path $outDir "QControl-$version-setup-win-x64.exe"
$hashFile = Join-Path $outDir 'SHA256.txt'

$dotnet = $env:DOTNET_EXE
if ([string]::IsNullOrWhiteSpace($dotnet)) { $dotnet = 'dotnet' }

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Step([string]$text) {
    Write-Host ""
    Write-Host "  ==  $text" -ForegroundColor Cyan
}

function Info([string]$text) { Write-Host "      $text" -ForegroundColor DarkGray }

function Invoke-Dotnet([string[]]$arguments, [string]$what) {
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "$what 失败（dotnet 退出码 $LASTEXITCODE）" }
}

function Format-Mb([long]$bytes) { return ('{0:N0} MB' -f ($bytes / 1MB)) }

Write-Host ""
Write-Host "  Q-Control $version —— 打包安装版 + 免安装版" -ForegroundColor White
Info "dotnet : $dotnet"
Info "输出   : $outDir"

# ---------------------------------------------------------------- 清场
Step '清理旧产物'
# ⚠️ 只清**本版本**的产物和中间目录（dist\portable、_boot、_bundle、_payload、setup）。
#    别的版本文件夹一律不碰 —— 一个版本一个文件夹就是为了留档。
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
foreach ($p in @($portableZip, $setupExe, $hashFile, (Join-Path $outDir 'pack.log'), $payloadZip)) {
    if (Test-Path $p) { Remove-Item $p -Force; Info "删除 $([System.IO.Path]::GetFileName($p))" }
}
if (Test-Path $dist) {
    foreach ($p in @($setupOut, (Join-Path $payloadDir 'portable'), (Join-Path $dist '_silent'), $bootOut, $bundleRoot)) {
        if (Test-Path $p) { Remove-Item $p -Recurse -Force }
    }
}
New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null

# 控制台代码页多是 936，中文进别的终端/工具里容易花掉；
# 顺手留一份 UTF-8 的 <ver>\pack.log（出问题时让人把这一份发过来就够了）。
$packLog = Join-Path $outDir 'pack.log'
try { Start-Transcript -Path $packLog -Force | Out-Null } catch { $packLog = $null }

# ---------------------------------------------------------------- 1. 发布
if ($SkipPublish -and (Test-Path (Join-Path $stage 'QControl.exe'))) {
    Step '复用已有发布产物（-SkipPublish）'
} else {
    Step '发布主程序（框架依赖 win-x64）'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    # ⚠️ 数组字面量里别把 'x=' + (Join-Path ...) 直接写进去：它会被拆成两个参数
    #    （一个空属性 + 一个裸路径），MSBuild 于是报 MSB1008「只能指定一个项目」。
    $appBuildDir = '-p:BaseOutputPath=' + (Join-Path $root '_tmp\pkgbuild\')
    Invoke-Dotnet @(
        'publish', (Join-Path $src 'QControl.App\QControl.App.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
        # 不开 ReadyToRun：框架依赖时它只是把我们的 4 个程序集预先编译成当前补丁的机器码，
        # 体积翻倍（1.2 → 2.4 MB）换一次 JIT 的几百毫秒。这里要的是小，不是启动快。
        '-p:PublishReadyToRun=false',
        '-p:SignBinaries=false',                       # 签名放到压缩前做，见文件头
        $appBuildDir,
        '-o', $stage
    ) '发布主程序'
}

# ---------------------------------------------------------------- 2. 体检
Step '检查发布产物'
$mustHave = @('QControl.exe', 'QControl.dll')
foreach ($name in $mustHave) {
    $path = Join-Path $stage $name
    if (-not (Test-Path $path)) { throw "发布产物缺 $name —— 这套目录不完整，装出去也起不来" }
}
# 2026-10-30 起全程序只有一个 exe（静默启动器并进了主程序）。这行是"别再长出第二个入口"的闸门
$strays = @(Get-ChildItem $stage -Filter 'QControl.Silent.*' -File)
if ($strays.Count -gt 0) { throw "发布产物里还有 $($strays[0].Name) —— 静默启动器已并入主程序，别把它带进包" }
$stageSize = (Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum
Info "程序文件：$((Get-ChildItem $stage -File).Count) 个 / $(Format-Mb $stageSize)"

# ⚠️ 这条断言是"别再胖回去"的闸门：框架依赖发布约 2 MB，自包含是 138 MB。
#    如果哪天有人手滑把 --self-contained 改回 true，这里会立刻拦下来，而不是等打出来的
#    安装包被人吐槽"比原厂大八倍"。
if ($stageSize -gt 20MB) {
    throw "发布产物有 $(Format-Mb $stageSize) —— 这不像框架依赖发布（运行时又被塞进来了？）"
}

# pdb 不进分发包：对最终用户没有意义
Get-ChildItem $stage -Filter *.pdb -File | Remove-Item -Force

# ---------------------------------------------------------------- 1b. 运行时依赖
Step '断言主程序是框架依赖'
# 2026-10-25 那次事故（免安装版双击弹 "You must install or update .NET"）的根因是
# 同一个包里的两个入口发布模式不一致。2026-10-30 只剩一个入口，那条坑结构上消失了；
# 但反过来要防"谁又给它单独加了 includedFrameworks" —— 体积会一夜涨 136 MB，而且是悄悄涨的。
$cfgName = 'QControl.runtimeconfig.json'
$cfgPath = Join-Path $stage $cfgName
if (-not (Test-Path $cfgPath)) { throw "发布产物缺 $cfgName" }
$cfg = Get-Content $cfgPath -Raw
if ($cfg -match 'includedFrameworks') {
    throw ("$cfgName 里带 includedFrameworks —— 又变回自包含了（体积会涨 136 MB）。" +
           "多半是 src\QControl.App\bin 里躺着一次旧的 --self-contained 发布产物，删掉那个 bin 目录重跑即可。")
}
# WPF 项目写的是 "frameworks"（数组，Desktop + Core 两个），无 WPF 的写 "framework"（单个）
if ($cfg -notmatch '"frameworks?"') { throw "$cfgName 里没有 framework/frameworks 段 —— 不是合法的框架依赖配置" }
$appCfg = Get-Content (Join-Path $stage 'QControl.runtimeconfig.json') -Raw
if ($appCfg -notmatch 'Microsoft\.WindowsDesktop\.App') {
    throw 'QControl.runtimeconfig.json 里没有 Microsoft.WindowsDesktop.App —— 主程序是 WPF 程序，缺了起不来'
}
Info '主程序是框架依赖（Microsoft.NETCore.App / WindowsDesktop.App 10.0）'

# ---------------------------------------------------------------- 2b. 契约样本
Step '带上契约样本 tests\fixtures'
# ⚠️ 必须带：主程序启动时**硬性**要求这份目录存在（缺了直接 FATAL 退出），
#    理由是"宁可不起服务，也不用空对象冒充数据"。分发包不带的后果是
#    "双击没反应 / 一闪就退"——装了等于没装。
#    放置位置：exe 目录下 tests\fixtures（Paths.FindUp 从 exe 目录向上找这个名字）
$fixturesSrc = Join-Path $root 'tests\fixtures'
if (-not (Test-Path (Join-Path $fixturesSrc 'status.json'))) { throw "缺少契约样本 $fixturesSrc" }
$fixturesDst = Join-Path $stage 'tests\fixtures'
if (Test-Path $fixturesDst) { Remove-Item $fixturesDst -Recurse -Force }
New-Item -ItemType Directory -Force -Path $fixturesDst | Out-Null
Copy-Item (Join-Path $fixturesSrc '*') $fixturesDst -Recurse -Force
Info "契约样本：$((Get-ChildItem $fixturesDst -Recurse -File).Count) 个文件"

# ---------------------------------------------------------------- 2b-2. HWiNFO 落脚目录
Step '建 HWiNFO 落脚目录（放说明，不放程序）'
# 为什么要有这个目录：PCH 温度 / 电压 / 显存结温 / PL1·PL2 / 硬盘 SMART 这五项只有
# HWiNFO 拿得到，而 HWiNFO 是第三方软件、**不能随包分发**（EULA）。所以包里只有位置 + 一张字条：
# 用户把 HWiNFO64.exe 丢进 `HWiNFO\` 就补齐那几行。
# ⚠️ 2026-10-29 之前这里什么都没有：安装版和绿色版装/解压出来都**没有 HWiNFO 文件夹**，
#    而说明里写着"放到本目录下的 HWiNFO\ 里"—— 等于让用户自己建目录，等于没引导。
$hwinfoDir = Join-Path $stage 'HWiNFO'
New-Item -ItemType Directory -Force -Path $hwinfoDir | Out-Null
$hwinfoNote = Join-Path $hwinfoDir '把 HWiNFO64.exe 放在这里.txt'
$hwinfoText = @"
Q-Control 不随包分发 HWiNFO（第三方软件，许可不允许）。

下面这几项只有 HWiNFO 拿得到，想看到它们，把你自己的 HWiNFO 放进来：
  · PCH 温度      · CPU / GPU 核心电压   · 显存结温
  · CPU PL1 / PL2 · 硬盘 SMART

做法：把这个文件夹里放上 HWiNFO64.exe（便携版即可），然后重启 Q-Control。
这个文件夹空的、或者放的不是 HWiNFO，都不影响其它功能，只是上面那几行整行隐藏
（不会显示假数据）。

不想放这里也行，另外两处程序照样会找到（都在这个文件夹之上）：
  1. 解压出来的 Q-Control\ 根下建 HWiNFO\，或再往外一层放 tools\HWiNFO\
  2. 兜底：用户级环境变量 CC_HWI_DIR = 你那份 HWiNFO 的目录（前面都找不到才用它）
"@
[System.IO.File]::WriteAllText($hwinfoNote, $hwinfoText, (New-Object System.Text.UTF8Encoding($true)))
Info "HWiNFO\（含说明）已就位，$(Format-Mb ((Get-Item $hwinfoNote).Length))"

# ---------------------------------------------------------------- 2c. 布局
Step '检查布局（框架依赖：不搬 bin\）'
# ⚠️ 2026-10-25 那份"把 240 个运行时 dll 收进 bin\ + 改写 deps.json"的逻辑**已删除**
#    （2026-10-28）。它是自包含时代的产物：那时安装目录根平铺着 240 多个 dll，点进去
#    一片 dll 才需要收拢。框架依赖之后根目录就十来个文件，收拢不但没用，还要白担
#    "deps.json 改写出错 ⇒ 装到别人机器上起不来"的风险。
#    真要回到自包含，从 git 历史里翻回上一版（keepAtRoot 名单是实测出来的，别重写）。
$rootDlls = @(Get-ChildItem $stage -Filter *.dll -File)
if ($rootDlls.Count -gt 10) {
    throw "根目录有 $($rootDlls.Count) 个 dll（框架依赖应 ≤10）—— 是不是又改回自包含了？"
}
Info "根目录 $($rootDlls.Count) 个 dll / $((Get-ChildItem $stage -File).Count) 个文件"

# ---- 真跑一次 ------------------------------------------------------------
# 用主程序自己的 --selftest 证明这套发布产物真能起来（路由数与字段数从输出里解析，
# 不再写死 —— 它们会随字段增删漂，写死就变成"安静骗人的旧数字"，见 ROADMAP 末节）。
# ⚠️ 框架依赖版要求本机装有 .NET 10 桌面运行时 —— 开发机上必然有（跑得动 SDK 就有），
#    换机器打包时先确认，否则这里会以 0x80008096 之类的宿主错误失败。
# ⚠️ 必须**等它退完**再取退出码：主程序是 **GUI 子系统**（2026-10-29 起，为了双击不弹黑框），
#    PowerShell 的 `&` 和 cmd 都**不会等** GUI 进程 —— 直接 `& exe; $LASTEXITCODE` 拿到的是上一条
#    命令的残留值，于是"发布产物起不来"这类真事故会被静默放过。
# ⚠️ 不用 `Start-Process -Wait`（本该是这个，但它在这台机器上炸了：PowerShell 5.1 里
#    `Start-Process` 会重建环境块，遇到同时存在 `HTTPS_PROXY` / `https_proxy` 的机器
#    （Windows 环境变量不区分大小写，字典里却区分）直接抛 ArgumentException）。
#    改用 ProcessStartInfo，退出码与输出都自己收，稳。
$probeLog = Join-Path $dist 'layout-probe.log'
$probePsi = New-Object System.Diagnostics.ProcessStartInfo
$probePsi.FileName = Join-Path $stage 'QControl.exe'
$probePsi.Arguments = '--selftest --no-log'
$probePsi.WorkingDirectory = $stage
$probePsi.UseShellExecute = $false
$probePsi.CreateNoWindow = $true
$probePsi.RedirectStandardOutput = $true
$probePsi.RedirectStandardError = $true
# ⚠️ 必须显式指定 UTF-8：子进程（ConsoleHost）把输出统一成 UTF-8，而
#    `ProcessStartInfo` 默认按**控制台代码页**（本机 936/GBK）解码管道 ⇒
#    中文全变乱码，下面那条"输出里有没有『通过』"的断言就永远不成立。
$probePsi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
$probePsi.StandardErrorEncoding = New-Object System.Text.UTF8Encoding($false)
$probe = [System.Diagnostics.Process]::Start($probePsi)
$probeOut = $probe.StandardOutput.ReadToEndAsync()
$probeErrText = $probe.StandardError.ReadToEndAsync()
$probe.WaitForExit()
$probeText = $probeOut.Result + $probeErrText.Result
[System.IO.File]::WriteAllText($probeLog, $probeText, (New-Object System.Text.UTF8Encoding($false)))
if ($probe.ExitCode -ne 0) {
    throw "发布产物起不来（--selftest 退出码 $($probe.ExitCode)）。本机装了 .NET 10 桌面运行时吗？详见 $probeLog"
}
if ($probeText -notmatch '通过') {
    throw "自检退出码是 0，但输出里没有「通过」—— 别信退出码，见 $probeLog"
}
Remove-Item $probeLog -Force -ErrorAction SilentlyContinue
# 路由数/字段数当场从输出里取：写死过 32/1977 → 52/1979 → 53/1982 → 53/1989 → 53/2005，
# 每次都靠人肉同步，漏一次就变成"日志里写着 2005、实际 1957"。解析不出来不算失败（上面已断言过
# 「通过」），但要如实说清楚。
if ($probeText -match '通过：(\d+)\s*条路由冻结，(\d+)\s*个字段零丢失') {
    Info "主程序自检通过（--selftest：$($Matches[1]) 路由 / $($Matches[2]) 字段）"
} else {
    Info '主程序自检通过（--selftest）—— 但路由/字段数没解析出来，请核对上方输出'
}

# ---------------------------------------------------------------- 2d. 绿色版引导器
Step '编译绿色版引导器（net48）'
# 绿色版的**唯一入口**（zip 里改名成 Q-Control.exe，见第 5 步）。
# 它跑在 .NET Framework 4.8 上（Windows 自带），所以能在"还没装 .NET 10"的机器上活着，
# 把缺运行时这件事说清楚、顺手装掉 —— 这件事以前只能写在说明文件里，等于没做。
if (Test-Path $bootOut) { Remove-Item $bootOut -Recurse -Force }
Invoke-Dotnet @(
    'build', (Join-Path $src 'QControl.Boot\QControl.Boot.csproj'),
    '-c', 'Release',
    '-p:SignBinaries=false',                       # 签名放到压包前，见文件头
    '-o', $bootOut
) '编译绿色版引导器'

$bootExe = Join-Path $bootOut 'QControl.Boot.exe'
if (-not (Test-Path $bootExe)) { throw "没生成 $bootExe" }
Get-ChildItem $bootOut -Filter *.pdb -File -ErrorAction SilentlyContinue | Remove-Item -Force
# 安装版是 net48、引导器也是 net48，都不该带运行时：几百 KB 才对得上
$bootSize = (Get-Item $bootExe).Length
if ($bootSize -gt 1MB) {
    throw "绿色版引导器有 $(Format-Mb $bootSize) —— 这不是 net48 应该有的体积（应为几百 KB）"
}
Info "引导器：$(Format-Mb $bootSize)"

# ---------------------------------------------------------------- 3. 签名
$pfx = Join-Path $root 'scripts\qcontrol.pfx'
if ($SkipSign -or -not (Test-Path $pfx)) {
    Step '跳过签名'
    Info $(if ($SkipSign) { '按参数要求跳过' } else { "没有证书 $pfx（跑 scripts\make-cert.ps1 生成）" })
} else {
    Step '签名主程序与绿色版引导器（scripts\sign.ps1）'
    # 只签我们自己的 PE：里面绝大多数是微软自带运行时，已签名，脚本会直接跳过
    foreach ($dir in @($stage, $bootOut)) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\sign.ps1') $dir
        if ($LASTEXITCODE -ne 0) { throw "签名失败（$dir，退出码 $LASTEXITCODE）" }
    }
}

# ---------------------------------------------------------------- 4. 打 payload
Step '打包安装载荷 payload.zip'
# includeBaseDirectory=false：解包时直接落到安装目录，不多套一层文件夹
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Info "payload.zip：$(Format-Mb ((Get-Item $payloadZip).Length))"

# ---------------------------------------------------------------- 5. 免安装版
Step '组装免安装版（根上只放引导器，主程序进 app\）'
# 2026-10-29 起绿色版换了布局：
#
#     Q-Control\
#       Q-Control.exe      <- net48 引导器（把 QControl.Boot.exe 改名搬过来）
#       使用说明.txt
#       app\               <- 主程序整套 + model.json + tests\fixtures
#
#   为什么不再把主程序摊在根上：主程序是框架依赖发布，缺 .NET 10 桌面运行时时
#   双击只会一闪而过（连缺什么都看不到）。引导器专门负责那件事：检测 -> 提示 ->
#   自动下载安装 -> 再拉起主程序。根上只留一个 exe，用户也没有点错的机会。
if (Test-Path $bundleRoot) { Remove-Item $bundleRoot -Recurse -Force }
$bundleApp = Join-Path $bundle 'app'
New-Item -ItemType Directory -Force -Path $bundleApp | Out-Null

# 主程序原样整体搬进 app\：tests\fixtures 与 model.json 都是"从 exe 目录向上找"，
# 多一层子目录不影响（见 QControl.App\Http\Paths.cs）
Copy-Item (Join-Path $stage '*') $bundleApp -Recurse -Force

# 绿色版的 exe 旁边必须有 model.json（否则主程序退回纯桩模式）；
# 安装版的载荷里**故意不含**它 —— 安装版的配置放在 %ProgramData% 下，见 Installer.SeedConfig
$modelSource = Join-Path $root 'model.json'
if (-not (Test-Path $modelSource)) { throw "缺少默认配置 $modelSource" }
Copy-Item $modelSource (Join-Path $bundleApp 'model.json') -Force

Copy-Item $bootExe (Join-Path $bundle 'Q-Control.exe') -Force

$readme = Join-Path $bundle '使用说明.txt'
$portableReadme = @"
Q-Control 控制中心  v$version  ·  免安装版（绿色版）
================================================

【怎么启动】
  双击 Q-Control.exe。

  · 第一次启动会先检查运行环境。本机没有 .NET 10 桌面运行时的话，会弹一个
    小窗口：点「自动下载并安装」，等它装完（约 55 MB，中途会弹一次管理员
    授权，点「是」），装好后程序会自动打开。装过之后再双击就是直接启动。
  · 启动后只在托盘出现一个图标：右键托盘图标 → 打开界面。
    要改风扇曲线 / 性能模式 / 灯效时，界面会提示「需管理员」，点一次即可。

【程序、配置、日志在哪】
  app\            程序本体（QControl.exe …）
  app\model.json  配置
  app\logs\       运行日志
  logs\boot.log   绿色版启动器（Q-Control.exe）的日志：第一次双击没反应时先看这里
  整个 Q-Control 文件夹直接拷走 = 连设置一起带走。

【怎么退出】
  右键托盘图标 → 退出。不要用任务管理器杀进程：
  那会跳过收尾，风扇可能停在当前占用手动转速上（重启或睡眠唤醒才恢复）。

【怎么卸载】
  绿色版**没有卸载程序**（本来就不写注册表、不建快捷方式、不装服务），
  所以"卸载"就是删目录，但按这个顺序：
    1. 右键托盘图标 → 退出（不要用任务管理器杀进程）；
    2. 如果开过「开机自启」，先在设置页把它关掉（这会删掉计划任务）；
    3. 删掉整个 Q-Control 目录。配置和日志在里面，一起没了。
  这三步的先后不能反：先删目录再关自启，计划任务就指向一个不存在的 exe 了。

【Windows 提示"已保护你的电脑"？】
  点「更多信息」→「仍要运行」。免费证书拿不到 SmartScreen 白名单，属正常现象。

【运行时装不上 / 连不上网】
  1. 打开 https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0
  2. 选「Windows 桌面运行时 10.0.x · x64」（约 55 MB），装到默认路径即可
     （默认就是 C:\Program Files\dotnet，不要改）。
  3. 装完再双击 Q-Control.exe。
  也可以把下载到的 windowsdesktop-runtime-10.x.x-win-x64.exe 直接放进这个
  文件夹，再双击 Q-Control.exe 点「自动下载并安装」—— 它会优先用本地那一份。

【少了 PCH 温度 / 电压 / 显存结温 / PL1·PL2 / 硬盘 SMART？】
  这几项只有 HWiNFO 拿得到，包里没有带它（第三方软件，不能随包分发）。
  想补上就按程序查找的顺序来（找到即停）：最省事的是把 HWiNFO64.exe 放进
  app\HWiNFO\（这个文件夹包里已经建好了，里面有说明）；放别处也行 ——
  程序会从 app\ 往上找 10 层里的 HWiNFO\ 或 tools\HWiNFO\。
  真要指到这套文件夹外面，才用环境变量（它是兜底，不是优先）：
    用户级环境变量 CC_HWI_DIR = 你自己那份 HWiNFO 的目录
  不配也能正常用，只是上面那几项会整行隐藏 —— 不会显示假数据。

许可证：MIT
"@
[System.IO.File]::WriteAllText($readme, $portableReadme, (New-Object System.Text.UTF8Encoding($true)))

# 断言：根上只能有一个 exe。哪天有人把主程序又摊回根上，用户就会去点那个
# "缺运行时一闪而过"的入口，这次改动就白做了。
$rootExes = @(Get-ChildItem $bundle -Filter *.exe -File)
if ($rootExes.Count -ne 1 -or $rootExes[0].Name -ne 'Q-Control.exe') {
    throw "绿色版根目录应该只有 Q-Control.exe，实际是：$(($rootExes | ForEach-Object Name) -join ', ')"
}

# 真跑一次引导器自检。
# ⚠️ 引导器也是 GUI 子系统：`&` 和 cmd 都**不等**它，退出码拿到的是残值。
# ⚠️ 这里同样不走 `Start-Process -Wait` —— 理由与第 2b 那段相同（本机 HTTPS_PROXY /
#    https_proxy 并存会让 PowerShell 5.1 重建环境块时抛 ArgumentException）。
$bootPsi = New-Object System.Diagnostics.ProcessStartInfo
$bootPsi.FileName = Join-Path $bundle 'Q-Control.exe'
$bootPsi.Arguments = '--boot-selftest'
$bootPsi.WorkingDirectory = $bundle
$bootPsi.UseShellExecute = $false
$bootPsi.CreateNoWindow = $true
$bootProbe = [System.Diagnostics.Process]::Start($bootPsi)
if (-not $bootProbe.WaitForExit(60000)) {
    try { $bootProbe.Kill() } catch { /* 已经退了 */ }
    throw '绿色版引导器自检超时（60 秒没退出）'
}
if ($bootProbe.ExitCode -ne 0) {
    throw "绿色版引导器自检失败（退出码 $($bootProbe.ExitCode)）。详见 $(Join-Path $bundle 'logs\boot-selftest.log')"
}
Info '引导器自检通过（认得出 app\ 里的主程序）'

# 自检会在 bundle 根上留下 logs\（boot.log + boot-selftest.log）。
# 那是**打包机**的诊断记录，不该跟着 zip 发出去 —— 用户第一次启动时自己会生成。
$bootLogs = Join-Path $bundle 'logs'
if (Test-Path $bootLogs) { Remove-Item $bootLogs -Recurse -Force }

Step '打包免安装版 zip'
if (Test-Path $portableZip) { Remove-Item $portableZip -Force }
# includeBaseDirectory = true 时，**传进去的那个目录本身**会成为 zip 顶层：
# 传 $bundle（…\Q-Control）才会得到 Q-Control\…；传它的父目录会多套一层 _bundle\。
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $bundle, $portableZip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

# 体积闸门：1 MB 上下（引导器几百 KB + 主程序 2 MB 压缩之后）。
# 超过 8 MB 基本只能是有人又把运行时打进去了。
$portableSize = (Get-Item $portableZip).Length
if ($portableSize -gt 8MB) {
    throw "绿色版有 $(Format-Mb $portableSize) —— 里面八成又塞了 .NET 运行时（应为 1 MB 上下）"
}
Info "绿色版：$(Format-Mb $portableSize)"

# ---------------------------------------------------------------- 6. 安装包
Step '编译安装包（net48 单 exe，内嵌 payload）'
# 安装器是 net48（Windows 10 1903+ / 11 自带）⇒ 它自己能跑在"还没装 .NET 10"的机器上，
# 而且**不带任何运行时**：整包 = 安装器自己的几百 KB + 载荷（约 1 MB）。所以这里只是
# 普通 build，没有 --self-contained / PublishSingleFile 那些东西（那些正是 72 MB 的来源）。
$payloadProp = '-p:PayloadZip=' + $payloadZip
Invoke-Dotnet @(
    'build', (Join-Path $src 'QControl.Setup\QControl.Setup.csproj'),
    '-c', 'Release',
    '-p:SignBinaries=false',
    $payloadProp,
    '-o', $setupOut
) '编译安装包'

$built = Join-Path $setupOut 'QControl.Setup.exe'
if (-not (Test-Path $built)) { throw "没生成 $built" }
Get-ChildItem $setupOut -Filter *.pdb -File -ErrorAction SilentlyContinue | Remove-Item -Force

# 体积闸门：安装器只有几百 KB + 载荷。超过 10 MB 说明又有人把运行时打进去了
# （net48 的 WPF 在系统里，不该出现在这里）。
$setupStagingSize = (Get-Item $built).Length
if ($setupStagingSize -gt 10MB) {
    throw "安装包有 $(Format-Mb $setupStagingSize) —— 里面八成又塞了运行时（应为 2 MB 上下）"
}

# 先签再搬：sign.ps1 认的是"文件夹"，签完再从 staging 目录搬到 dist 根
if (-not $SkipSign -and (Test-Path $pfx)) {
    Step '签名安装包'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\sign.ps1') $setupOut
    if ($LASTEXITCODE -ne 0) { throw "签名安装包失败（退出码 $LASTEXITCODE）" }
}

Move-Item $built $setupExe -Force
Info "安装包：$(Format-Mb ((Get-Item $setupExe).Length))"

# ---------------------------------------------------------------- 8. 校验
Step '生成 SHA256'
$lines = foreach ($f in @($setupExe, $portableZip)) {
    $hash = (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant()
    "{0}  {1}" -f $hash, (Split-Path -Leaf $f)
}
[System.IO.File]::WriteAllLines($hashFile, $lines, (New-Object System.Text.UTF8Encoding($false)))
$lines | ForEach-Object { Info $_ }

Write-Host ""
Write-Host "  完成。产物在：$outDir" -ForegroundColor Green
Write-Host "    安装包     $(Split-Path -Leaf $setupExe)   $(Format-Mb ((Get-Item $setupExe).Length))" -ForegroundColor Green
Write-Host "    免安装版   $(Split-Path -Leaf $portableZip)   $(Format-Mb ((Get-Item $portableZip).Length))" -ForegroundColor Green
Write-Host ""
Write-Host "  发布前自检：$setupExe /selftest（不用管理员）" -ForegroundColor Yellow
Write-Host "  真机验证  ：双击安装包装一遍，开始菜单里应有「卸载 Q-Control」" -ForegroundColor Yellow
Write-Host ""

if ($packLog) { try { Stop-Transcript | Out-Null } catch { } }
