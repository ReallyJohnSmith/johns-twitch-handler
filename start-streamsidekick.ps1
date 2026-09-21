$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot 'src\StreamSidekick\StreamSidekick.csproj'
$sourceRoot = Join-Path $projectRoot 'src\StreamSidekick'
$appPath = Join-Path $sourceRoot 'bin\Debug\net9.0\JohnsTwitchHandler.exe'
$dashboardUrl = 'http://127.0.0.1:5274'
$logRoot = Join-Path $projectRoot 'logs'
$standardLog = Join-Path $logRoot 'johns-twitch-handler.log'
$errorLog = Join-Path $logRoot 'johns-twitch-handler-error.log'

function Test-JohnsTwitchHandler {
    try {
        $health = Invoke-RestMethod "$dashboardUrl/api/health" -TimeoutSec 2
        return $health.ok -eq $true
    }
    catch {
        return $false
    }
}

try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw ".NET is required to run John's Twitch Handler."
    }

    # A shortcut launch is an intentional clean restart. Closing every process with this
    # dedicated executable name prevents an older build from keeping the local port.
    $runningCopies = Get-Process -Name @('StreamSidekick', "John's Twitch Handler", 'JohnsTwitchHandler') -ErrorAction SilentlyContinue
    if ($runningCopies) {
        Write-Host "Closing other John's Twitch Handler copies..."
        $runningCopies | Stop-Process -Force
        $runningCopies | Wait-Process -Timeout 8 -ErrorAction SilentlyContinue
    }

    Write-Host "Building the latest John's Twitch Handler source..."
    # Dependencies are already restored with the project; avoiding an automatic restore also
    # prevents a locked or protected user-level NuGet.Config from blocking normal startup.
    dotnet build $projectFile --nologo --no-restore
    if ($LASTEXITCODE -ne 0) { throw "The John's Twitch Handler build failed. Review the error shown above." }

    Write-Host "Starting John's Twitch Handler..."
    Write-Host "Dashboard: $dashboardUrl"
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    # Run independently and hidden so the shortcut can finish without leaving a console window.
    Start-Process -FilePath $appPath `
        -ArgumentList @('--urls', $dashboardUrl) `
        -WorkingDirectory $sourceRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $standardLog `
        -RedirectStandardError $errorLog | Out-Null

    # Wait for a real health response instead of assuming that a spawned process started correctly.
    for ($attempt = 0; $attempt -lt 30 -and -not (Test-JohnsTwitchHandler); $attempt++) {
        Start-Sleep -Milliseconds 500
    }
    if (-not (Test-JohnsTwitchHandler)) {
        $detail = if (Test-Path -LiteralPath $errorLog) {
            (Get-Content -LiteralPath $errorLog -Tail 8 -ErrorAction SilentlyContinue) -join ' '
        } else { 'No detailed error was written.' }
        throw "The app did not become ready. $detail Check $errorLog for details."
    }

    Write-Host "John's Twitch Handler is ready. Opening your dashboard..."
    try {
        Start-Process $dashboardUrl
    }
    catch {
        # The server is healthy even if Windows has no default browser association.
        Write-Host "Windows could not open the browser automatically. Open $dashboardUrl manually." -ForegroundColor Yellow
    }
    Start-Sleep -Seconds 1
}
catch {
    Write-Host ''
    Write-Host "John's Twitch Handler could not start: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'Press Enter after reading this message.' -ForegroundColor Yellow
    Read-Host | Out-Null
    exit 1
}
