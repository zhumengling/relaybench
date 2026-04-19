$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptRoot

$projectPath = Join-Path $scriptRoot "NetTest.App\NetTest.App.csproj"
$nugetConfigPath = Join-Path $scriptRoot "NuGet.Config"
$dotnetCliHome = Join-Path $scriptRoot ".dotnet-cli"
$nugetPackagesPath = Join-Path $dotnetCliHome "packages"
$profileRoot = Join-Path $dotnetCliHome "profile"
$appDataPath = Join-Path $profileRoot "AppData\Roaming"
$logPath = Join-Path $scriptRoot "start.log"
$workspaceDataDirectory = Join-Path $scriptRoot "data"
$workspaceConfigDirectory = Join-Path $scriptRoot "config"
$workspaceReportsDirectory = Join-Path $workspaceDataDirectory "reports"
$workspaceAppStatePath = Join-Path $workspaceDataDirectory "app-state.json"
$workspaceProxyTrendsPath = Join-Path $workspaceDataDirectory "proxy-trends.json"
$workspaceProxyRelayConfigPath = Join-Path $workspaceConfigDirectory "proxy-relay.json"
$runOutputDirectory = Join-Path $scriptRoot ".artifacts\run\app"
$runDllPath = Join-Path $runOutputDirectory "NetTest.App.dll"
$appStartupLogPath = Join-Path $scriptRoot "app-startup.log"

function Write-StartLog {
    param([string]$Message)

    $line = "[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

function Reset-StartLog {
    Set-Content -LiteralPath $logPath -Value "" -Encoding UTF8
}

function Ensure-Directory {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
    }
}

function Ensure-JsonSeedFile {
    param(
        [string]$Path,
        [string]$Content,
        [string]$Description
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        Ensure-Directory -Path $parent
    }

    if (Test-Path -LiteralPath $Path) {
        Write-StartLog ("Seed file already exists: {0} ({1})" -f $Description, $Path)
        return
    }

    Set-Content -LiteralPath $Path -Value $Content -Encoding UTF8
    Write-StartLog ("Seed file created: {0} ({1})" -f $Description, $Path)
}

function Ensure-WorkspaceState {
    Ensure-Directory -Path $workspaceDataDirectory
    Ensure-Directory -Path $workspaceConfigDirectory
    Ensure-Directory -Path $workspaceReportsDirectory
    Ensure-Directory -Path $dotnetCliHome
    Ensure-Directory -Path $nugetPackagesPath
    Ensure-Directory -Path $profileRoot
    Ensure-Directory -Path $appDataPath
    Ensure-Directory -Path $runOutputDirectory

    Ensure-JsonSeedFile -Path $workspaceAppStatePath -Content '{}' -Description 'app state'
    Ensure-JsonSeedFile -Path $workspaceProxyTrendsPath -Content '[]' -Description 'proxy trends'
    Ensure-JsonSeedFile -Path $workspaceProxyRelayConfigPath -Content '{}' -Description 'proxy relay config'
}

function Set-LauncherEnvironment {
    $env:DOTNET_CLI_HOME = $dotnetCliHome
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $env:DOTNET_NOLOGO = "1"
    $env:NUGET_PACKAGES = $nugetPackagesPath
    $env:HOME = $profileRoot
    $env:USERPROFILE = $profileRoot
    $env:APPDATA = $appDataPath
    $env:NETTEST_WORKSPACE_ROOT = $scriptRoot
}

function Get-ProjectTargetFramework {
    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "Project file not found: $projectPath"
    }

    [xml]$projectXml = Get-Content -LiteralPath $projectPath
    $targetFramework = @($projectXml.Project.PropertyGroup.TargetFramework | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($targetFramework)) {
        return "net10.0-windows"
    }

    return $targetFramework
}

function Get-RequiredMajorVersion {
    param([string]$TargetFramework)

    if ($TargetFramework -match '^net(\d+)\.') {
        return [int]$Matches[1]
    }

    return 10
}

function Get-DotnetCommand {
    Get-Command dotnet -ErrorAction SilentlyContinue
}

function Test-DotnetSdkAvailable {
    param(
        $DotnetCommand,
        [int]$RequiredMajorVersion
    )

    $sdkList = & $DotnetCommand.Source --list-sdks 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $sdkList) {
        return $false
    }

    foreach ($line in $sdkList) {
        if ($line -match '^(\d+)\.') {
            if ([int]$Matches[1] -ge $RequiredMajorVersion) {
                return $true
            }
        }
    }

    return $false
}

function Open-SdkDownloadPage {
    param([int]$RequiredMajorVersion)

    $downloadUrl = "https://dotnet.microsoft.com/zh-cn/download/dotnet/$RequiredMajorVersion.0"
    Write-Host ".NET SDK $RequiredMajorVersion is required. The download page will be opened now." -ForegroundColor Yellow
    Write-StartLog "Missing .NET SDK. Opening download page: $downloadUrl"
    Start-Process $downloadUrl | Out-Null
}

function Build-Application {
    param($DotnetCommand)

    Write-Host "Building NetTest from source..." -ForegroundColor Cyan
    Write-StartLog "Building project to $runOutputDirectory"

    & $DotnetCommand.Source build $projectPath `
        -c Debug `
        -o $runOutputDirectory `
        --configfile $nugetConfigPath `
        -p:UseAppHost=false `
        -p:UseSharedCompilation=false `
        -nologo `
        -v minimal

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }

    $unexpectedExe = Join-Path $runOutputDirectory "NetTest.App.exe"
    if (Test-Path -LiteralPath $unexpectedExe) {
        Remove-Item -LiteralPath $unexpectedExe -Force -ErrorAction SilentlyContinue
    }

    if (-not (Test-Path -LiteralPath $runDllPath)) {
        throw "Build finished but DLL was not found: $runDllPath"
    }
}

function Start-Application {
    param($DotnetCommand)

    Write-Host "Starting NetTest..." -ForegroundColor Cyan
    Write-StartLog "Launching dotnet with DLL: $runDllPath"

    $process = Start-Process -FilePath $DotnetCommand.Source `
        -ArgumentList @($runDllPath) `
        -WorkingDirectory $scriptRoot `
        -WindowStyle Hidden `
        -PassThru

    Start-Sleep -Seconds 3
    $process.Refresh()
    if ($process.HasExited) {
        throw "Application exited immediately. Check $appStartupLogPath and $logPath"
    }

    Write-StartLog ("Launch succeeded. PID={0}" -f $process.Id)
}

try {
    Reset-StartLog
    Write-StartLog "Launcher started."
    Write-StartLog ("Workspace root: {0}" -f $scriptRoot)

    Ensure-WorkspaceState
    Set-LauncherEnvironment
    Write-StartLog "Workspace directories and environment are ready."

    $targetFramework = Get-ProjectTargetFramework
    $requiredMajorVersion = Get-RequiredMajorVersion -TargetFramework $targetFramework
    Write-StartLog ("Target framework: {0}" -f $targetFramework)

    $dotnet = Get-DotnetCommand
    if (-not $dotnet) {
        Open-SdkDownloadPage -RequiredMajorVersion $requiredMajorVersion
        throw "dotnet command was not found. Please install .NET SDK $requiredMajorVersion and run start.cmd again."
    }

    Write-StartLog ("dotnet command: {0}" -f $dotnet.Source)

    if (-not (Test-DotnetSdkAvailable -DotnetCommand $dotnet -RequiredMajorVersion $requiredMajorVersion)) {
        Open-SdkDownloadPage -RequiredMajorVersion $requiredMajorVersion
        throw ".NET SDK $requiredMajorVersion or newer was not found."
    }

    Build-Application -DotnetCommand $dotnet
    Start-Application -DotnetCommand $dotnet
}
catch {
    $message = $_.Exception.Message
    Write-StartLog ("Launch failed: {0}" -f $message)
    if ($_.ScriptStackTrace) {
        Write-StartLog ("Stack trace: {0}" -f $_.ScriptStackTrace)
    }

    Write-Host "Launch failed: $message" -ForegroundColor Red
    Write-Host "See log: $logPath" -ForegroundColor Yellow
    exit 1
}
