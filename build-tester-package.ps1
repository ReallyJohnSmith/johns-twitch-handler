[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TwitchClientId,
    [string]$SpotifyClientId = '',
    [string]$TesterLabel = 'Tester',
    [string]$ReleaseVersion = '',
    [string]$WebsiteOrigin = '',
    [switch]$DevelopmentPreview,
    [switch]$EnableSpotify,
    [switch]$EnableTangia,
    [switch]$EnableStreamElements,
    [switch]$EnableTikFinity
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot 'src\StreamSidekick\StreamSidekick.csproj'
$installerProject = Join-Path $projectRoot 'src\StreamSidekick.Installer\StreamSidekick.Installer.csproj'
$packageAssets = Join-Path $projectRoot 'packaging'
$artifactRoot = Join-Path $projectRoot $(if ($DevelopmentPreview) { 'artifacts\development-packages' } else { 'artifacts\tester-packages' })
$versionFile = Join-Path $projectRoot 'RELEASE_VERSION.txt'
$effectiveVersion = if ($ReleaseVersion.Trim()) { $ReleaseVersion.Trim() } else { (Get-Content -LiteralPath $versionFile -Raw).Trim() }
if ($effectiveVersion -notmatch '^\.\d{3}$') { throw 'ReleaseVersion must use the .001 numbering format.' }
$versionTag = $effectiveVersion.TrimStart('.')
$safeTesterLabel = ($TesterLabel.Trim() -replace '[^A-Za-z0-9]+', '-').Trim('-')
if (-not $safeTesterLabel) { throw 'TesterLabel must contain at least one letter or number.' }
$workingRoot = Join-Path ([System.IO.Path]::GetTempPath()) "johns-handler-package-$([guid]::NewGuid().ToString('N'))"
$publishRoot = Join-Path $workingRoot 'payload'
$installerPublishRoot = Join-Path $workingRoot 'installer'
$payloadZip = Join-Path $workingRoot 'payload.zip'
$setupPath = Join-Path $artifactRoot "Johns-Twitch-Handler-$safeTesterLabel-v$versionTag-Setup.exe"
if ($DevelopmentPreview) {
    if (-not $WebsiteOrigin) { throw 'A website development preview requires WebsiteOrigin.' }
    if ($ReleaseVersion) { throw 'Development previews do not use an approved release number.' }
    $effectiveVersion = 'website-preview'
    $setupPath = Join-Path $artifactRoot 'Johns-Twitch-Handler-Website-Preview-Setup.exe'
}

function Test-PublicClientId([string]$value) {
    return $value -match '^[A-Za-z0-9]{16,128}$'
}

if (-not (Test-PublicClientId $TwitchClientId)) {
    throw 'TwitchClientId must be the public Client ID from the John''s Twitch Handler Twitch developer application.'
}
if ($SpotifyClientId -and -not (Test-PublicClientId $SpotifyClientId)) {
    throw 'SpotifyClientId is not a valid public Spotify application ID.'
}
if ($WebsiteOrigin) {
    $websiteUri = $null
    if (-not [Uri]::TryCreate($WebsiteOrigin, [UriKind]::Absolute, [ref]$websiteUri) -or
        $websiteUri.Scheme -ne 'https' -or $websiteUri.AbsolutePath -ne '/' -or $websiteUri.Query -or $websiteUri.Fragment -or $websiteUri.UserInfo) {
        throw 'WebsiteOrigin must be an HTTPS origin with no path, credentials, query, or fragment.'
    }
    $WebsiteOrigin = $websiteUri.GetLeftPart([UriPartial]::Authority)
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

# A self-contained publish carries the .NET runtime. The tester installs no SDK, runtime, Node.js,
# database, or developer tooling.
dotnet publish $projectFile `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $publishRoot `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'The tester build failed.' }

# Client IDs identify this app but are not secrets. Tokens and passwords are never packaged.
$settingsPath = Join-Path $publishRoot 'appsettings.json'
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$settings.Release.Channel = 'Tester'
$settings.Release.Label = $TesterLabel.Trim()
$settings.Release.Version = $effectiveVersion
if ($DevelopmentPreview) {
    $settings.Release.Channel = 'Development'
    $settings.Release.Label = 'Website helper preview - not a release'
}
$settings.Integrations.Twitch.ClientId = $TwitchClientId.Trim()
$settings.Integrations.Spotify.ClientId = $SpotifyClientId.Trim()
$settings.Integrations.Spotify.EnabledByDefault = [bool]$EnableSpotify
$settings.Integrations.Tangia.EnabledByDefault = [bool]$EnableTangia
$settings.Integrations.StreamElements.EnabledByDefault = [bool]$EnableStreamElements
$settings.Integrations.TikFinity.EnabledByDefault = [bool]$EnableTikFinity
$settings.WebsiteBridge.StartOnSignIn = [bool]$WebsiteOrigin
$settings.WebsiteBridge.WebsiteOrigin = $WebsiteOrigin
$settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $settingsPath -Encoding UTF8

Copy-Item -LiteralPath (Join-Path $packageAssets "Open John's Twitch Handler.cmd") -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $packageAssets "Launch John's Twitch Handler.ps1") -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $packageAssets 'README FIRST.txt') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'README-FOR-TESTERS.md') -Destination $publishRoot
$testerReadmePath = Join-Path $publishRoot 'README-FOR-TESTERS.md'
$testerReadme = Get-Content -LiteralPath $testerReadmePath -Raw
Set-Content -LiteralPath $testerReadmePath -Value $testerReadme.Replace('(docs/tester-kit/README.md)', '(<Tester kit/START HERE.html>)') -Encoding UTF8
$plainReadme = $testerReadme.Replace('(docs/tester-kit/README.md)', '(Tester kit/START HERE.html)')
$plainReadme = $plainReadme -replace '(?m)^#{1,6} ', '' -replace '\*\*', '' -replace '(?m)^```(?:text)?\r?\n?', ''
$plainReadme = $plainReadme -replace '\[([^\]]+)\]\(([^)]+)\)', '$1: $2'
Set-Content -LiteralPath (Join-Path $publishRoot 'README-FOR-TESTERS.txt') -Value $plainReadme -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $packageAssets 'Start-WebsiteHelper.ps1') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $packageAssets 'Start Website Helper.cmd') -Destination $publishRoot
$packageReadme = Join-Path $publishRoot 'README FIRST.txt'
$readmeText = Get-Content -LiteralPath $packageReadme -Raw
$readmeText = $readmeText -replace '(?m)^Release:.*$', "Release: $($TesterLabel.Trim()) $effectiveVersion"
Set-Content -LiteralPath $packageReadme -Value $readmeText -Encoding UTF8

& (Join-Path $packageAssets 'copy-tester-kit.ps1') -DestinationDirectory (Join-Path $publishRoot 'Tester kit')

# The payload ZIP is an internal build detail embedded into Setup.exe. Testers receive one Windows
# application and never need to extract, move, or keep a folder of runtime files.
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $payloadZip -CompressionLevel Optimal
dotnet publish $installerProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $installerPublishRoot `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    "-p:PayloadZip=$payloadZip"
if ($LASTEXITCODE -ne 0) { throw 'The tester installer build failed.' }

Copy-Item -LiteralPath (Join-Path $installerPublishRoot 'JohnsTwitchHandlerSetup.exe') -Destination $setupPath -Force
# Cleanup is limited to the unique package directory created by this invocation.
$resolvedWorkingRoot = [IO.Path]::GetFullPath($workingRoot)
$expectedTempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\johns-handler-package-'
if (-not $resolvedWorkingRoot.StartsWith($expectedTempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to clean a package directory outside the generated temporary path.'
}
Remove-Item -LiteralPath $workingRoot -Recurse -Force
Write-Host "Tester installer created: $setupPath" -ForegroundColor Green
Write-Host 'It contains public application IDs only. No tokens, passwords, reports, or viewer data were included.'
