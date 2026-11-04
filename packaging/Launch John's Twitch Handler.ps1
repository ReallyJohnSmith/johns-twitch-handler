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
    # Website packages reuse the running helper; failures use the same visible
    # troubleshooting message as the ordinary local launcher.
    $settingsFile = Join-Path $packageRoot 'appsettings.json'
    if (Test-Path -LiteralPath $settingsFile) {
        $websiteSettings = (Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).WebsiteBridge
        if ($websiteSettings.StartOnSignIn -and $websiteSettings.WebsiteOrigin) {
            & (Join-Path $packageRoot 'Start-WebsiteHelper.ps1')
            if ($LASTEXITCODE -ne 0) { throw 'The website helper could not start.' }
            Start-Process $websiteSettings.WebsiteOrigin
            exit 0
        }
    }
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
        throw 'The dashboard did not open. Tell John what this message says. Do not send passwords or whole log folders.'
    }

    Start-Process $dashboardUrl
}
catch {
    Write-Host "John's Twitch Handler could not start: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'Press Enter after reading this message.' -ForegroundColor Yellow
    Read-Host | Out-Null
    exit 1
}
