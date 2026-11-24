@echo off
rem ============================================================
rem  Stop the local ControlCenter backend(s).
rem
rem  The C# build owns 8765. 8766 is still probed for a leftover
rem  legacy 旧版 instance (removed 2026-10-24) - harmless if idle.
rem
rem  ORDER MATTERS: always ask it to quit FIRST. on_quit parks the
rem  fans at the current temperature's duty and releases the curve;
rem  a bare taskkill /F skips on_quit, so the EC keeps spinning at
rem  the last written duty value (only sleep/resume gets it back).
rem
rem  ASCII-only on purpose: .cmd files get mangled by the console
rem  code page if they contain Chinese.
rem ============================================================
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
set "LOG=%~dp0run.log"

set "ISADMIN=0"
>nul 2>&1 fsutil dirty query %systemdrive%
if not errorlevel 1 set "ISADMIN=1"
if "%ISADMIN%"=="0" (
    >nul 2>&1 net session
    if not errorlevel 1 set "ISADMIN=1"
)
if not "%ISADMIN%"=="1" (
    echo [INFO] Requesting administrator rights to stop the backend...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set "STOPPED=0"
call :stop_port 8765
call :stop_port 8766

if "!STOPPED!"=="1" (
    >>"%LOG%" echo [%date% %time%] stop.cmd stopped backend
) else (
    echo [INFO] No ControlCenter backend was listening.
)
ping -n 3 127.0.0.1 >nul
exit /b 0

rem ------------------------------------------------------------
rem  :stop_port <port> - graceful quit, then force-kill leftovers
rem ------------------------------------------------------------
:stop_port
set "PORT=%~1"
netstat -ano | findstr ":%PORT%" | findstr LISTENING >nul 2>&1
if errorlevel 1 exit /b 0
set "STOPPED=1"

echo [INFO] Port %PORT%: asking the backend to quit gracefully...
rem  --noproxy "*" is required: the system HTTP proxy would answer 502
curl -s -X POST --noproxy "*" --max-time 3 "http://127.0.0.1:%PORT%/api/app/quit" >nul 2>&1
set "GRACEFUL=0"
if not errorlevel 1 set "GRACEFUL=1"
rem  on_quit does real EC writes (parking the fans); give it a moment
ping -n 4 127.0.0.1 >nul

set "FOUND=0"
for /f "tokens=5" %%p in ('netstat -ano ^| findstr ":%PORT%" ^| findstr LISTENING') do (
    set "FOUND=1"
    taskkill /PID %%p /F /T
)
if "!FOUND!"=="1" (
    echo [OK] Port %PORT%: force-killed (graceful=!GRACEFUL!)
) else (
    echo [OK] Port %PORT%: backend exited.
)
exit /b 0
