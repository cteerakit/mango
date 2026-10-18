[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = 'Stop'
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$registration = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/{A54A8757-79EE-44B6-8943-43CF5C5B5FD2}_is1'
if (Test-Path $registration) { throw 'An EXE installation already exists. Run this smoke test on a separate test Windows account.' }
if (Get-Process Mango -ErrorAction SilentlyContinue) { throw 'Close Mango before running the installer smoke test.' }
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "mango-installer-test-$([guid]::NewGuid())"
$settingsPath = Join-Path $env:LOCALAPPDATA 'Mango/settings.json'
$settingsBackup = if (Test-Path -LiteralPath $settingsPath) { [IO.File]::ReadAllBytes($settingsPath) } else { $null }
$appProcess = $null

function Invoke-Setup {
    $setup = Start-Process -FilePath $installer -WindowStyle Hidden -Wait -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$testRoot`"", "/LOG=`"$testRoot-install.log`"")
    if ($setup.ExitCode -ne 0) { throw "Installer failed: $($setup.ExitCode). See $testRoot-install.log" }
}

try {
    Invoke-Setup
    $exe = Join-Path $testRoot 'Mango.exe'
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -ne $Version) { throw 'Installed version differs from the release version.' }
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Mango.lnk'
    if (-not (Test-Path -LiteralPath $shortcut)) { throw 'Start menu shortcut is missing.' }
    $shell = New-Object -ComObject WScript.Shell
    if ($shell.CreateShortcut($shortcut).TargetPath -ne $exe) { throw 'Start menu shortcut points to the wrong executable.' }
    if ((Get-ItemProperty $registration).DisplayVersion -ne $Version) { throw 'Windows uninstall registration has the wrong version.' }

    $appProcess = Start-Process -FilePath $exe -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $appProcess.Refresh()
        if ($appProcess.HasExited) { throw "Installed Mango exited during startup: $($appProcess.ExitCode)." }
    } until ($appProcess.MainWindowHandle -ne 0 -or [DateTime]::UtcNow -gt $deadline)
    if ($appProcess.MainWindowHandle -eq 0) { throw 'Installed Mango did not create a window.' }
    if (-not ('MangoInstallerVerification' -as [type])) {
        Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MangoInstallerVerification
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetPackageFullName(IntPtr process, ref uint length, IntPtr name);
}
'@
    }
    [uint32]$packageNameLength = 0
    if ([MangoInstallerVerification]::GetPackageFullName($appProcess.Handle, [ref]$packageNameLength, [IntPtr]::Zero) -ne 15700) {
        throw 'Mango unexpectedly requires package identity.'
    }
    Write-Output "Installed Mango $Version launched without MSIX registration."
    [void]$appProcess.CloseMainWindow()
    if (-not $appProcess.WaitForExit(10000)) { throw 'Mango did not close normally.' }

    $settingsHash = (Get-FileHash -LiteralPath $settingsPath).Hash
    Invoke-Setup
    if ((Get-FileHash -LiteralPath $settingsPath).Hash -ne $settingsHash) { throw 'Reinstall changed settings or notes.' }
    $uninstaller = Join-Path $testRoot 'unins000.exe'
    $uninstall = Start-Process -FilePath $uninstaller -WindowStyle Hidden -Wait -PassThru -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    if ($uninstall.ExitCode -ne 0) { throw "Uninstall failed: $($uninstall.ExitCode)." }
    if ((Get-FileHash -LiteralPath $settingsPath).Hash -ne $settingsHash) { throw 'Uninstall removed or changed settings and notes.' }
    if ((Test-Path $registration) -or (Test-Path -LiteralPath $exe) -or (Test-Path -LiteralPath $shortcut)) {
        throw 'Uninstall left the app, shortcut, or uninstall registration behind.'
    }
    Write-Output 'Install, version, shortcut, startup, reinstall, uninstall, and settings preservation checks passed.'
}
finally {
    if ($appProcess -and -not $appProcess.HasExited) { Stop-Process -Id $appProcess.Id }
    $testUninstaller = Join-Path $testRoot 'unins000.exe'
    if (Test-Path -LiteralPath (Join-Path $testRoot 'Mango.exe')) {
        if (Test-Path -LiteralPath $testUninstaller) {
            Start-Process -FilePath $testUninstaller -WindowStyle Hidden -Wait -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') | Out-Null
        }
    }
    if ($null -ne $settingsBackup) { [IO.File]::WriteAllBytes($settingsPath, $settingsBackup) }
}
