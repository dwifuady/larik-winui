# Verifies that the MSIX actually carries the identity, publisher and version we intended.
# Run after packaging; the Store rejects a package whose identity does not match the reserved app.
#
#   pwsh ./scripts/verify-store-package.ps1 `
#       -Package Larik/AppPackages/.../Larik_1.0.0.0_x64_Store.msix `
#       -IdentityName '<Identity Name>' `
#       -Publisher 'CN=<Publisher>' `
#       -Version 1.0.0.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Package,
    [Parameter(Mandatory)][string] $IdentityName,
    [Parameter(Mandatory)][string] $Publisher,
    [Parameter(Mandatory)][string] $Version
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Package)) {
    throw "The package does not exist: $Package"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $Package).Path)
try {
    $entry = $archive.GetEntry('AppxManifest.xml')
    if ($null -eq $entry) {
        throw 'The package has no AppxManifest.xml.'
    }

    $reader = [System.IO.StreamReader]::new($entry.Open())
    try {
        [xml] $manifest = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}
finally {
    $archive.Dispose()
}

$namespaces = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
$namespaces.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')

$identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $namespaces)
if ($null -eq $identity) {
    throw 'The package manifest has no Identity element.'
}

if ($identity.Name -ne $IdentityName) {
    throw "Identity name mismatch. Expected '$IdentityName', package has '$($identity.Name)'."
}

if ($identity.Publisher -ne $Publisher) {
    throw "Publisher mismatch. Expected '$Publisher', package has '$($identity.Publisher)'."
}

if ($identity.Version -ne $Version) {
    throw "Version mismatch. Expected '$Version', package has '$($identity.Version)'."
}

Write-Host "Verified package: $IdentityName $Version"
Write-Host "  Publisher: $($identity.Publisher)"
Write-Host "  File:      $((Get-Item $Package).Length / 1MB | ForEach-Object { '{0:N1} MB' -f $_ })"
