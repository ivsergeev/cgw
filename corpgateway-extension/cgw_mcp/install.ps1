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
    Get-Process -Name 'node' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host 'Service removed' -ForegroundColor Green
    exit 0
}

$Action = New-ScheduledTaskAction -Execute $NodeBin -Argument $Entry -WorkingDirectory $ScriptDir

$Trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

$Settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit (New-TimeSpan -Days 365)

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $Action `
    -Trigger $Trigger `
    -Settings $Settings `
    -Description 'CorpGateway MCP Server' `
    -RunLevel Limited

Write-Host 'Installed scheduled task' -ForegroundColor Green

Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 2

Write-Host 'Service started' -ForegroundColor Green
Write-Host ''
Write-Host 'Commands:' -ForegroundColor Cyan
Write-Host '  Get-ScheduledTask -TaskName cgw-mcp'
Write-Host '  Start-ScheduledTask -TaskName cgw-mcp'
Write-Host '  Stop-ScheduledTask -TaskName cgw-mcp'
Write-Host '  .\install.ps1 -Uninstall'

Start-Sleep -Seconds 1
if (Test-Path $ConfigPath) {
    $cfg = Get-Content $ConfigPath -Raw | ConvertFrom-Json
    $LogPath = Join-Path $ConfigDir 'logs'
    Write-Host ''
    Write-Host ('Config: ' + $ConfigPath) -ForegroundColor White
    Write-Host ('Logs:   ' + $LogPath) -ForegroundColor White
    Write-Host ''
    Write-Host ('Port:            ' + $cfg.port) -ForegroundColor White
    Write-Host ('Agent token:     ' + $cfg.token) -ForegroundColor Yellow
    Write-Host ('Extension token: ' + $cfg.extensionToken) -ForegroundColor Yellow
}
