[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$OutputDirectory,
    [string]$InnoSetupCompiler
)

# Builds the Tauri app and packages it with the same Inno Setup installer (and AppId) as the
# WinUI releases, so in-app updates move existing installs over in place.

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+\.0$' -or
    @($Version.Split('.') | Where-Object { [long]$_ -gt 65535 }).Count -gt 0) {
    throw 'Version must be four numbers ending in .0 (e.g. 1.1.0.0), each between 0 and 65535.'
}
$root = Split-Path $PSScriptRoot -Parent
$app = Join-Path $root 'app'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root "AppPackages/$Version-x64-setup" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$publish = Join-Path $output "publish-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $publish -Force | Out-Null
if (-not $InnoSetupCompiler) { $InnoSetupCompiler = & "$PSScriptRoot/Get-InnoSetup.ps1" }

$semver = ($Version.Split('.')[0..2]) -join '.'
$env:MANGO_VERSION = $Version
Push-Location $app
try {
    npm ci
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
    npx tauri build --no-bundle --config "{`"version`":`"$semver`"}"
    if ($LASTEXITCODE -ne 0) { throw 'Mango build failed.' }
}
finally {
    Pop-Location
    Remove-Item Env:MANGO_VERSION -ErrorAction SilentlyContinue
}

Copy-Item -LiteralPath (Join-Path $app 'target/release/Mango.exe') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $publish

# Windows 11 ships the WebView2 runtime; the bootstrapper installs it on older Windows 10.
$bootstrapper = Join-Path $output 'MicrosoftEdgeWebview2Setup.exe'
if (-not (Test-Path -LiteralPath $bootstrapper)) {
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapper
}
$signature = Get-AuthenticodeSignature -LiteralPath $bootstrapper
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    throw 'The WebView2 bootstrapper is not signed by Microsoft.'
}

& $InnoSetupCompiler "/DAppVersion=$Version" "/DPublishDir=$publish" "/DOutputDir=$output" "/DWebView2Bootstrapper=$bootstrapper" (Join-Path $root 'distribution/Mango.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $output 'MangoSetup-x64.exe'
Get-FileHash -LiteralPath $installer -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash.ToLowerInvariant())  MangoSetup-x64.exe" } |
    Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8
Write-Output "Installer ready: $installer"
