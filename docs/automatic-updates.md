# Publishing Mango installers

Mango ships as `MangoSetup-x64.exe` on GitHub Releases. It installs for the current Windows user, adds a Start menu shortcut, and includes .NET and the Windows App SDK. Users do not need PowerShell, administrator access, or a certificate import.

## Build locally

Run from the repository root with the .NET 10 SDK installed:

```powershell
./scripts/Build-Installer.ps1 -Version 1.0.3.0
```

The script downloads the pinned Inno Setup 6.7.3 compiler, verifies its SHA-256 checksum, and installs the compiler for the current user. To use an existing compiler, pass `-InnoSetupCompiler C:/path/to/ISCC.exe`.

The output folder is `AppPackages/1.0.3.0-x64-setup`. Distribute `MangoSetup-x64.exe` and `SHA256SUMS.txt`. The `publish-*` subfolder is build staging and is not a separate download.

## Publish a release

1. Run `dotnet test tests/Mango.Tests/Mango.Tests.csproj -c Release`.
2. Build the installer and run `./scripts/Test-Installer.ps1 -InstallerPath ./AppPackages/1.0.3.0-x64-setup/MangoSetup-x64.exe -Version 1.0.3.0` on a test Windows account. This installs, launches, reinstalls, and uninstalls Mango in a temporary folder.
3. Commit and push the release changes.
4. Create and push a new four-part version tag, for example `git tag v1.0.3.0` followed by `git push origin v1.0.3.0`. Each version part must be between 0 and 65535.
5. Wait for **Release Mango** to pass tests, build and smoke-test the x64 installer, and publish the GitHub release.

Every `v*` tag publishes a stable release as latest. Do not use this workflow for prereleases or older versions. Do not replace published installers. Fix release problems with a higher version.

## Update behavior

In **Settings > App updates**, select **Check for updates**. Mango compares its installed version with the latest stable GitHub release. When a newer x64 installer is available, select **Install update and restart**. Mango then:

1. Downloads `MangoSetup-x64.exe` to a new temporary folder.
2. Verifies its SHA-256 hash against the release's `SHA256SUMS.txt`. A mismatch deletes the file and nothing runs.
3. Starts the installer with `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1` and exits. The installer replaces the files and starts Mango again. `/RELAUNCH=1` is what triggers the relaunch; a plain silent install, such as the one in `Test-Installer.ps1`, does not start Mango.

**Download installer manually** still opens the GitHub download in the default browser as a fallback. The installer log is `install.log` in the temporary folder.

Updates start only when the user selects **Install update and restart**. Mango never checks for or installs updates in the background. A release must include both `MangoSetup-x64.exe` and `SHA256SUMS.txt`, or Mango reports that the installer is not available yet. Offline checks and GitHub rate limits show a retry message.

The checksum comes from the same release as the installer, so it detects corruption but not a compromised release. Code signing would be needed for that.

## Move from the old MSIX release

The EXE installer is a separate installation from MSIX. On first launch, Mango copies settings and player notes from the old package's `LocalCache/Local/Mango/settings.json` if `%LOCALAPPDATA%/Mango/settings.json` does not already exist. It preserves the original file and any existing unpackaged settings. API caches are rebuilt as needed.

Keep the old Mango installed until you confirm that your settings and notes are present in the new app. If you already have unpackaged settings, export notes from the old app and import them into the new app under **Settings > Notes**. You can then remove the old MSIX installation through Windows Settings. Start the new Mango using its Start menu shortcut.

Old `.appinstaller` feeds cannot move users to an EXE installer automatically. Keep old release assets available, and direct those users to `MangoSetup-x64.exe` once.

## Signing

The conventional EXE installer does not require an MSIX signing certificate. Current releases are unsigned, so Windows may show an unknown-publisher or SmartScreen warning. Smart App Control or organizational policies may block unsigned programs. Do not promise that an unsigned installer will run on every device.

Publicly trusted signing remains an optional future improvement. It requires verified publisher credentials. MSIX test certificates and the old GitHub PFX secrets are not used by this workflow.

References: [Unpackaged WinUI deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app) and [Windows app reputation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).
