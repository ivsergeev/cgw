# install.ps1 — build cgw and install to %USERPROFILE%\bin
param(
    [string]$InstallDir = "$env:USERPROFILE\.local\bin"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$CliDir = Join-Path $ScriptDir "CorpGatewayCli"

Write-Host "Building cgw..." -ForegroundColor Cyan

Push-Location $CliDir
try {
    dotnet publish -c Release -r win-x64 --self-contained -o .\dist\win-x64 `
        -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false
} finally {
    Pop-Location
}

$Binary = Join-Path $CliDir "dist\win-x64\cgw.exe"
if (-not (Test-Path $Binary)) {
    Write-Error "Build failed: binary not found at $Binary"
    exit 1
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $Binary (Join-Path $InstallDir "cgw.exe") -Force

Write-Host ""
Write-Host "Installed to $InstallDir\cgw.exe" -ForegroundColor Green

# Check PATH
$CurrentPath = [Environment]::GetEnvironmentVariable("PATH", "User")
if ($CurrentPath -notlike "*$InstallDir*") {
    [Environment]::SetEnvironmentVariable(
        "PATH",
        "$CurrentPath;$InstallDir",
        "User"
    )
    Write-Host "Added $InstallDir to user PATH" -ForegroundColor Yellow
    Write-Host "Restart your terminal for PATH changes to take effect."
}

Write-Host ""
Write-Host "Test: cgw health" -ForegroundColor Cyan
