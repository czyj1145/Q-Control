@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "PY=python"

rem 自提权：HWiNFO64.exe 的清单是 requireAdministrator + uiAccess，
rem 非管理员起不来（CreateProcess 直接报 740）。已在管理员态则不弹 UAC。
set "ISADMIN=0"
>nul 2>&1 fsutil dirty query %systemdrive%
if not errorlevel 1 set "ISADMIN=1"
if "%ISADMIN%"=="0" (
    >nul 2>&1 net session
    if not errorlevel 1 set "ISADMIN=1"
)
if "%ISADMIN%"=="1" goto run

echo [INFO] 需要管理员权限（HWiNFO 清单要求提权），马上弹 UAC，请点「是」。
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
if errorlevel 1 (
    echo [FAIL] 自动提权失败：请右键本文件 -^> 以管理员身份运行
    pause
)
exit /b

:run
echo [OK] 已是管理员。正在拉起 HWiNFO 并 dump 全部读数（首次约 10~60 秒）...
echo.
"%PY%" "%~dp0_probe_hwi_all.py"
echo.
pause
