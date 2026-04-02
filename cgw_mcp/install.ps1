param(
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$ScriptDir = $PSScriptRoot
$TaskName = 'cgw-mcp'
$NodeBin = (Get-Command node -ErrorAction SilentlyContinue).Source
$Entry = Join-Path $ScriptDir 'index.js'
$ConfigDir = Join-Path $env:USERPROFILE '.corpgateway'
$ConfigPath = Join-Path $ConfigDir 'cgw_mcp.json'

if (-not $NodeBin) {
    Write-Host 'Error: node not found in PATH' -ForegroundColor Red
    Write-Host 'Install Node.js 18+: https://nodejs.org'
    exit 1
}

if (-not (Test-Path $Entry)) {
    Write-Host ('Error: ' + $Entry + ' not found') -ForegroundColor Red
    exit 1
}

$ModulesDir = Join-Path $ScriptDir 'node_modules'
if (-not (Test-Path $ModulesDir)) {
    Write-Host 'Installing dependencies...'
    Push-Location $ScriptDir
    npm install --production
    Pop-Location
}

if ($Uninstall) {
    # Stop daemon
    & $NodeBin $Entry stop

    # Remove scheduled task (if exists from old install)
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

    Write-Host 'Service removed' -ForegroundColor Green
    exit 0
}

# Stop old instance if running
& $NodeBin $Entry stop 2>$null

# Remove old scheduled task (if exists from previous install method)
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

# Create scheduled task that starts the daemon at logon
# "start" mode spawns a detached background process (windowsHide: true) and exits
$Action = New-ScheduledTaskAction -Execute $NodeBin -Argument "`"$Entry`" start" -WorkingDirectory $ScriptDir

$Trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

$Settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $Action `
    -Trigger $Trigger `
    -Settings $Settings `
    -Description 'CorpGateway MCP Server — start daemon at logon' `
    -RunLevel Limited

Write-Host 'Installed scheduled task (starts daemon at logon)' -ForegroundColor Green

# Start daemon now
& $NodeBin $Entry start

Write-Host ''
Write-Host 'Commands:' -ForegroundColor Cyan
Write-Host ('  node ' + $Entry + ' status    — check status')
Write-Host ('  node ' + $Entry + ' stop      — stop daemon')
Write-Host ('  node ' + $Entry + ' start     — start daemon')
Write-Host ('  node ' + $Entry + ' restart   — restart daemon')
Write-Host '  .\install.ps1 -Uninstall        — remove autostart'
