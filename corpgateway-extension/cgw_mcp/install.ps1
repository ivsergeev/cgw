# cgw_mcp daemon installer (Windows)
#
# Installs cgw_mcp as a Windows startup task (Task Scheduler)
# Runs at user logon, restarts on failure.
#
# Usage:
#   .\install.ps1              # install and start
#   .\install.ps1 -Uninstall   # stop and remove

param(
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

$ScriptDir = $PSScriptRoot
$TaskName = "cgw-mcp"
$NodeBin = (Get-Command node -ErrorAction SilentlyContinue).Source
$Entry = Join-Path $ScriptDir "index.js"
$ConfigDir = Join-Path $env:USERPROFILE ".corpgateway"
$ConfigPath = Join-Path $ConfigDir "cgw_mcp.json"

# ── Checks ──────────────────────────────────────────────────

if (-not $NodeBin) {
    Write-Host "Error: node not found in PATH" -ForegroundColor Red
    Write-Host "Install Node.js 18+: https://nodejs.org"
    exit 1
}

if (-not (Test-Path $Entry)) {
    Write-Host "Error: $Entry not found" -ForegroundColor Red
    exit 1
}

# Install npm deps if needed
if (-not (Test-Path (Join-Path $ScriptDir "node_modules"))) {
    Write-Host "Installing dependencies..."
    Push-Location $ScriptDir
    npm install --production
    Pop-Location
}

# ── Uninstall ───────────────────────────────────────────────

if ($Uninstall) {
    # Stop running process
    Get-Process -Name "node" -ErrorAction SilentlyContinue | Where-Object {
        try { $_.CommandLine -match "cgw_mcp" } catch { $false }
    } | Stop-Process -Force -ErrorAction SilentlyContinue

    # Remove scheduled task
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

    Write-Host "Service removed" -ForegroundColor Green
    exit 0
}

# ── Install ─────────────────────────────────────────────────

# Create scheduled task that runs at logon
$Action = New-ScheduledTaskAction `
    -Execute $NodeBin `
    -Argument "`"$Entry`"" `
    -WorkingDirectory $ScriptDir

$Trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

$Settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit (New-TimeSpan -Days 365)

# Remove old task if exists
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $Action `
    -Trigger $Trigger `
    -Settings $Settings `
    -Description "CorpGateway MCP Server — HTTP + WebSocket daemon for Chrome extension" `
    -RunLevel Limited

Write-Host "Installed scheduled task: $TaskName" -ForegroundColor Green

# Start immediately
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 2

Write-Host "Service started" -ForegroundColor Green
Write-Host ""
Write-Host "Commands:" -ForegroundColor Cyan
Write-Host "  Get-ScheduledTask -TaskName $TaskName"
Write-Host "  Start-ScheduledTask -TaskName $TaskName"
Write-Host "  Stop-ScheduledTask -TaskName $TaskName"
Write-Host "  .\install.ps1 -Uninstall"

# Show config
Start-Sleep -Seconds 1
if (Test-Path $ConfigPath) {
    $cfg = Get-Content $ConfigPath | ConvertFrom-Json
    Write-Host ""
    Write-Host "Config: $ConfigPath" -ForegroundColor White
    Write-Host "Logs:   $ConfigDir\logs\" -ForegroundColor White
    Write-Host ""
    Write-Host "Port:            $($cfg.port)" -ForegroundColor White
    Write-Host "Agent token:     $($cfg.token)" -ForegroundColor Yellow
    Write-Host "Extension token: $($cfg.extensionToken)" -ForegroundColor Yellow
}
