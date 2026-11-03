@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0first-run.ps1" (
  echo [ERROR] first-run.ps1 not found. Please extract the whole folder first.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0first-run.ps1"
echo.
pause