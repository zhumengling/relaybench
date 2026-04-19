$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptRoot

$appExe = Join-Path $scriptRoot "NetTest.App.exe"
$runtimeConfigPath = Join-Path $scriptRoot "NetTest.App.runtimeconfig.json"
$requiredRuntimeName = "Microsoft.WindowsDesktop.App"
$dotnetCliHome = Join-Path $scriptRoot ".dotnet-cli"
$profileRoot = Join-Path $dotnetCliHome "profile"
$appDataPath = Join-Path $profileRoot "AppData\Roaming"
$logPath = Join-Path $scriptRoot "start.log"
$dataDirectory = Join-Path $scriptRoot "data"
$configDirectory = Join-Path $scriptRoot "config"
$reportsDirectory = Join-Path $dataDirectory "reports"
$appStatePath = Join-Path $dataDirectory "app-state.json"
$proxyTrendsPath = Join-Path $dataDirectory "proxy-trends.json"
$proxyRelayConfigPath = Join-Path $configDirectory "proxy-relay.json"

function Write-StartLog {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    $line = "[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

function Reset-StartLog {
    Set-Content -LiteralPath $logPath -Value "" -Encoding UTF8
}

function Ensure-LauncherDirectories {
    New-Item -ItemType Directory -Force -Path $dotnetCliHome | Out-Null
    New-Item -ItemType Directory -Force -Path $profileRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $appDataPath | Out-Null
}

function Ensure-AppDirectories {
    New-Item -ItemType Directory -Force -Path $scriptRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null
    New-Item -ItemType Directory -Force -Path $configDirectory | Out-Null
    New-Item -ItemType Directory -Force -Path $reportsDirectory | Out-Null
}

function Ensure-JsonSeedFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Content,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    if (Test-Path $Path) {
        Write-StartLog "启动自检：${Description} 已存在。${Path}"
        return
    }

    Set-Content -LiteralPath $Path -Value $Content -Encoding UTF8
    Write-StartLog "启动自检：已初始化 ${Description}。${Path}"
}

function Ensure-AppSeedFiles {
    Ensure-JsonSeedFile -Path $appStatePath -Content '{}' -Description '应用状态文件'
    Ensure-JsonSeedFile -Path $proxyTrendsPath -Content '[]' -Description '中转站趋势文件'
    Ensure-JsonSeedFile -Path $proxyRelayConfigPath -Content '{}' -Description '中转站目录配置文件'
}

function Write-EnvironmentSelfCheckLog {
    Write-StartLog "启动自检：程序目录 = ${scriptRoot}"
    Write-StartLog "启动自检：程序文件 = ${appExe}"
    Write-StartLog "启动自检：运行时配置 = ${runtimeConfigPath}"
    Write-StartLog "启动自检：DOTNET_CLI_HOME = ${dotnetCliHome}"
}

function Get-RequiredRuntimeMajorVersion {
    if (-not (Test-Path $runtimeConfigPath)) {
        return 10
    }

    $runtimeConfig = Get-Content -Path $runtimeConfigPath -Raw | ConvertFrom-Json
    $desktopFramework = @($runtimeConfig.runtimeOptions.frameworks | Where-Object { $_.name -eq $requiredRuntimeName }) | Select-Object -First 1
    if ($desktopFramework.version -match '^(\d+)\.') {
        return [int]$Matches[1]
    }

    return 10
}

function Get-DotnetCommand {
    Get-Command dotnet -ErrorAction SilentlyContinue
}

function Test-WindowsDesktopRuntime {
    param(
        [Parameter(Mandatory = $true)]
        $DotnetCommand,
        [Parameter(Mandatory = $true)]
        [int]$RequiredMajorVersion
    )

    $runtimeList = & $DotnetCommand.Source --list-runtimes 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $runtimeList) {
        return $false
    }

    $escapedRuntimeName = [regex]::Escape($requiredRuntimeName)
    foreach ($line in $runtimeList) {
        if ($line -match ("^{0}\s+(\d+)\.(\d+)\." -f $escapedRuntimeName)) {
            if ([int]$Matches[1] -eq $RequiredMajorVersion) {
                return $true
            }
        }
    }

    return $false
}

try {
    Reset-StartLog
    Write-StartLog '发布版启动器开始运行。'

    Ensure-LauncherDirectories
    Ensure-AppDirectories
    Ensure-AppSeedFiles
    Write-EnvironmentSelfCheckLog

    $env:DOTNET_CLI_HOME = $dotnetCliHome
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $env:DOTNET_NOLOGO = "1"
    $env:HOME = $profileRoot
    $env:USERPROFILE = $profileRoot
    $env:APPDATA = $appDataPath
    Write-StartLog '启动自检：启动器运行环境已准备完成。'

    $requiredRuntimeMajorVersion = Get-RequiredRuntimeMajorVersion
    $runtimeDownloadUrl = "https://dotnet.microsoft.com/zh-cn/download/dotnet/$requiredRuntimeMajorVersion.0"
    $dotnet = Get-DotnetCommand
    $hasRequiredRuntime = $false

    if ($dotnet) {
        Write-StartLog "启动自检：已检测到 dotnet。$($dotnet.Source)"
        $hasRequiredRuntime = Test-WindowsDesktopRuntime -DotnetCommand $dotnet -RequiredMajorVersion $requiredRuntimeMajorVersion
    }
    else {
        Write-StartLog '启动自检：未找到 dotnet 命令。'
    }

    if (-not $hasRequiredRuntime) {
        Write-Host "未检测到 .NET Desktop Runtime ${requiredRuntimeMajorVersion}。" -ForegroundColor Yellow
        Write-Host '即将打开微软官方下载页面，请安装后再重新运行 start.cmd。' -ForegroundColor Yellow
        Write-StartLog "启动自检：缺少 .NET Desktop Runtime ${requiredRuntimeMajorVersion}，准备打开 ${runtimeDownloadUrl}"
        Start-Process $runtimeDownloadUrl | Out-Null
        throw "缺少必须的 .NET Desktop Runtime ${requiredRuntimeMajorVersion}。请安装后重新运行 start.cmd。"
    }

    if (-not (Test-Path $appExe)) {
        throw "未在启动器同目录找到程序文件：${appExe}"
    }

    Write-Host '正在启动 NetTest ...' -ForegroundColor Cyan
    Write-StartLog "启动自检：准备从 ${appExe} 启动程序。"
    $process = Start-Process -FilePath $appExe -WorkingDirectory $scriptRoot -PassThru
    Start-Sleep -Seconds 2
    $process.Refresh()
    if ($process.HasExited) {
        $appStartupLogPath = Join-Path $scriptRoot "app-startup.log"
        throw "程序启动后立即退出。请查看 ${appStartupLogPath} 和 ${logPath} 里的详细日志。"
    }

    Write-StartLog '启动成功：程序在启动检查后仍保持运行。'
}
catch {
    $message = $_.Exception.Message
    Write-StartLog "启动失败：$message"
    if ($_.ScriptStackTrace) {
        Write-StartLog "调用栈：$($_.ScriptStackTrace)"
    }

    Write-Host "启动失败：$message" -ForegroundColor Red
    Write-Host "请查看日志：${logPath}" -ForegroundColor Yellow
    throw
}
