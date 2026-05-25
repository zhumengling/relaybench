param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "release",
    [switch]$IncludeSelfContained
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSCommandPath
$projectPath = Join-Path $repoRoot "RelayBench.WinUI\RelayBench.WinUI.csproj"
$nugetConfigPath = Join-Path $repoRoot "NuGet.Config"
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$resolvedOutputRoot = Join-Path $repoRoot $OutputRoot
$localDotnet = Join-Path $repoRoot ".codex-tools\dotnet-sdk\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { "dotnet" }

Push-Location $repoRoot

try {
    if ($dotnet -eq "dotnet" -and -not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "dotnet was not found. Please install .NET SDK 10 first."
    }

    [xml]$props = Get-Content -LiteralPath $propsPath
    $version = $props.Project.PropertyGroup.Version

    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "Failed to read version from Directory.Build.props."
    }

    $targets = @(
        @{
            Name = "framework-dependent"
            SelfContained = $false
            Description = "Requires local .NET Desktop Runtime 10 and Windows App Runtime 2.0; keeps the package smallest"
        }
    )

    if ($IncludeSelfContained) {
        $targets += @{
            Name = "self-contained"
            SelfContained = $true
            Description = "Bundles .NET runtime and results in a larger package; Windows App Runtime 2.0 is still required"
        }
    }

    New-Item -ItemType Directory -Path $resolvedOutputRoot -Force | Out-Null

    foreach ($target in $targets) {
        $packageName = "relaybench-v$version-$Runtime-winui-$($target.Name)"
        $publishDirectory = Join-Path $resolvedOutputRoot $packageName
        $zipPath = Join-Path $resolvedOutputRoot "$packageName.zip"
        $hashPath = Join-Path $resolvedOutputRoot "$packageName.sha256.txt"

        New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

        if (Test-Path -LiteralPath $zipPath) {
            Remove-Item -LiteralPath $zipPath -Force
        }

        if (Test-Path -LiteralPath $hashPath) {
            Remove-Item -LiteralPath $hashPath -Force
        }

        Write-Host "==> Publishing $packageName"

        & $dotnet publish $projectPath `
            -c $Configuration `
            -r $Runtime `
            --self-contained $($target.SelfContained.ToString().ToLowerInvariant()) `
            --configfile $nugetConfigPath `
            -p:UseAppHost=true `
            -o $publishDirectory

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed: $packageName"
        }

        Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $zipPath -CompressionLevel Optimal -Force

        $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -LiteralPath $hashPath -Value "$hash  $([System.IO.Path]::GetFileName($zipPath))" -Encoding utf8

        $zipSizeMb = [math]::Round(((Get-Item -LiteralPath $zipPath).Length / 1MB), 2)
        Write-Host "    Created: $(Split-Path -Leaf $zipPath) ($zipSizeMb MB)"
        Write-Host "    Notes: $($target.Description)"
    }

    Write-Host ""
    Write-Host "Output directory: $resolvedOutputRoot"
    Write-Host "Generated release assets:"
    Write-Host "  - relaybench-v$version-$Runtime-winui-framework-dependent.zip"
    Write-Host "  - relaybench-v$version-$Runtime-winui-framework-dependent.sha256.txt"
    if ($IncludeSelfContained) {
        Write-Host "  - relaybench-v$version-$Runtime-winui-self-contained.zip"
        Write-Host "  - relaybench-v$version-$Runtime-winui-self-contained.sha256.txt"
    }
    Write-Host ""
    Write-Host "Runtime note: because RelayBench.WinUI is an unpackaged WinUI app, target machines need Windows App Runtime 2.0 in addition to .NET unless deployment is changed to package/include the Windows App SDK runtime."
}
finally {
    Pop-Location
}
