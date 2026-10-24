## Changes in v1.0.5.0

- **Settings > App updates** can now install updates for you. Select **Install update and restart**; Mango downloads the installer, verifies its SHA-256 checksum, installs it silently, and reopens. **Download installer manually** remains as a fallback.
- If you are on v1.0.4.0 or earlier, install this version once by hand. After that, updates install from inside Mango.

## Install Mango

Download **MangoSetup-x64.exe**, open it, and follow the setup wizard. Mango installs for your Windows user and adds a Start menu shortcut. No administrator access, PowerShell, or certificate import is required. Runtimes are included.

This installer is unsigned. Windows may show an unknown-publisher or SmartScreen warning, and some device security policies block unsigned apps.

## Updates

Open **Settings > App updates > Check for updates**. When an update is available, select **Install update and restart**. Mango downloads and verifies the installer, installs it, and reopens. Your settings and player notes are kept.

## Existing MSIX users

Install the EXE version before removing the old Mango. The new app imports the old package's settings and notes if no unpackaged settings already exist. Confirm your data is present before uninstalling the old version. If needed, use **Settings > Notes** to export from the old app and import into the new app.

Windows x64 only.
