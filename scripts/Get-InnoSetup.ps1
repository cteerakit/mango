[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$toolRoot = Join-Path $env:LOCALAPPDATA 'MangoBuildTools/InnoSetup-6.7.3'
$compiler = Join-Path $toolRoot 'ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $installer = Join-Path ([IO.Path]::GetTempPath()) "mango-inno-$([guid]::NewGuid()).exe"
    try {
        Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $installer
        if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne
            '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732') {
            throw 'Inno Setup download checksum did not match.'
        }
        $process = Start-Process -FilePath $installer -WindowStyle Hidden -Wait -PassThru -ArgumentList @(
            '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=`"$toolRoot`"")
        if ($process.ExitCode -ne 0) { throw "Inno Setup installation failed: $($process.ExitCode)" }
    }
    finally { Remove-Item -LiteralPath $installer -ErrorAction SilentlyContinue }
}
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Inno Setup compiler was not installed.' }
Write-Output $compiler
