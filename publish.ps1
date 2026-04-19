$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptRoot

$outputDirectory = Join-Path $scriptRoot "dist\win-x64"
$projectPath = Join-Path $scriptRoot "NetTest.App\NetTest.App.csproj"
$dotnetCliHome = Join-Path $scriptRoot ".dotnet-cli"
$nugetPackagesPath = Join-Path $dotnetCliHome "packages"
$nugetConfigPath = Join-Path $scriptRoot "NuGet.Config"
$profileRoot = Join-Path $dotnetCliHome "profile"
$appDataPath = Join-Path $profileRoot "AppData\Roaming"
$distLauncherPs1 = Join-Path $scriptRoot "launchers\dist-start.ps1"
$distLauncherCmd = Join-Path $scriptRoot "launchers\dist-start.cmd"
$stateBackupRoot = Join-Path $scriptRoot ".artifacts\publish-state-backup"
$stateBackupPaths = @(
    "config\proxy-relay.json",
    "data\app-state.json",
    "data\proxy-trends.json",
    "data\reports"
)

function Backup-PublishState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$OutputDirectory,
        [Parameter(Mandatory = $true)]
        [string]$BackupDirectory
    )

    if (-not (Test-Path $OutputDirectory)) {
        return
    }

    if (Test-Path $BackupDirectory) {
        Remove-Item -LiteralPath $BackupDirectory -Recurse -Force
    }

    foreach ($relativePath in $stateBackupPaths) {
        $sourcePath = Join-Path $OutputDirectory $relativePath
        if (-not (Test-Path $sourcePath)) {
            continue
        }

        $targetPath = Join-Path $BackupDirectory $relativePath
        $targetParent = Split-Path -Parent $targetPath
        if (-not (Test-Path $targetParent)) {
            New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
        }

        if ((Get-Item -LiteralPath $sourcePath).PSIsContainer) {
            Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Recurse -Force
        }
        else {
            Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
        }
    }
}

function Restore-PublishState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BackupDirectory,
        [Parameter(Mandatory = $true)]
        [string]$OutputDirectory
    )

    if (-not (Test-Path $BackupDirectory)) {
        return
    }

    foreach ($relativePath in $stateBackupPaths) {
        $sourcePath = Join-Path $BackupDirectory $relativePath
        if (-not (Test-Path $sourcePath)) {
            continue
        }

        $targetPath = Join-Path $OutputDirectory $relativePath
        $targetParent = Split-Path -Parent $targetPath
        if (-not (Test-Path $targetParent)) {
            New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
        }

        if ((Get-Item -LiteralPath $sourcePath).PSIsContainer) {
            Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Recurse -Force
        }
        else {
            Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
        }
    }
}

New-Item -ItemType Directory -Force -Path $dotnetCliHome | Out-Null
New-Item -ItemType Directory -Force -Path $nugetPackagesPath | Out-Null
New-Item -ItemType Directory -Force -Path $profileRoot | Out-Null
New-Item -ItemType Directory -Force -Path $appDataPath | Out-Null
$env:DOTNET_CLI_HOME = $dotnetCliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_NOLOGO = "1"
$env:NUGET_PACKAGES = $nugetPackagesPath
$env:HOME = $profileRoot
$env:USERPROFILE = $profileRoot
$env:APPDATA = $appDataPath

if (Test-Path $outputDirectory) {
    Backup-PublishState -OutputDirectory $outputDirectory -BackupDirectory $stateBackupRoot
    $resolvedOutputDirectory = (Resolve-Path $outputDirectory).Path
    if (-not $resolvedOutputDirectory.StartsWith($scriptRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理工作区之外的输出目录：$resolvedOutputDirectory"
    }

    Get-ChildItem -LiteralPath $resolvedOutputDirectory -Force | Remove-Item -Recurse -Force
}
else {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

Write-Host "正在发布依赖系统运行时的 NetTest 网络诊断套件（win-x64）..." -ForegroundColor Cyan
dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained false `
    --configfile $nugetConfigPath `
    -p:UseAppHost=true `
    -p:UseSharedCompilation=false `
    -o $outputDirectory

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码：$LASTEXITCODE。"
}

Copy-Item -LiteralPath $distLauncherPs1 -Destination (Join-Path $outputDirectory "start.ps1") -Force
Copy-Item -LiteralPath $distLauncherCmd -Destination (Join-Path $outputDirectory "start.cmd") -Force
Restore-PublishState -BackupDirectory $stateBackupRoot -OutputDirectory $outputDirectory

Write-Host "发布输出目录：" -ForegroundColor Cyan
Write-Host "  $outputDirectory" -ForegroundColor Green
