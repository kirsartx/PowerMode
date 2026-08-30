[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [switch]$CreateZip,
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$rootPath = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Root).Path)
$rootPrefix = if ($rootPath.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
    $rootPath
} else {
    $rootPath + [System.IO.Path]::DirectorySeparatorChar
}
$solution = Join-Path $rootPath "PowerMode.slnx"
$project = Join-Path $rootPath "src\PowerMode.App\PowerMode.App.csproj"
$cliSource = Join-Path $rootPath "src\PowerMode.Cli\PowerModeSwitcher.bat"
$engineSource = Join-Path $rootPath "src\PowerMode.Cli\PowerMode.Engine.ps1"
$readmeSource = Join-Path $rootPath "README.md"
$publishScriptSource = Join-Path $rootPath "scripts\Publish-Portable.ps1"
$dist = Join-Path $rootPath "dist"
$output = Join-Path $dist "PowerMode-win-x64"
$staging = Join-Path $dist ".PowerMode-win-x64.staging"
$backup = Join-Path $dist ".PowerMode-win-x64.backup"
$zip = Join-Path $dist "PowerMode-win-x64.zip"
$zipSidecar = Join-Path $dist "PowerMode-win-x64.zip.sha256"
$stagingZip = Join-Path $dist ".PowerMode-win-x64.staging.zip"
$stagingZipSidecar = Join-Path $dist ".PowerMode-win-x64.staging.zip.sha256"
$zipBackup = Join-Path $dist ".PowerMode-win-x64.zip.backup"
$zipSidecarBackup = Join-Path $dist ".PowerMode-win-x64.zip.sha256.backup"
$appOutput = Join-Path $staging "App"

function Assert-WorkspaceChild([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ($fullPath -ne $rootPath -and
        -not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the workspace: $fullPath"
    }
}

function Assert-NoReparsePoint([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootItem = Get-Item -LiteralPath $rootPath -Force -ErrorAction Stop
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to use a reparse-point workspace root: $rootPath"
    }
    $relative = if ($fullPath.Length -gt $rootPath.Length) {
        $fullPath.Substring($rootPath.Length).TrimStart([char[]]"\/")
    } else {
        ''
    }
    $current = $rootPath
    $parts = if ([string]::IsNullOrWhiteSpace($relative)) {
        @()
    } else {
        $relative -split '[\\/]'
    }
    foreach ($part in $parts) {
        if ([string]::IsNullOrWhiteSpace($part)) {
            continue
        }
        $current = Join-Path $current $part
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to use a reparse-point path inside the workspace: $current"
            }
        }
    }
}

foreach ($path in @($dist, $output, $staging, $backup, $zip, $zipSidecar,
        $stagingZip, $stagingZipSidecar, $zipBackup, $zipSidecarBackup, $appOutput)) {
    Assert-WorkspaceChild $path
    Assert-NoReparsePoint $path
}
if (-not (Test-Path -LiteralPath $solution -PathType Leaf)) {
    throw "Solution not found: $solution"
}
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Project not found: $project"
}
foreach ($source in @($cliSource, $engineSource, $readmeSource)) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Publish source not found: $source"
    }
}

if (-not $SkipTests) {
    & dotnet test $solution -c Release -p:Platform=x64 -m:1 `
        -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw "Release tests failed with exit code $LASTEXITCODE."
    }
}

New-Item -ItemType Directory -Path $dist -Force | Out-Null
if (Test-Path -LiteralPath $backup) {
    if (Test-Path -LiteralPath $output) {
        throw "A previous portable publish left both output and backup directories. Review and remove $backup only after confirming $output is complete."
    } else {
        Move-Item -LiteralPath $backup -Destination $output
    }
}
if ((Test-Path -LiteralPath $zipBackup) -or (Test-Path -LiteralPath $zipSidecarBackup)) {
    $hasZipBackup = Test-Path -LiteralPath $zipBackup
    $hasZipSidecarBackup = Test-Path -LiteralPath $zipSidecarBackup
    $hasZip = Test-Path -LiteralPath $zip
    $hasZipSidecar = Test-Path -LiteralPath $zipSidecar
    if ($hasZipBackup -and $hasZipSidecarBackup -and -not $hasZip -and -not $hasZipSidecar) {
        Move-Item -LiteralPath $zipBackup -Destination $zip
        Move-Item -LiteralPath $zipSidecarBackup -Destination $zipSidecar
    } else {
        throw "A previous ZIP publish left an incomplete backup state. Review $zip, $zipSidecar, $zipBackup, and $zipSidecarBackup before retrying."
    }
}
foreach ($path in @($staging, $stagingZip, $stagingZipSidecar)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

& dotnet publish $project -c Release -r win-x64 --self-contained true -p:Platform=x64 `
    -p:DebugType=None -p:DebugSymbols=false -o $appOutput
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

foreach ($name in @("PowerModeSwitcher.bat", "PowerMode.Engine.ps1", "README.md")) {
    $publishedCopy = Join-Path $appOutput $name
    if (Test-Path -LiteralPath $publishedCopy) {
        Remove-Item -LiteralPath $publishedCopy -Force
    }
}
$cliDestination = Join-Path $staging "PowerModeSwitcher.bat"
$engineDestination = Join-Path $staging "PowerMode.Engine.ps1"
$readmeDestination = Join-Path $staging "README.md"
Copy-Item -LiteralPath $cliSource -Destination $cliDestination -Force
Copy-Item -LiteralPath $engineSource -Destination $engineDestination -Force
Copy-Item -LiteralPath $readmeSource -Destination $readmeDestination -Force

$sourceCliHash = (Get-FileHash -LiteralPath $cliSource -Algorithm SHA256).Hash
$publishedCliHash = (Get-FileHash -LiteralPath $cliDestination -Algorithm SHA256).Hash
if ($sourceCliHash -ne $publishedCliHash) {
    throw "Published CLI integrity check failed."
}
$sourceEngineHash = (Get-FileHash -LiteralPath $engineSource -Algorithm SHA256).Hash
$publishedEngineHash = (Get-FileHash -LiteralPath $engineDestination -Algorithm SHA256).Hash
if ($sourceEngineHash -ne $publishedEngineHash) {
    throw "Published PowerShell engine integrity check failed."
}
$sourceReadmeHash = (Get-FileHash -LiteralPath $readmeSource -Algorithm SHA256).Hash
$publishedReadmeHash = (Get-FileHash -LiteralPath $readmeDestination -Algorithm SHA256).Hash
if ($sourceReadmeHash -ne $publishedReadmeHash) {
    throw "Published README integrity check failed."
}

$launcher = @'
@echo off
start "" "%~dp0App\PowerMode.exe" %*
'@
[System.IO.File]::WriteAllText(
    (Join-Path $staging "00-START PowerMode.bat"),
    $launcher.TrimStart() + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

$executable = Join-Path $appOutput "PowerMode.exe"
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published executable not found: $executable"
}
$executableItem = Get-Item -LiteralPath $executable -ErrorAction Stop
$productVersion = $executableItem.VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($productVersion)) {
    throw "Published executable has no product version."
}
$executableHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
$publishScriptHash = (Get-FileHash -LiteralPath $publishScriptSource -Algorithm SHA256).Hash

$gitCommit = & git -C $rootPath rev-parse HEAD
if ($LASTEXITCODE -ne 0) {
    throw "Unable to read the source commit with exit code $LASTEXITCODE."
}
$gitStatus = & git -C $rootPath status --porcelain
if ($LASTEXITCODE -ne 0) {
    throw "Unable to read source dirty state with exit code $LASTEXITCODE."
}
$commit = ($gitCommit -join [Environment]::NewLine).Trim()
$dirty = -not [string]::IsNullOrWhiteSpace(($gitStatus -join [Environment]::NewLine))

$forbiddenFiles = @(Get-ChildItem -LiteralPath $appOutput -Recurse -File | Where-Object {
    $forbiddenPdbPattern = "*.pdb"
    $forbiddenTestDllPattern = "*Tests*.dll"
    $forbiddenDirectories = @("obj", "ref", "refint")
    $hasForbiddenDirectory = $false
    foreach ($directoryName in $forbiddenDirectories) {
        if ($_.FullName -match "[\\/]$directoryName([\\/]|$)") {
            $hasForbiddenDirectory = $true
            break
        }
    }
    return $_.Name -like $forbiddenPdbPattern -or
        $_.Name -like $forbiddenTestDllPattern -or
        $hasForbiddenDirectory
})
if ($forbiddenFiles.Count -gt 0) {
    throw "Portable App output contains forbidden development artifacts: $($forbiddenFiles.FullName -join ', ')"
}

$buildInfo = [ordered]@{
    version = "1.1.0"
    productVersion = $productVersion
    commit = $commit
    dirty = $dirty
    builtAtUtc = [DateTime]::UtcNow.ToString("O")
    executable = "App/PowerMode.exe"
    runtime = "win-x64"
    selfContained = $true
    executableSha256 = $executableHash
    cliSha256 = $publishedCliHash
    engineSha256 = $publishedEngineHash
    readmeSha256 = $publishedReadmeHash
    publishScriptSha256 = $publishScriptHash
}
[System.IO.File]::WriteAllText(
    (Join-Path $staging "build-info.json"),
    ($buildInfo | ConvertTo-Json) + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

$runningFromOutput = Get-Process -Name PowerMode, PowerModeWinUI -ErrorAction SilentlyContinue | Where-Object {
    try {
        $_.Path -and [System.IO.Path]::GetFullPath($_.Path).StartsWith(
            $output + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        $false
    }
}
if ($runningFromOutput) {
    throw "PowerMode is running from the portable folder. Exit it and build again."
}

if (Test-Path -LiteralPath $output) {
    Move-Item -LiteralPath $output -Destination $backup
}
try {
    Move-Item -LiteralPath $staging -Destination $output
}
catch {
    if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $output)) {
        Move-Item -LiteralPath $backup -Destination $output
    }
    throw
}
if (Test-Path -LiteralPath $backup) {
    Remove-Item -LiteralPath $backup -Recurse -Force
}

if ($CreateZip) {
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $stagingZip -CompressionLevel Optimal
    $zipHash = (Get-FileHash -LiteralPath $stagingZip -Algorithm SHA256).Hash
    [System.IO.File]::WriteAllText(
        $stagingZipSidecar,
        "$zipHash  PowerMode-win-x64.zip$([Environment]::NewLine)",
        [System.Text.UTF8Encoding]::new($false))
    $hadZip = Test-Path -LiteralPath $zip
    $hadZipSidecar = Test-Path -LiteralPath $zipSidecar
    try {
        if ($hadZip) {
            Move-Item -LiteralPath $zip -Destination $zipBackup
        }
        if ($hadZipSidecar) {
            Move-Item -LiteralPath $zipSidecar -Destination $zipSidecarBackup
        }
        Move-Item -LiteralPath $stagingZip -Destination $zip
        Move-Item -LiteralPath $stagingZipSidecar -Destination $zipSidecar
        $installedZipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
        if ($installedZipHash -ne $zipHash) {
            throw "Installed portable archive failed its SHA-256 integrity check."
        }
    }
    catch {
        if (Test-Path -LiteralPath $zip) {
            Remove-Item -LiteralPath $zip -Force
        }
        if (Test-Path -LiteralPath $zipSidecar) {
            Remove-Item -LiteralPath $zipSidecar -Force
        }
        if ($hadZip -and (Test-Path -LiteralPath $zipBackup)) {
            Move-Item -LiteralPath $zipBackup -Destination $zip
        }
        if ($hadZipSidecar -and (Test-Path -LiteralPath $zipSidecarBackup)) {
            Move-Item -LiteralPath $zipSidecarBackup -Destination $zipSidecar
        }
        throw
    }
    if (Test-Path -LiteralPath $zipBackup) {
        Remove-Item -LiteralPath $zipBackup -Force
    }
    if (Test-Path -LiteralPath $zipSidecarBackup) {
        Remove-Item -LiteralPath $zipSidecarBackup -Force
    }
}

Write-Host "PowerMode portable build: $output"
if ($CreateZip) {
    Write-Host "PowerMode portable archive: $zip"
}
