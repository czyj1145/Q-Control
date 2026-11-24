@echo off
REM ============================================================
REM  dump EC 空间 —— 必须管理员（PawnIo 读写 IO 端口需要特权，
REM  非管理员下会读到全 0，别误以为 EC 没数据）。
REM
REM  用法：右键本文件 →“以管理员身份运行”
REM  结果：同目录 _ec_dump.txt
REM ============================================================
chcp 936 >nul
cd /d "%~dp0"
set "PY=python"

net session >nul 2>&1
if errorlevel 1 goto NOADMIN
where python >nul 2>&1
if errorlevel 1 goto NOPY

echo 管理员 OK，正在 dump EC ...
echo.
"%PY%" "%~dp0_probe_ec3.py"
echo.
echo 完成，结果在：
echo   %~dp0_ec_dump.txt
echo.
pause
exit /b 0

:NOADMIN
echo.
echo [x] 当前不是管理员。
echo     PawnIo 读写 IO 端口需要特权，非管理员会读回来一整片 00。
echo.
echo     请右键本文件 →“以管理员身份运行”
echo.
pause
exit /b 1

:NOPY
echo.
echo [x] 找不到 python：
echo     %PY%
echo.
pause
exit /b 1
