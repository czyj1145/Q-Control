@echo off
rem ============================================================
rem  publish-r2r.cmd - OPTIONAL ReadyToRun build of the backend.
rem
rem  Why it exists: an ordinary build is JIT-compiled at startup, and
rem  the machine code the JIT emits lives in PRIVATE memory that no
rem  other process can share. ReadyToRun precompiles that code into
rem  the DLLs themselves, so the runtime maps it from disk instead -
rem  file-backed pages that the OS can drop under memory pressure.
rem  Expect a few MB less private commit and a faster startup.
rem
rem  WHY IT IS NOT WIRED INTO THE NORMAL STARTUP:
rem    R2R needs a RuntimeIdentifier, and a RID changes the output
rem    path. run-silent.wsf, src\run-tray.cmd and the autostart task
rem    all point at
rem      src\QControl.App\bin\Release\net10.0-windows\
rem    HARD-CODED. Switching to this build means editing those two
rem    files and recreating the autostart entry (it stores a full
rem    path). That should be a deliberate decision, not a surprise.
rem
rem  Output: src\QControl.App\bin\R2R\net10.0-windows\
rem  Nothing here disturbs the ordinary build.
rem
rem  ASCII-only on purpose: .cmd files get mangled by the console
rem  code page if they contain Chinese.
rem ============================================================
setlocal EnableExtensions
cd /d "%~dp0"

rem Resolve dotnet: DOTNET_ROOT first, then the isolated install, then PATH.
rem DOTNET_ROOT, then the isolated install, then PATH - never hardcode a user-absolute path.
set "DOTNET="
if defined DOTNET_ROOT if exist "%DOTNET_ROOT%\dotnet.exe" set "DOTNET=%DOTNET_ROOT%\dotnet.exe"
if not defined DOTNET if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not defined DOTNET set "DOTNET=dotnet"
set "PROJ=%~dp0QControl.App\QControl.App.csproj"
set "OUT=%~dp0QControl.App\bin\R2R\net10.0-windows"
set "REL=%~dp0QControl.App\bin\Release\net10.0-windows"

rem The fallback above may be a bare `dotnet`, so probe it instead of
rem testing for a file that may not exist under that name.
"%DOTNET%" --version >nul 2>&1
if errorlevel 1 (
  echo.
  echo  [ERROR] dotnet not found: %DOTNET%
  echo          Set DOTNET_ROOT, or install it under
  echo          %%USERPROFILE%%\.dotnet, or put it on PATH.
  echo.
  pause
  exit /b 1
)

echo.
echo  Publishing ReadyToRun build - framework-dependent, win-x64...
echo    -^> %OUT%
echo.

"%DOTNET%" publish "%PROJ%" -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o "%OUT%"
if errorlevel 1 (
  echo.
  echo  [ERROR] publish failed - see the output above.
  echo          A missing runtime pack is the usual cause; it needs
  echo          to be restorable from NuGet.
  pause
  exit /b 1
)

rem 2026-10-30: the silent launcher is gone - it was merged into QControl.exe
rem (one exe, one entry: the app is WinExe since 2026-10-29, so the autostart
rem task no longer needs a console-free wrapper). This script used to copy
rem QControl.Silent.* by hand because -r relocated it into win-x64\; there is
rem nothing to copy now.

rem -r leaks into every project in the graph, so the App has just built
rem into net10.0-windows\win-x64\ instead of net10.0-windows\. One
rem plain build puts the default layout back, so the normal startup
rem path stays exactly as run-silent.wsf expects it.
echo.
echo  Restoring the default build layout...
"%DOTNET%" build "%PROJ%" -c Release -v q --nologo
if errorlevel 1 (
  echo  [WARN] the follow-up build failed - run src\test.cmd before
  echo         starting the backend.
)

echo.
echo  Done. ReadyToRun build is in:
echo    %OUT%
echo.
echo  To actually RUN it, point the startup chain at that folder:
echo    run-silent.wsf   : the BuildDir function near the top
echo    src\run-tray.cmd : the EXE= line
echo  then recreate the autostart entry - the scheduled task stores
echo  a full path, so it keeps aiming at the old folder until you do.
echo.
pause
