[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DestinationDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$guideSource = Join-Path $repositoryRoot 'src\StreamSidekick\wwwroot\tester-guide.html'
$docsSource = Join-Path $repositoryRoot 'docs\tester-kit'
$kitFiles = @('README.md', 'CHECKLIST.md', 'FEEDBACK.md', 'KNOWN-LIMITATIONS.md')

# Copy only the reviewed guide files, never runtime data or the tester invitation draft.
foreach ($name in $kitFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $docsSource $name) -PathType Leaf)) {
        throw "Tester guide file is missing: $name"
    }
}
if (-not (Test-Path -LiteralPath $guideSource -PathType Leaf)) { throw 'The offline tester guide is missing.' }

New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
Copy-Item -LiteralPath $guideSource -Destination (Join-Path $DestinationDirectory 'START HERE.html') -Force
foreach ($name in $kitFiles) {
    Copy-Item -LiteralPath (Join-Path $docsSource $name) -Destination $DestinationDirectory -Force
}
