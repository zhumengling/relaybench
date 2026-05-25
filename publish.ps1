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

function Organize-PublishDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishDirectory
    )

    $exeName = "RelayBench.WinUI.exe"
    $assemblyName = "RelayBench.WinUI.dll"
    $appHostPath = Join-Path $PublishDirectory $exeName
    $appDirectory = Join-Path $PublishDirectory "app"
    $symbolsDirectory = Join-Path $PublishDirectory "symbols"

    if (-not (Test-Path -LiteralPath $appHostPath)) {
        throw "Missing apphost executable: $appHostPath"
    }

    New-Item -ItemType Directory -Path $appDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $symbolsDirectory -Force | Out-Null

    Set-AppHostManagedAssemblyPath `
        -ExePath $appHostPath `
        -OriginalAssemblyName $assemblyName `
        -RelativeAssemblyPath "app\$assemblyName"

    Get-ChildItem -LiteralPath $PublishDirectory -File | ForEach-Object {
        if ($_.Name -eq $exeName) {
            return
        }

        $destination = if ($_.Extension -ieq ".pdb") { $symbolsDirectory } else { $appDirectory }
        Move-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
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
        $zipPath = Join-Path $resolvedOutputRoot "$packageName.zip"
        $hashPath = Join-Path $resolvedOutputRoot "$packageName.sha256.txt"

        Reset-Directory -Path $publishDirectory -Root $resolvedOutputRoot

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

        Organize-PublishDirectory -PublishDirectory $publishDirectory

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
