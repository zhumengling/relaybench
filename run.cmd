@echo off
setlocal EnableExtensions

cd /d "%~dp0"

set "MODE=run"
set "CONFIG=Release"

:parse_args
if "%~1"=="" goto execute

if /I "%~1"=="run" (
    set "MODE=run"
    shift
    goto parse_args
)

if /I "%~1"=="build" (
    set "MODE=build"
    shift
    goto parse_args
)

if /I "%~1"=="debug" (
    set "CONFIG=Debug"
    shift
    goto parse_args
)

if /I "%~1"=="release" (
    set "CONFIG=Release"
    shift
    goto parse_args
)

echo [NetTest] Unknown argument: %~1
echo Usage:
echo   run.cmd              ^<== start Release
echo   run.cmd debug        ^<== start Debug
echo   run.cmd build        ^<== build Release only
echo   run.cmd build debug  ^<== build Debug only
exit /b 1

:execute
where dotnet >nul 2>nul
if errorlevel 1 (
    echo [NetTest] dotnet was not found. Please install the .NET SDK first.
    exit /b 1
)

if /I "%MODE%"=="build" (
    echo [NetTest] Building %CONFIG%...
    dotnet build ".\NetTest.App\NetTest.App.csproj" -c %CONFIG%
    exit /b %errorlevel%
)

echo [NetTest] Starting %CONFIG%...
dotnet run --project ".\NetTest.App\NetTest.App.csproj" -c %CONFIG% --no-launch-profile
exit /b %errorlevel%
