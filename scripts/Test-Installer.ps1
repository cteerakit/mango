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
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    $fileVersion = "$($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart).$($info.FilePrivatePart)"
    if ($fileVersion -ne $Version) { throw "Installed version $fileVersion differs from the release version $Version." }
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Mango.lnk'
    if (-not (Test-Path -LiteralPath $shortcut)) { throw 'Start menu shortcut is missing.' }
    $shell = New-Object -ComObject WScript.Shell
    if ($shell.CreateShortcut($shortcut).TargetPath -ne $exe) { throw 'Start menu shortcut points to the wrong executable.' }
    if ((Get-ItemProperty $registration).DisplayVersion -ne $Version) { throw 'Windows uninstall registration has the wrong version.' }

    if (-not ('MangoInstallerVerification' -as [type])) {
        Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class MangoInstallerVerification
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetPackageFullName(IntPtr process, ref uint length, IntPtr name);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int max);

    // Process.MainWindowHandle guesses among Tauri's several top-level windows, so list them directly.
    public static List<KeyValuePair<IntPtr, string>> VisibleWindows(int processId)
    {
        var windows = new List<KeyValuePair<IntPtr, string>>();
        EnumWindows((window, _) => {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner == processId && IsWindowVisible(window)) {
                var title = new StringBuilder(256);
                GetWindowText(window, title, title.Capacity);
                windows.Add(new KeyValuePair<IntPtr, string>(window, title.ToString()));
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }
}
'@
    }

    $appProcess = Start-Process -FilePath $exe -WorkingDirectory $testRoot -PassThru
    # WebView2 can start slowly on a fresh CI runner.
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $companion = $null
    do {
        Start-Sleep -Milliseconds 250
        if ($appProcess.HasExited) { throw "Installed Mango exited during startup: $($appProcess.ExitCode)." }
        $windows = [MangoInstallerVerification]::VisibleWindows($appProcess.Id)
        $companion = $windows | Where-Object Value -eq 'Mango' | Select-Object -First 1
    } until ($companion -or [DateTime]::UtcNow -gt $deadline)
    if (-not $companion) {
        $seen = ($windows | ForEach-Object { "'$($_.Value)'" }) -join ', '
        throw "Installed Mango did not show the companion window. Visible windows: $(if ($seen) { $seen } else { 'none' })."
    }
    [uint32]$packageNameLength = 0
    if ([MangoInstallerVerification]::GetPackageFullName($appProcess.Handle, [ref]$packageNameLength, [IntPtr]::Zero) -ne 15700) {
        throw 'Mango unexpectedly requires package identity.'
    }
    Write-Output "Installed Mango $Version launched without MSIX registration."
    # Closing the companion window quits Mango (the tray icon goes with it).
    if (-not [MangoInstallerVerification]::PostMessage($companion.Key, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)) {
        throw 'Could not request closure of the Mango companion window.'
    }
    if (-not $appProcess.WaitForExit(15000)) { throw 'Mango did not close normally.' }

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
