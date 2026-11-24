@echo off
rem ============================================================
rem  pack-release.cmd - build the two distributables.
rem
rem  Produces, in ControlCenter\dist\<ver>\  (one folder per version):
rem    QControl-<ver>-setup-win-x64.exe          installer (one file, ~1 MB)
rem    QControl-<ver>-portable-win-x64.zip       no-install build (~1 MB)
rem    SHA256.txt                                checksums for both
rem    pack.log                                  full log of this run
rem
rem  Both are framework-dependent (2026-10-28): the target machine needs the
rem  .NET 10 Desktop Runtime. Neither bundles the runtime - doing so costs ~136 MB.
rem  The installer checks for it and silently installs it when missing.
rem  The portable zip does the same since 2026-10-29, through a small net48
rem  launcher (Q-Control.exe, source in src\QControl.Boot) that sits at the root
rem  of the zip while the real program lives in app\. Before that, double-clicking
rem  the portable build on a machine without .NET 10 just flashed and died.
rem  The version comes from ControlCenter\VERSION - the only place to bump it.
rem
rem  Everything else lives in scripts\pack-release.ps1 (zipping + signing are
rem  far less painful in PowerShell). ASCII-only on purpose: .cmd files get
rem  mangled by the console code page if they contain Chinese.
rem ============================================================
setlocal EnableExtensions
cd /d "%~dp0.."

rem Resolve dotnet: DOTNET_ROOT first, then the isolated install, then PATH.
rem DOTNET_ROOT, then the isolated install, then PATH - never hardcode a user-absolute path.
set "DOTNET="
if defined DOTNET_ROOT if exist "%DOTNET_ROOT%\dotnet.exe" set "DOTNET=%DOTNET_ROOT%\dotnet.exe"
if not defined DOTNET if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not defined DOTNET set "DOTNET=dotnet"

"%DOTNET%" --version >nul 2>&1
if errorlevel 1 (
  echo.
  echo  [ERROR] dotnet not found: %DOTNET%
  echo          Put it on PATH or set DOTNET_ROOT.
  echo.
  pause
  exit /b 1
)

set "DOTNET_EXE=%DOTNET%"
rem  ⚠️ 2026-10-28 把 ps1 挪到了 ControlCenter\scripts\：这里必须走 ..\scripts\，
rem     早先的 %~dp0pack-release.ps1 在挪动当天就再也跑不起来了（发版脚本静默失效）。
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\pack-release.ps1" %*
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
  echo.
  echo  [ERROR] packaging failed - exit code %RC%
  pause
)
exit /b %RC%
