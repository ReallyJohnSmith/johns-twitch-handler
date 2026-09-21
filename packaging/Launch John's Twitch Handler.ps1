$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appPath = Join-Path $packageRoot 'JohnsTwitchHandler.exe'
$dashboardUrl = 'http://127.0.0.1:5274'
$logRoot = Join-Path $env:LOCALAPPDATA 'StreamSidekick\logs'

function Test-HandlerHealth {
    try {
        $health = Invoke-RestMethod "$dashboardUrl/api/health" -TimeoutSec 2
        return $health.ok -eq $true
    }
    catch { return $false }
}

try {
    # Reopening the launcher is a clean restart. This prevents an old copy from holding the port and
    # guarantees that the browser shows the build in this folder.
    Get-Process -Name @("John's Twitch Handler", 'StreamSidekick', 'JohnsTwitchHandler') -ErrorAction SilentlyContinue |
        Stop-Process -Force

    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    Start-Process -FilePath $appPath `
        -ArgumentList @('--urls', $dashboardUrl) `
        -WorkingDirectory $packageRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $logRoot 'handler.log') `
        -RedirectStandardError (Join-Path $logRoot 'handler-error.log') | Out-Null

    for ($attempt = 0; $attempt -lt 40 -and -not (Test-HandlerHealth); $attempt++) {
        Start-Sleep -Milliseconds 500
    }
    if (-not (Test-HandlerHealth)) {
        throw "The dashboard did not start. Send John the safe diagnostic report or the file $logRoot\handler-error.log."
    }

    Start-Process $dashboardUrl
}
catch {
    Write-Host "John's Twitch Handler could not start: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'Press Enter after reading this message.' -ForegroundColor Yellow
    Read-Host | Out-Null
    exit 1
}
