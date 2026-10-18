[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$OutputDirectory,
    [string]$InnoSetupCompiler
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
    @($Version.Split('.') | Where-Object { [long]$_ -gt 65535 }).Count -gt 0) {
    throw 'Version must have four numeric parts, each between 0 and 65535.'
}
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root "AppPackages/$Version-x64-setup" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$publish = Join-Path $output "publish-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (-not $InnoSetupCompiler) { $InnoSetupCompiler = & "$PSScriptRoot/Get-InnoSetup.ps1" }

dotnet publish (Join-Path $root 'src/Mango/Mango.csproj') -c Release -p:Platform=x64 -r win-x64 `
    -p:WindowsPackageType=None -p:EnableMsixTooling=true -p:EnableWinAppRunSupport=false `
    -p:GenerateAppxPackageOnBuild=false -p:SelfContained=true -p:WindowsAppSDKSelfContained=true `
    -p:PublishTrimmed=false "-p:Version=$Version" "-p:AssemblyVersion=$Version" "-p:FileVersion=$Version" `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Mango publish failed.' }

foreach ($file in @('Mango.exe', 'coreclr.dll', 'Microsoft.UI.Xaml.dll', 'Read-SteamRoster.ps1', 'Mango.pri', 'App.xbf', 'MainWindow.xbf', 'Assets/Hud/bounty-rune.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $file))) { throw "Missing published file: $file" }
}
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $publish
& $InnoSetupCompiler "/DAppVersion=$Version" "/DPublishDir=$publish" "/DOutputDir=$output" (Join-Path $root 'distribution/Mango.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $output 'MangoSetup-x64.exe'
Get-FileHash -LiteralPath $installer -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash.ToLowerInvariant())  MangoSetup-x64.exe" } |
    Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8
Write-Output "Installer ready: $installer"
