$ErrorActionPreference = 'Stop'
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) "mango-update-test-$([guid]::NewGuid())"
[void][IO.Directory]::CreateDirectory($temporaryDirectory)

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

try {
    Add-Type -AssemblyName System.IO.Compression.ZipFile
    $packagePath = Join-Path $temporaryDirectory 'Mango.msix'
    $archive = [IO.Compression.ZipFile]::Open($packagePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = [IO.StreamWriter]::new($archive.CreateEntry('AppxManifest.xml').Open())
        try {
            $writer.Write('<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="Mango.Test" Publisher="CN=Mango &amp; Test" Version="2.10.3.0" ProcessorArchitecture="arm64" /></Package>')
        }
        finally { $writer.Dispose() }
        # Only the generator is under test; Windows verifies real release signatures at installation.
        [void]$archive.CreateEntry('AppxSignature.p7x')
    }
    finally { $archive.Dispose() }

    $arguments = @{
        PackagePath = $packagePath
        PackageUri = 'https://example.com/releases/v2.10.3.0/Mango.msix?one=1&two=2'
        AppInstallerUri = 'https://example.com/latest/Mango-arm64.appinstaller'
        OutputPath = Join-Path $temporaryDirectory 'feed/Mango.appinstaller'
    }
    & "$PSScriptRoot/New-AppInstaller.ps1" @arguments | Out-Null
    [xml]$feed = Get-Content -LiteralPath $arguments.OutputPath -Raw
    Assert-True ($feed.AppInstaller.NamespaceURI -eq 'http://schemas.microsoft.com/appx/appinstaller/2017/2') 'Wrong schema.'
    Assert-True ($feed.AppInstaller.Uri -eq $arguments.AppInstallerUri) 'The update channel must use the stable URL.'
    Assert-True ($feed.AppInstaller.Version -eq '2.10.3.0') 'Feed version must match the package.'
    Assert-True ($feed.AppInstaller.MainPackage.Name -eq 'Mango.Test') 'Package identity was not preserved.'
    Assert-True ($feed.AppInstaller.MainPackage.Publisher -eq 'CN=Mango & Test') 'Publisher was not XML escaped correctly.'
    Assert-True ($feed.AppInstaller.MainPackage.Version -eq '2.10.3.0') 'Package version was not preserved.'
    Assert-True ($feed.AppInstaller.MainPackage.ProcessorArchitecture -eq 'arm64') 'Architecture was not preserved.'
    Assert-True ($feed.AppInstaller.MainPackage.Uri -eq $arguments.PackageUri) 'Package URL was not preserved.'
    Assert-True ($feed.AppInstaller.UpdateSettings.OnLaunch.HoursBetweenUpdateChecks -eq '0') 'Every launch should check for updates.'
    Assert-True ($null -ne $feed.AppInstaller.UpdateSettings.AutomaticBackgroundTask) 'Background updates must be enabled.'
    Assert-True ($null -eq $feed.AppInstaller.UpdateSettings.ForceUpdateFromAnyVersion) 'Downgrades must not be enabled.'

    $arguments.PackageUri = 'http://example.com/Mango.msix'
    $rejected = $false
    try { & "$PSScriptRoot/New-AppInstaller.ps1" @arguments | Out-Null }
    catch { $rejected = $_.Exception.Message -like '*HTTPS*' }
    Assert-True $rejected 'Insecure update URLs must be rejected.'

    $arguments.PackageUri = 'https://example.com/Mango.msix'
    $archive = [IO.Compression.ZipFile]::Open($packagePath, [IO.Compression.ZipArchiveMode]::Update)
    try { $archive.GetEntry('AppxSignature.p7x').Delete() }
    finally { $archive.Dispose() }
    $rejected = $false
    try { & "$PSScriptRoot/New-AppInstaller.ps1" @arguments | Out-Null }
    catch { $rejected = $_.Exception.Message -like '*signed*' }
    Assert-True $rejected 'Unsigned packages must be rejected.'
    Write-Output 'App Installer tests passed.'
}
finally {
    $resolvedTemporaryDirectory = [IO.Path]::GetFullPath($temporaryDirectory)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($resolvedTemporaryDirectory) -ne $temporaryRoot -or
        [IO.Path]::GetFileName($resolvedTemporaryDirectory) -notlike 'mango-update-test-*') {
        throw 'Refusing to remove a test directory outside the temporary root.'
    }
    Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
}
