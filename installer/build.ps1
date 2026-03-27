# CorpGateway Installer Build Script
# Prerequisites: .NET 8 SDK, Inno Setup (ISCC in PATH or default location)

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent
$PublishDir = "$PSScriptRoot\publish"
$OutputDir = "$PSScriptRoot\output"

Write-Host "=== CorpGateway Installer Build ===" -ForegroundColor Cyan

# Clean
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }

# Publish Gateway
Write-Host "`n[1/3] Publishing Gateway..." -ForegroundColor Yellow
dotnet publish "$Root\CorpGateway\CorpGateway.csproj" `
    -c $Configuration -r $Runtime --self-contained `
    -o "$PublishDir\gateway"
if ($LASTEXITCODE -ne 0) { throw "Gateway publish failed" }

# Publish CLI
Write-Host "`n[2/3] Publishing CLI..." -ForegroundColor Yellow
dotnet publish "$Root\CorpGatewayCli\CorpGatewayCli.csproj" `
    -c $Configuration -r $Runtime --self-contained `
    -o "$PublishDir\cli"
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }

# Find Inno Setup compiler
$IsccPaths = @(
    "ISCC",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
)
$Iscc = $null
foreach ($p in $IsccPaths) {
    if (Get-Command $p -ErrorAction SilentlyContinue) { $Iscc = $p; break }
    if (Test-Path $p) { $Iscc = $p; break }
}

if (-not $Iscc) {
    Write-Host "`n[!] Inno Setup not found. Published files are in:" -ForegroundColor Yellow
    Write-Host "    Gateway: $PublishDir\gateway" -ForegroundColor White
    Write-Host "    CLI:     $PublishDir\cli" -ForegroundColor White
    Write-Host "    Install Inno Setup 6 and run this script again to create the installer." -ForegroundColor Gray
    exit 0
}

# Build installer
Write-Host "`n[3/3] Building installer with Inno Setup..." -ForegroundColor Yellow
& $Iscc "$PSScriptRoot\setup.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed" }

$SetupFile = Get-ChildItem "$OutputDir\*.exe" | Select-Object -First 1
Write-Host "`n=== Done! ===" -ForegroundColor Green
Write-Host "Installer: $($SetupFile.FullName)" -ForegroundColor White
Write-Host "Size: $([math]::Round($SetupFile.Length / 1MB, 1)) MB" -ForegroundColor White
