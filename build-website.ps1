[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$DestinationDirectory)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'src\StreamSidekick\wwwroot'
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
if ($destination.TrimEnd('\') -eq ([IO.Path]::GetFullPath($source)).TrimEnd('\')) { throw 'Website output must be separate from the local dashboard.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
# Explicit allowlist: never package credentials, settings, logs, or runtime data.
$files = @('app.js', 'styles.css', 'dashboard-runtime.js', 'layout-defaults.js', 'chat-popout.html', 'tester-guide.html', 'website-connect.js', 'website-connect.css')
foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $source $file) -Destination (Join-Path $destination $file) -Force }
$index = Get-Content -LiteralPath (Join-Path $source 'index.html') -Raw
if (-not $index.Contains('<script src="app.js?v=015"></script>')) { throw 'Dashboard entry changed; update the website build before publishing.' }
$index = $index.Replace('<script src="app.js?v=015"></script>', '<script src="website-connect.js?v=1"></script>')
$index = $index.Replace('</head>', '<link rel="stylesheet" href="website-connect.css?v=1"></head>')
$index = $index.Replace('<body data-user-mode=', '<body class="website-awaiting" data-user-mode=')
$index = $index.Replace('LOCAL ONLY · Data stays on this PC', 'CONNECTED TO YOUR PC')
$index = $index.Replace('John cannot see your settings, credentials, reports, or activity.', 'The website dashboard connects to your local Windows helper. Credentials and reports remain on your PC.')
Set-Content -LiteralPath (Join-Path $destination 'dashboard.html') -Value $index -Encoding UTF8
$welcomeFiles = @('index.html', 'welcome.css', 'welcome.js', 'welcome-config.js', 'mark.svg', 'demo.html', 'demo.css', 'demo.js')
foreach ($file in $welcomeFiles) { Copy-Item -LiteralPath (Join-Path (Join-Path $PSScriptRoot 'website') $file) -Destination (Join-Path $destination $file) -Force }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README-FOR-TESTERS.md') -Destination $destination -Force
$testerReadmePath = Join-Path $destination 'README-FOR-TESTERS.md'
$testerReadme = Get-Content -LiteralPath $testerReadmePath -Raw
Set-Content -LiteralPath $testerReadmePath -Value $testerReadme.Replace('(docs/tester-kit/README.md)', '(tester-guide.html)') -Encoding UTF8
# The plain-text download opens directly in Notepad. Its guide link stays useful
# after downloading it away from the website directory.
$websiteOrigin = (Get-Content -LiteralPath (Join-Path (Split-Path -Parent $source) 'appsettings.json') -Raw | ConvertFrom-Json).WebsiteBridge.WebsiteOrigin
$plainReadme = $testerReadme.Replace('(docs/tester-kit/README.md)', "($websiteOrigin/tester-guide.html)")
$plainReadme = $plainReadme -replace '(?m)^#{1,6} ', '' -replace '\*\*', '' -replace '(?m)^```(?:text)?\r?\n?', ''
$plainReadme = $plainReadme -replace '\[([^\]]+)\]\(([^)]+)\)', '$1: $2'
Set-Content -LiteralPath (Join-Path $destination 'README-FOR-TESTERS.txt') -Value $plainReadme -Encoding UTF8
Write-Output "Website built at $destination"
