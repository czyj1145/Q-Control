@echo off
rem ============================================================
rem  P3/P4 verifier launcher.
rem
rem  READ-ONLY (no admin needed):
rem    p3-probe.cmd                        EC read + write-guard check
rem    p3-probe.cmd --sensors              also compare every sensor
rem                                        against the running instance (8765)
rem
rem  WRITE (elevates, asks for confirmation):
rem    p3-probe.cmd write                  backlight write -> readback -> restore
rem    p3-probe.cmd write --set-backlight 1
rem    p3-probe.cmd write --set-fans 100,100
rem
rem  The read-only path only issues read-group commands
rem  (0xFA00: 0x0200 / 0x0202 / 0x0203 / 0x0207). No EC writes.
rem  ASCII-only on purpose so chcp 65001 can be set for Chinese output.
rem ============================================================
setlocal EnableExtensions
cd /d "%~dp0"

set "DOTNET="
if defined DOTNET_ROOT if exist "%DOTNET_ROOT%\dotnet.exe" set "DOTNET=%DOTNET_ROOT%\dotnet.exe"
if not defined DOTNET if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not defined DOTNET set "DOTNET=dotnet"

set "PROJ=%~dp0QControl.EcProbe\QControl.EcProbe.csproj"

chcp 65001 >nul
title P3/P4 verifier

if /i "%~1"=="write" goto writetest

rem ---------------------------------------------------------- read-only
"%DOTNET%" run --project "%PROJ%" -c Release -- %*
set "RC=%errorlevel%"
goto done

rem ---------------------------------------------------------- write
:writetest
set "ISADMIN=0"
>nul 2>&1 net session
if not errorlevel 1 set "ISADMIN=1"
if "%ISADMIN%"=="1" goto writetest_run

echo [INFO] Write verification needs administrator rights.
echo        Click Yes on the UAC prompt.
rem Pass %* (including "write") -- otherwise --set-fans would be lost
rem during elevation. The probe itself understands the "write" token.
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs"
exit /b

:writetest_run
echo.
echo ============================================================
echo  WRITE VERIFICATION
echo.
echo   Default         : backlight write -^> readback -^> restore
echo   --set-backlight : write one level, read back
echo   --set-fans a,b  : write fan duty, wait 3s, compare RPM
echo.
echo  Nothing else is written. Performance mode and GPU mode are
echo  NOT available here on purpose (they change APMD / need reboot).
echo ============================================================
echo.
pause
"%DOTNET%" run --project "%PROJ%" -c Release -- %*
set "RC=%errorlevel%"

:done
echo.
if "%RC%"=="0" (
    echo [PASS] exit code 0
) else (
    echo [FAIL] exit code %RC%
)
echo.
pause
exit /b %RC%
