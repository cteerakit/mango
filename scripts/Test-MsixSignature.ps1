[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [string]$SignToolPath
)

$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackagePath).Path
Add-Type -AssemblyName System.IO.Compression.ZipFile
Add-Type -AssemblyName System.Security.Cryptography.Pkcs

$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $signature = $archive.GetEntry('AppxSignature.p7x')
    if ($null -eq $signature) { throw 'The MSIX is unsigned.' }
    $stream = $signature.Open()
    $buffer = [IO.MemoryStream]::new()
    try {
        $stream.CopyTo($buffer)
        $bytes = $buffer.ToArray()
    }
    finally {
        $stream.Dispose()
        $buffer.Dispose()
    }
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
    $cms.Decode([byte[]]$bytes[4..($bytes.Length - 1)])
    $cms.CheckSignature($true)
    $certificate = $cms.SignerInfos[0].Certificate
    if ($certificate.Subject -eq $certificate.Issuer -or
        $certificate.Thumbprint -eq 'EF17E38930F9225A7F14BF52F9B487381B74BCB7') {
        throw 'Public releases require a publicly trusted signing certificate. The self-signed Mango test certificate is not allowed.'
    }
}
finally { $archive.Dispose() }

if (-not $SignToolPath) {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { $SignToolPath = $command.Source }
    else {
        $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
        $sdk = Get-ChildItem -LiteralPath $sdkRoot -Directory |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'x64/signtool.exe') } |
            Select-Object -First 1
        if (-not $sdk) { throw 'Install the Windows SDK or pass -SignToolPath.' }
        $SignToolPath = Join-Path $sdk.FullName 'x64/signtool.exe'
    }
}

& $SignToolPath verify /pa /all /tw /v $package
if ($LASTEXITCODE -ne 0) {
    throw 'MSIX trust or timestamp verification failed. Do not publish this package.'
}
Write-Output "Verified trusted MSIX: $package"
