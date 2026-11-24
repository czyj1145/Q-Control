@echo off
rem ============================================================
rem  Core + contract test runner.
rem  Offline: no hardware, no admin rights, no EC access needed.
rem  ASCII-only on purpose so chcp 65001 can be set for Chinese output.
rem ============================================================
setlocal EnableExtensions
cd /d "%~dp0"

set "DOTNET="
if defined DOTNET_ROOT if exist "%DOTNET_ROOT%\dotnet.exe" set "DOTNET=%DOTNET_ROOT%\dotnet.exe"
if not defined DOTNET if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not defined DOTNET set "DOTNET=dotnet"

chcp 65001 >nul
title QControl tests

echo ============================================================
echo  dotnet test  (Core logic + HTTP contract)
echo ============================================================
"%DOTNET%" test "%~dp0..\QControl.slnx" -c Release %*
set "RC=%errorlevel%"

echo.
if "%RC%"=="0" (
    echo [PASS] all tests green
) else (
    echo [FAIL] exit code %RC%
)
echo.
pause
exit /b %RC%
