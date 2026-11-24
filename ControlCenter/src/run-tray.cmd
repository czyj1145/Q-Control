@echo off
rem ============================================================
rem  Run the C# backend WITH the tray icon and the native WPF window.
rem
rem  Double-click this file. It asks for UAC (EC writes need
rem  admin). Closing the console window hands the fans back the
rem  same way Ctrl+C does.
rem
rem  This backend owns the MAIN port 8765 - the same address the
rem  scripts, the autostart task and any bookmark already use.
rem
rem  On Windows 11 a new tray icon goes into the overflow area:
rem  click the "^" chevron next to the clock, or drag it out to
rem  pin it. Left click opens the dashboard, right click shows
rem  the menu (open / autostart / quit).
rem
rem  ASCII-only on purpose: .cmd files get mangled by the console
rem  code page if they contain Chinese.
rem ============================================================
setlocal EnableExtensions
cd /d "%~dp0"

set "ISADMIN=0"
>nul 2>&1 net session
if not errorlevel 1 set "ISADMIN=1"
if "%ISADMIN%"=="1" goto run

echo.
echo  Requesting administrator rights - click Yes on the UAC prompt.
echo.
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
exit /b

:run
set "EXE=%~dp0QControl.App\bin\R2R\net10.0-windows\QControl.exe"
if not exist "%EXE%" set "EXE=%~dp0QControl.App\bin\Release\net10.0-windows\QControl.exe"
if not exist "%EXE%" (
    echo.
    echo  [ERROR] Not built yet: %EXE%
    echo          Run src\test.cmd once, or build the solution in Release.
    echo.
    pause
    exit /b 1
)

echo.
echo  Starting C# backend on 8765 (main port) with the tray icon...
echo  (left click the icon to open the UI; right click for the menu)
echo.
rem  No --port on purpose: 8765 is the compiled-in default, and one
rem  source of truth beats a second copy inside a .cmd file.
rem 2026-10-21: do NOT force --tray here any more. The UI is WPF now, so the
rem console entry should also show the window (that is what "console" means:
rem watch the startup banner AND use the app). Only the autostart task asks
rem for tray-only, and it passes --tray itself.
rem Drop the literal "console" token that run-as-admin.cmd forwarded.
set "PASSTHRU=%*"
set "PASSTHRU=%PASSTHRU:console=%"
rem 2026-10-29: QControl.exe is a GUI-subsystem app now (double-clicking it no longer
rem pops a console). cmd does NOT wait for GUI apps, so without `start /wait` the
rem "Backend exited" line below would print the instant the backend starts.
rem Conhost is shared, so the banner/log output still lands in this window.
start /wait "" "%EXE%" %PASSTHRU%
echo.
echo  Backend exited. If the fans seem stuck, start it again and let
echo  the curve take over, or run stop.cmd to make sure nothing is left.
echo.
pause
