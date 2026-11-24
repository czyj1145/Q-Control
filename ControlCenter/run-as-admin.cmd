@echo off
rem ============================================================
rem  MAIN ENTRY - double-click this file.
rem
rem  DEFAULT (no arguments): NO black console window.
rem    The work is handed to run-silent.wsf, which
rem      - starts QControl.exe (a WinExe since 2026-10-29 - no console of
rem        its own, so there is no black box to hide anymore);
rem      - lets the app do its own elevation (HighestAvailable task first,
rem        zero UAC; runas only the very first time);
rem      - calls the existing window to the front if one is already running.
rem    Net effect: nothing pops up in the taskbar, you only get the tray
rem    icon. The console output is still tee'd into
rem    ControlCenter\logs\cc-yyyy-MM-dd.log.
rem
rem  --console: the old, visible way - startup banner, Ctrl+C works,
rem    fans are handed back when the window closes. Use it when you
rem    actually want to watch what happens.
rem
rem  Since P8 (2026-10-20) the C# backend owns 8765 - the same port
rem  every script, bookmark and the autostart task already use, and
rem  the only backend there is.
rem
rem  ASCII-only on purpose: .cmd files get mangled by the console
rem  code page if they contain Chinese.
rem ============================================================
setlocal EnableExtensions
cd /d "%~dp0"

for %%a in (%*) do if /i "%%~a"=="--console" goto console

if not exist "%~dp0run-silent.wsf" (
    echo.
    echo  [ERROR] run-silent.wsf is missing next to run-as-admin.cmd.
    echo.
    pause
    exit /b 1
)
rem  `start` returns immediately, so this window is gone in a blink.
start "" wscript.exe "%~dp0run-silent.wsf" %*
exit /b 0

:console
call "%~dp0src\run-tray.cmd" %*
exit /b %errorlevel%
