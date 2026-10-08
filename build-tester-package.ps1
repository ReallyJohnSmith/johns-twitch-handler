[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TwitchClientId,
    [string]$SpotifyClientId = '',
    [string]$TesterLabel = 'Tester',
    [string]$ReleaseVersion = '',
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
$artifactRoot = Join-Path $projectRoot 'artifacts\tester-packages'
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

function Test-PublicClientId([string]$value) {
    return $value -match '^[A-Za-z0-9]{16,128}$'
}

if (-not (Test-PublicClientId $TwitchClientId)) {
    throw 'TwitchClientId must be the public Client ID from the John''s Twitch Handler Twitch developer application.'
}
if ($SpotifyClientId -and -not (Test-PublicClientId $SpotifyClientId)) {
    throw 'SpotifyClientId is not a valid public Spotify application ID.'
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
$settings.Integrations.Twitch.ClientId = $TwitchClientId.Trim()
$settings.Integrations.Spotify.ClientId = $SpotifyClientId.Trim()
$settings.Integrations.Spotify.EnabledByDefault = [bool]$EnableSpotify
$settings.Integrations.Tangia.EnabledByDefault = [bool]$EnableTangia
$settings.Integrations.StreamElements.EnabledByDefault = [bool]$EnableStreamElements
$settings.Integrations.TikFinity.EnabledByDefault = [bool]$EnableTikFinity
$settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $settingsPath -Encoding UTF8

Copy-Item -LiteralPath (Join-Path $packageAssets "Open John's Twitch Handler.cmd") -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $packageAssets "Launch John's Twitch Handler.ps1") -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $packageAssets 'README FIRST.txt') -Destination $publishRoot
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
Remove-Item -LiteralPath $workingRoot -Recurse -Force
Write-Host "Tester installer created: $setupPath" -ForegroundColor Green
Write-Host 'It contains public application IDs only. No tokens, passwords, reports, or viewer data were included.'
