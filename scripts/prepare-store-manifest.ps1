# Builds the Store copy of the package manifest from the dev manifest.
#
# Partner Center assigns the identity name, publisher and display name. Set those as repository
# variables (STORE_IDENTITY_NAME, STORE_PUBLISHER, STORE_PUBLISHER_DISPLAY_NAME) or pass them here.
#
#   pwsh ./scripts/prepare-store-manifest.ps1 `
#       -IdentityName "<from Partner Center>" `
#       -Publisher "CN=<from Partner Center>" `
#       -PublisherDisplayName "Larik" `
#       -Version 1.0.0.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $IdentityName,
    [Parameter(Mandatory)][string] $Publisher,
    [string] $PublisherDisplayName = 'Larik',
    [string] $Version = '1.0.0.0',
    [string] $Source = 'Larik/Package.appxmanifest',
    [string] $Output = 'Larik/obj/store/Package.appxmanifest'
)

$ErrorActionPreference = 'Stop'

if ($Version -match '^\d+\.\d+\.\d+$') {
    # The package identity needs four parts; the app's informational version stays three.
    $Version = "$Version.0"
}

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "The Store package version must be three or four numeric parts (for example 1.0.0 or 1.0.0.0), not '$Version'."
}

if ([string]::IsNullOrWhiteSpace($IdentityName)) {
    throw 'The Store identity name is missing. Copy it from Partner Center.'
}

if ($IdentityName -eq '5F1C2F1B-E63A-4681-A08C-189E9C4C2781') {
    throw 'That is the development package identity. Copy the Identity Name from Partner Center.'
}

if ([string]::IsNullOrWhiteSpace($Publisher)) {
    throw 'The Store publisher is missing. Copy the Publisher DN from Partner Center.'
}

if ([string]::IsNullOrWhiteSpace($PublisherDisplayName)) {
    throw 'The Store publisher display name is missing.'
}

if ($Publisher -notmatch '=') {
    $Publisher = "CN=$Publisher"
}

if ($Publisher -match 'AppPublisher' -or $PublisherDisplayName -match '^AppPublisher$') {
    throw 'That is the development publisher. Copy the Publisher and display name from Partner Center.'
}

$root = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $root $Source
$outputPath = Join-Path $root $Output

[xml] $manifest = Get-Content -Raw $sourcePath
$namespaces = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
$namespaces.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')

$identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $namespaces)
if ($null -eq $identity) {
    throw "The manifest has no Package/Identity element: $sourcePath"
}

$identity.Name = $IdentityName
$identity.Publisher = $Publisher
$identity.Version = $Version

$properties = $manifest.SelectSingleNode('/f:Package/f:Properties', $namespaces)
if ($null -ne $properties) {
    $properties.PublisherDisplayName = $PublisherDisplayName
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outputPath) | Out-Null
$manifest.Save($outputPath)

Write-Host "Store manifest: $outputPath"
Write-Host "  Name:      $IdentityName"
Write-Host "  Publisher: $Publisher"
Write-Host "  Version:   $Version"
