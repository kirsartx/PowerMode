[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [switch]$CreateZip,
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$rootPath = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Root).Path).TrimEnd('\')
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
$appOutput = Join-Path $staging "App"

function Assert-WorkspaceChild([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $prefix = $rootPath + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the workspace: $fullPath"
    }
}

foreach ($path in @($dist, $output, $staging, $backup, $zip, $zipSidecar, $stagingZip, $appOutput)) {
    Assert-WorkspaceChild $path
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
foreach ($path in @($staging, $backup, $stagingZip)) {
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
    if (Test-Path -LiteralPath $zip) {
        Remove-Item -LiteralPath $zip -Force
    }
    if (Test-Path -LiteralPath $zipSidecar) {
        Remove-Item -LiteralPath $zipSidecar -Force
    }
    Move-Item -LiteralPath $stagingZip -Destination $zip
    $zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    [System.IO.File]::WriteAllText(
        $zipSidecar,
        "$zipHash  PowerMode-win-x64.zip$([Environment]::NewLine)",
        [System.Text.UTF8Encoding]::new($false))
}

Write-Host "PowerMode portable build: $output"
if ($CreateZip) {
    Write-Host "PowerMode portable archive: $zip"
}
