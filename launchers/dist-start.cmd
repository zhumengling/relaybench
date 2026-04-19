@echo off
set SCRIPT_DIR=%~dp0
if not exist "%SCRIPT_DIR%data" mkdir "%SCRIPT_DIR%data" >nul 2>nul
if not exist "%SCRIPT_DIR%config" mkdir "%SCRIPT_DIR%config" >nul 2>nul
if not exist "%SCRIPT_DIR%data\reports" mkdir "%SCRIPT_DIR%data\reports" >nul 2>nul
if not exist "%SCRIPT_DIR%data\app-state.json" > "%SCRIPT_DIR%data\app-state.json" echo {}
if not exist "%SCRIPT_DIR%data\proxy-trends.json" > "%SCRIPT_DIR%data\proxy-trends.json" echo []
if not exist "%SCRIPT_DIR%config\proxy-relay.json" > "%SCRIPT_DIR%config\proxy-relay.json" echo {}
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%start.ps1"
set EXIT_CODE=%ERRORLEVEL%
if not "%EXIT_CODE%"=="0" (
    echo.
    echo 启动失败，详情请查看 "%SCRIPT_DIR%start.log"
    pause
)
exit /b %EXIT_CODE%
