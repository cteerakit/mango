## Install Mango with automatic updates

This is a self-signed test release. Trust the public test certificate once on each Windows PC, then install through App Installer. Future releases use the same certificate and update automatically.

1. Download [Mango-Test.cer](https://github.com/cteerakit/mango/releases/latest/download/Mango-Test.cer).
2. Open PowerShell **as Administrator** in the download folder and run:

   ```powershell
   Import-Certificate -FilePath ./Mango-Test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
   ```

   The certificate subject is `CN=AppPublisher`. Its thumbprint is `EF17E38930F9225A7F14BF52F9B487381B74BCB7`.

3. Download and open [Mango for Windows x64](https://github.com/cteerakit/mango/releases/latest/download/Mango-x64.appinstaller). New releases support Windows x64 only.

If Windows does not recognize `.appinstaller` files, install Microsoft's App Installer from the Microsoft Store first.

Existing users should install through the `.appinstaller` once to enroll in updates. Keep Mango installed to preserve settings and notes. Windows then checks for updates on launch and in the background and installs them when Mango is not in use. You can check availability in **Settings > App updates**.
