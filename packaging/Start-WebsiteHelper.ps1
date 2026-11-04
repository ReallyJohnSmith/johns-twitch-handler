$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$helperUrl = 'http://127.0.0.1:5274'
function Test-WebsiteHelper {
    try { return (Invoke-RestMethod "$helperUrl/bridge/status" -TimeoutSec 2).protocol -eq 1 }
    catch { return $false }
}
# Starting from Windows sign-in or the website must never restart an active stream's helper.
if (Test-WebsiteHelper) { exit 0 }
$logRoot = Join-Path $env:LOCALAPPDATA 'StreamSidekick\logs'
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
Start-Process -FilePath (Join-Path $packageRoot 'JohnsTwitchHandler.exe') `
    -ArgumentList @('--urls', $helperUrl) -WorkingDirectory $packageRoot -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $logRoot 'website-helper.log') `
    -RedirectStandardError (Join-Path $logRoot 'website-helper-error.log') | Out-Null
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    if (Test-WebsiteHelper) { exit 0 }
    Start-Sleep -Milliseconds 500
}
throw 'The Windows helper did not start. Open the Handler shortcut for connection help.'
