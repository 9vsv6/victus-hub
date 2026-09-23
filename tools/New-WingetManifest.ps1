<#
.SYNOPSIS
    Writes the three winget manifest files for one release of Victus Hub.

.DESCRIPTION
    The release exe is a single self-contained file, so winget installs it as a "portable" package.
    The output folder follows winget-pkgs' layout (manifests/9/9vsv6/VictusHub/<version>), ready to be
    copied into a fork of microsoft/winget-pkgs and submitted as a pull request.

.EXAMPLE
    ./tools/New-WingetManifest.ps1 -Version 1.5.0 -ExePath publish/HpVictusControl.exe -OutDir publish/winget
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $ExePath,
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $Repository = '9vsv6/victus-hub'
)

$ErrorActionPreference = 'Stop'

$id = '9vsv6.VictusHub'
$url = "https://github.com/$Repository/releases/download/v$Version/HpVictusControl.exe"
$sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $ExePath).Hash
$folder = Join-Path $OutDir "manifests/9/9vsv6/VictusHub/$Version"
New-Item -ItemType Directory -Force -Path $folder | Out-Null

$versionYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
"@

$installerYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: portable
Commands:
- victus-hub
MinimumOSVersion: 10.0.17763.0
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $sha256
ManifestType: installer
ManifestVersion: 1.6.0
"@

$localeYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: 9vsv6
PublisherUrl: https://github.com/9vsv6
PackageName: Victus Hub
PackageUrl: https://github.com/$Repository
License: Proprietary
ShortDescription: Fan, performance, GPU power and driver control for HP Victus and Omen laptops.
Description: |-
  A small replacement for the fan and performance parts of Omen Gaming Hub. It talks to the HP BIOS
  WMI interface directly: performance modes, fan control, GPU power, keyboard backlight, per-game
  profiles, and driver and BIOS updates from HP, NVIDIA and Intel.
Tags:
- hp
- victus
- omen
- fan-control
- laptop
ReleaseNotesUrl: https://github.com/$Repository/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@

# winget-pkgs wants UTF-8 without a byte order mark.
$utf8 = New-Object System.Text.UTF8Encoding $false
[IO.File]::WriteAllText((Join-Path $folder "$id.yaml"), $versionYaml, $utf8)
[IO.File]::WriteAllText((Join-Path $folder "$id.installer.yaml"), $installerYaml, $utf8)
[IO.File]::WriteAllText((Join-Path $folder "$id.locale.en-US.yaml"), $localeYaml, $utf8)

Write-Host "winget manifests for $id $Version -> $folder (SHA256 $sha256)"
