@echo off
setlocal EnableExtensions

cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
exit /b %errorlevel%
