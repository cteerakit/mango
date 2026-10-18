# Publishing automatic updates

Mango uses Windows App Installer to download and install signed MSIX updates. Users install `Mango-x64.appinstaller`, `Mango-arm64.appinstaller`, or `Mango-x86.appinstaller` from a GitHub release once. Future releases use the same feed URL.

## Configure signing

The current release channel uses a self-signed test certificate. Its public certificate is committed at `distribution/Mango-Test.cer` and included in each release. Users must import it into `Cert:\LocalMachine\TrustedPeople` once from an Administrator PowerShell, then install the `.appinstaller`. See [the release installation instructions](../distribution/release-notes.md).

GitHub stores the signing PFX and password in the repository secrets below. The local backup is in `%LOCALAPPDATA%\MangoSigning`, restricted to the current Windows user and SYSTEM. Its password backup is encrypted for the current Windows user with DPAPI. Keep this private backup for future releases. Do not regenerate the certificate for each update.

1. Obtain a code-signing PFX with its private key. Its subject must match `Identity.Publisher` in `src/Mango/Package.appxmanifest`, currently `CN=AppPublisher`. For an existing installation, keep its package name and publisher. Changing either can create a separate app with separate data.
2. Add the base64-encoded PFX as the GitHub Actions repository secret `MSIX_CERTIFICATE_BASE64`.
3. Add its password as `MSIX_CERTIFICATE_PASSWORD`.

Use the same signing identity for each release. For a self-signed certificate, users must trust its public certificate before installing. Do not distribute the private key. For public distribution, use a certificate trusted by the users' Windows devices.

The workflow bundles .NET and the Windows App SDK, signs the packages, and requests a signing timestamp. It disables trimming because Mango uses reflection-based JSON serialization for settings and API responses. It removes the PFX from each runner after packaging. When switching to a publicly trusted certificate, update the distributed public certificate and release installation instructions along with the signing secrets.

## Publish a release

1. Commit and push the release changes.
2. Create and push a tag with a higher four-part MSIX version. Each component must be between 0 and 65535. For example:

   ```powershell
   git tag v1.0.1.0
   git push origin v1.0.1.0
   ```

3. Wait for the **Release Mango** workflow to pass its tests and build all three architectures. The workflow publishes one GitHub release with all MSIX packages and App Installer feeds.
4. Direct new users to the `.appinstaller` asset for their architecture. Existing users enrolled in the feed receive the new version automatically.

Every `v*` tag triggers a stable release. Do not use this workflow for prereleases or older versions: the workflow makes the release the latest one. Release publication fails if that release already exists. Resolve the publication failure before retrying; do not overwrite an MSIX that users have already installed.

The feed has a stable URL such as `https://github.com/cteerakit/mango/releases/latest/download/Mango-x64.appinstaller`. Its package URL points to a specific release, such as `https://github.com/cteerakit/mango/releases/download/v1.0.1.0/Mango-x64.msix`. Keep old release assets available. The repository and assets must be publicly readable for users to update without authentication.

## Verify an update before distribution

1. Run `dotnet test tests/Mango.Tests/Mango.Tests.csproj -c Release` and `./scripts/Test-AppInstaller.ps1`.
2. On a test Windows device with App Installer installed and the signing certificate trusted, install an earlier release through its `.appinstaller`. Save a player note and a setting.
3. Publish a higher version to the test feed. In Mango, open **Settings > App updates** and select **Check for updates**. Confirm that Mango reports an available update and continues running.
4. Close Mango and allow Windows to install the update. Reopen Mango and confirm that the version changed and the saved note and setting remain.
5. Disconnect the network and check again. Confirm that Mango shows a retry message without crashing. Reconnect and retry.

The generator tests validate the feed contents and reject insecure URLs and packages without signatures. They do not replace the two-version installation test. Windows verifies package signatures and publisher identity during installation.

## Generate a feed for another host

Build and sign the MSIX first, then run:

```powershell
./scripts/New-AppInstaller.ps1 `
    -PackagePath ./Mango-x64.msix `
    -PackageUri https://updates.example.com/1.0.1.0/Mango-x64.msix `
    -AppInstallerUri https://updates.example.com/Mango-x64.appinstaller `
    -OutputPath ./Mango-x64.appinstaller
```

Upload the MSIX before replacing the feed at its stable HTTPS URL. Serve `.appinstaller` as `application/appinstaller` and `.msix` as `application/msix`. Do not cache the stable feed indefinitely. The generator reads the version, identity, publisher, and architecture from the package itself.

## Update behavior

The feed enables checks on every launch and background checks every eight hours. Windows handles downloads and applies updates when the app is not in use. Mango does not force the app to close. The Settings button checks availability; it does not force installation or promise that an update is already downloaded. Offline checks can fail and be retried later.

Direct `.msix` installations and development builds do not have an App Installer association. Settings reports that automatic updates are unavailable. Existing standalone installations need one installation through the `.appinstaller` to enroll. Do not uninstall Mango first, as uninstalling can remove local app data.

Reference: [Microsoft App Installer update settings](https://learn.microsoft.com/en-us/windows/msix/app-installer/update-settings) and [App Installer APIs](https://learn.microsoft.com/en-us/windows/msix/app-installer/app-installer-documentation).
