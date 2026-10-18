[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][uri]$PackageUri,
    [Parameter(Mandatory)][uri]$AppInstallerUri,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
foreach ($uri in @($PackageUri, $AppInstallerUri)) {
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or $uri.UserInfo) {
        throw 'Release URLs must be absolute HTTPS URLs without credentials.'
    }
}

Add-Type -AssemblyName System.IO.Compression.ZipFile
$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath).Path)
try {
    if ($null -eq $archive.GetEntry('AppxSignature.p7x')) {
        throw 'The MSIX must be signed before generating its update feed.'
    }
    $manifestEntry = $archive.GetEntry('AppxManifest.xml')
    if ($null -eq $manifestEntry) {
        throw 'Expected a single MSIX package containing AppxManifest.xml.'
    }
    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}
finally { $archive.Dispose() }

$identity = $manifest.Package.Identity
if (-not $identity.Name -or -not $identity.Publisher -or
    $identity.Version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
    $identity.ProcessorArchitecture -notin @('x86', 'x64', 'arm64', 'neutral')) {
    throw 'The MSIX package identity is incomplete or invalid.'
}

$document = [System.Xml.XmlDocument]::new()
$namespace = 'http://schemas.microsoft.com/appx/appinstaller/2017/2'
$root = $document.CreateElement('AppInstaller', $namespace)
$root.SetAttribute('Uri', $AppInstallerUri.AbsoluteUri)
$root.SetAttribute('Version', $identity.Version)
[void]$document.AppendChild($root)
$package = $document.CreateElement('MainPackage', $namespace)
foreach ($attribute in @('Name', 'Publisher', 'Version', 'ProcessorArchitecture')) {
    $package.SetAttribute($attribute, $identity.GetAttribute($attribute))
}
$package.SetAttribute('Uri', $PackageUri.AbsoluteUri)
[void]$root.AppendChild($package)
$updates = $document.CreateElement('UpdateSettings', $namespace)
$onLaunch = $document.CreateElement('OnLaunch', $namespace)
$onLaunch.SetAttribute('HoursBetweenUpdateChecks', '0')
[void]$updates.AppendChild($onLaunch)
[void]$updates.AppendChild($document.CreateElement('AutomaticBackgroundTask', $namespace))
[void]$root.AppendChild($updates)

$output = [System.IO.Path]::GetFullPath($OutputPath)
[void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($output))
$document.Save($output)
Write-Output $output
