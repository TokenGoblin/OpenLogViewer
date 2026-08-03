<#
.SYNOPSIS
    Builds the OpenLogViewer installer.

.DESCRIPTION
    Publishes the application self-contained and wraps it in an MSI.

    Self-contained on purpose. The people this is for plug a laptop into a car,
    often in a garage with no internet, and "install a 60 MB runtime first" is
    the wrong thing to say at that moment. It costs about 130 MB over a
    framework-dependent build, which is unremarkable for a download and decisive
    for someone standing in a workshop.

    Single-file, which was checked rather than assumed: self-extraction can
    break WPF's resources and the WinRT Bluetooth interop, and neither of them
    breaks here.

    Trimming is not an option — WPF is not trim-safe — so the size is close to
    its floor.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Version 0.2.0
#>

[CmdletBinding()]
param(
    # Three parts, because Windows Installer ignores the fourth when it decides
    # whether one build supersedes another. A four-part version here would mean
    # releases that silently refuse to upgrade each other.
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$app = Join-Path $root 'src\OpenLogViewer.App\OpenLogViewer.App.csproj'

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $here 'out' }

# Taken from the application itself unless overridden, so the installer and the
# thing it installs can never disagree about what version they are.
if (-not $Version) {
    $csproj = [xml](Get-Content $app)
    $Version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) -as [string]

    if (-not $Version) { throw "No <Version> in $app, and none given." }
}

Write-Host "OpenLogViewer $Version ($Runtime)" -ForegroundColor Cyan

$staging = Join-Path $here 'obj'
$publish = Join-Path $staging 'publish'
$icon = Join-Path $staging 'icon'

foreach ($d in @($staging, $OutputDirectory)) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Path $d -Force | Out-Null
}

New-Item -ItemType Directory -Path $icon -Force | Out-Null
Copy-Item (Join-Path $root 'src\OpenLogViewer.App\AppIcon.ico') $icon

Write-Host "publishing…" -ForegroundColor DarkGray

& dotnet publish $app `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:Version=$Version `
    --output $publish `
    --nologo `
    --verbosity quiet

if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $publish 'OpenLogViewer.App.exe'
if (-not (Test-Path $exe)) { throw "published, but $exe is missing" }

# The .wxs names exactly one file, because a shortcut and a file association
# both need something with an identity to point at. That is only safe while the
# publish really is one file, so it is checked rather than assumed: turn off
# single-file, or add a dependency that will not embed, and this stops the
# release instead of shipping an installer missing half the application.
$published = Get-ChildItem $publish -Recurse -File
if ($published.Count -ne 1) {
    $names = ($published.Name | Sort-Object) -join ', '
    throw "expected a single published file, got $($published.Count): $names. " +
          "Add them to OpenLogViewer.wxs, or the installer will be incomplete."
}

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "   OpenLogViewer.App.exe  $size MB" -ForegroundColor DarkGray

# The licence, as the installer needs it. RTF because that is the only thing
# Windows Installer's licence pane will render.
$licenseRtf = Join-Path $staging 'License.rtf'
$licenseText = (Get-Content (Join-Path $root 'LICENSE') -Raw) -replace '\\', '\\\\' -replace '([{}])', '\$1'
$licenseText = ($licenseText -split "`r?`n") -join '\par' + '\par'

@"
{\rtf1\ansi\deff0{\fonttbl{\f0\fnil\fcharset0 Segoe UI;}}
\f0\fs18 $licenseText}
"@ | Set-Content $licenseRtf -Encoding ascii

Write-Host "building the msi…" -ForegroundColor DarkGray

$msi = Join-Path $OutputDirectory "OpenLogViewer-$Version-$Runtime.msi"

& wix build (Join-Path $here 'OpenLogViewer.wxs') `
    -define "Version=$Version" `
    -define "PublishDir=$publish" `
    -define "LicenseRtf=$licenseRtf" `
    -ext WixToolset.UI.wixext `
    -arch x64 `
    -out $msi

if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

$msiSize = [math]::Round((Get-Item $msi).Length / 1MB, 1)

Write-Host ""
Write-Host "$msi  ($msiSize MB)" -ForegroundColor Green
Write-Host ""
Write-Host "Not signed. Windows will show a SmartScreen warning until it is." -ForegroundColor Yellow
