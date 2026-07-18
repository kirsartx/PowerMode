[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [switch]$CreateZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
$project = Join-Path $rootPath "src\PowerMode.App\PowerMode.App.csproj"
$cliSource = Join-Path $rootPath "src\PowerMode.Cli\PowerModeSwitcher.bat"
$readmeSource = Join-Path $rootPath "README.md"
$dist = Join-Path $rootPath "dist"
$output = Join-Path $dist "PowerMode-win-x64"
$staging = Join-Path $dist ".PowerMode-win-x64.staging"
$backup = Join-Path $dist ".PowerMode-win-x64.backup"
$zip = Join-Path $dist "PowerMode-win-x64.zip"
$stagingZip = Join-Path $dist ".PowerMode-win-x64.staging.zip"
$appOutput = Join-Path $staging "App"

function Assert-WorkspaceChild([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $prefix = $rootPath + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the workspace: $fullPath"
    }
}

foreach ($path in @($dist, $output, $staging, $backup, $zip, $stagingZip, $appOutput)) {
    Assert-WorkspaceChild $path
}
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Project not found: $project"
}
foreach ($source in @($cliSource, $readmeSource)) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Publish source not found: $source"
    }
}

New-Item -ItemType Directory -Path $dist -Force | Out-Null
foreach ($path in @($staging, $backup, $stagingZip)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

& dotnet publish $project -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $appOutput
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

foreach ($name in @("PowerModeSwitcher.bat", "README.md")) {
    $publishedCopy = Join-Path $appOutput $name
    if (Test-Path -LiteralPath $publishedCopy) {
        Remove-Item -LiteralPath $publishedCopy -Force
    }
}
Copy-Item -LiteralPath $cliSource -Destination (Join-Path $staging "PowerModeSwitcher.bat")
Copy-Item -LiteralPath $readmeSource -Destination (Join-Path $staging "README.md")

$sourceCliHash = (Get-FileHash -LiteralPath $cliSource -Algorithm SHA256).Hash
$publishedCliHash = (Get-FileHash -LiteralPath (Join-Path $staging "PowerModeSwitcher.bat") -Algorithm SHA256).Hash
if ($sourceCliHash -ne $publishedCliHash) {
    throw "Published CLI integrity check failed."
}

$launcher = @'
@echo off
start "" "%~dp0App\PowerMode.exe" %*
'@
[System.IO.File]::WriteAllText(
    (Join-Path $staging "00-START PowerMode.bat"),
    $launcher.TrimStart() + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

$buildInfo = [ordered]@{
    builtAtUtc = [DateTime]::UtcNow.ToString("O")
    executable = "App/PowerMode.exe"
    runtime = "win-x64"
    selfContained = $true
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
    Move-Item -LiteralPath $stagingZip -Destination $zip
}

Write-Host "PowerMode portable build: $output"
if ($CreateZip) {
    Write-Host "PowerMode portable archive: $zip"
}
