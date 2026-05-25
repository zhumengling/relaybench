param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "release",
    [switch]$IncludeSelfContained
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSCommandPath
$projectPath = Join-Path $repoRoot "RelayBench.WinUI\RelayBench.WinUI.csproj"
$launcherProjectPath = Join-Path $repoRoot "RelayBench.Launcher\RelayBench.Launcher.csproj"
$nugetConfigPath = Join-Path $repoRoot "NuGet.Config"
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$resolvedOutputRoot = Join-Path $repoRoot $OutputRoot
$localDotnet = Join-Path $repoRoot ".codex-tools\dotnet-sdk\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { "dotnet" }

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar

    if (-not $resolvedPath.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean path outside output root: $resolvedPath"
    }
}

function Reset-Directory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    Assert-ChildPath -Path $Path -Root $Root

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Move-SymbolsToDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,
        [Parameter(Mandatory = $true)]
        [string]$SymbolsDirectory
    )

    New-Item -ItemType Directory -Path $SymbolsDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $SourceDirectory -Filter "*.pdb" -File -ErrorAction SilentlyContinue |
        Move-Item -Destination $SymbolsDirectory -Force
}

function Set-AppHostManagedAssemblyPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExePath,
        [Parameter(Mandatory = $true)]
        [string]$OriginalAssemblyName,
        [Parameter(Mandatory = $true)]
        [string]$RelativeAssemblyPath
    )

    $bytes = [System.IO.File]::ReadAllBytes($ExePath)
    $oldBytes = [System.Text.Encoding]::UTF8.GetBytes($OriginalAssemblyName)
    $newBytes = [System.Text.Encoding]::UTF8.GetBytes($RelativeAssemblyPath)
    $matches = New-Object System.Collections.Generic.List[int]

    for ($index = 0; $index -le $bytes.Length - $oldBytes.Length; $index++) {
        $matched = $true
        for ($offset = 0; $offset -lt $oldBytes.Length; $offset++) {
            if ($bytes[$index + $offset] -ne $oldBytes[$offset]) {
                $matched = $false
                break
            }
        }

        if ($matched) {
            $matches.Add($index)
        }
    }

    if ($matches.Count -ne 1) {
        throw "Expected one apphost assembly placeholder in $ExePath, found $($matches.Count)."
    }

    $start = $matches[0]
    $capacity = $oldBytes.Length

    while (($start + $capacity) -lt $bytes.Length -and $bytes[$start + $capacity] -eq 0) {
        $capacity++
    }

    if (($newBytes.Length + 1) -gt $capacity) {
        throw "Apphost assembly path '$RelativeAssemblyPath' is too long for the embedded placeholder."
    }

    [System.Array]::Copy($newBytes, 0, $bytes, $start, $newBytes.Length)

    for ($offset = $newBytes.Length; $offset -lt $capacity; $offset++) {
        $bytes[$start + $offset] = 0
    }

    [System.IO.File]::WriteAllBytes($ExePath, $bytes)
}

function Publish-RootLauncher {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishDirectory,
        [Parameter(Mandatory = $true)]
        [string]$RootDirectory,
        [Parameter(Mandatory = $true)]
        [string]$Runtime,
        [Parameter(Mandatory = $true)]
        [bool]$SelfContained
    )

    $launcherDirectory = Join-Path $PublishDirectory "launcher"
    $launcherExePath = Join-Path $launcherDirectory "RelayBench.WinUI.exe"
    $rootExePath = Join-Path $PublishDirectory "RelayBench.WinUI.exe"
    Reset-Directory -Path $launcherDirectory -Root $PublishDirectory

    & $dotnet publish $launcherProjectPath `
        -c $Configuration `
        -r $Runtime `
        --self-contained $($SelfContained.ToString().ToLowerInvariant()) `
        --configfile $nugetConfigPath `
        -p:UseAppHost=true `
        -p:PublishSingleFile=false `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o $launcherDirectory

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed: RelayBench.Launcher"
    }

    Move-Item -LiteralPath $launcherExePath -Destination $rootExePath -Force
    Set-AppHostManagedAssemblyPath `
        -ExePath $rootExePath `
        -OriginalAssemblyName "RelayBench.WinUI.dll" `
        -RelativeAssemblyPath "launcher\RelayBench.WinUI.dll"
}

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
        $appDirectory = Join-Path $publishDirectory "app"
        $symbolsDirectory = Join-Path $publishDirectory "symbols"
        $zipPath = Join-Path $resolvedOutputRoot "$packageName.zip"
        $hashPath = Join-Path $resolvedOutputRoot "$packageName.sha256.txt"

        Reset-Directory -Path $publishDirectory -Root $resolvedOutputRoot
        New-Item -ItemType Directory -Path $appDirectory -Force | Out-Null
        New-Item -ItemType Directory -Path $symbolsDirectory -Force | Out-Null

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
            -o $appDirectory

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed: $packageName"
        }

        Move-SymbolsToDirectory -SourceDirectory $appDirectory -SymbolsDirectory $symbolsDirectory
        Publish-RootLauncher `
            -PublishDirectory $publishDirectory `
            -RootDirectory $resolvedOutputRoot `
            -Runtime $Runtime `
            -SelfContained $target.SelfContained

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
