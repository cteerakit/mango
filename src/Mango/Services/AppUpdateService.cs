using Windows.ApplicationModel;

namespace Mango.Services;

public sealed class AppUpdateService
{
    public string Version { get; }
    public bool CanCheckForUpdates { get; }
    public string Status { get; }

    public AppUpdateService()
    {
        try
        {
            var package = Package.Current;
            var version = package.Id.Version;
            Version = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            CanCheckForUpdates = !package.IsDevelopmentMode && package.GetAppInstallerInfo() is not null;
            Status = package.IsDevelopmentMode
                ? "Automatic updates are unavailable in this development build."
                : CanCheckForUpdates
                ? "Windows checks for updates on launch and in the background. Updates are installed when Mango is not in use."
                : "Automatic updates require the Mango App Installer download. Install it once to enable future automatic updates. Your settings and notes are kept.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine($"App Installer is unavailable: {ex}");
            Version = "Development build";
            Status = "Automatic updates are unavailable in this development build.";
        }
    }

    public async Task<string> CheckForUpdatesAsync()
    {
        if (!CanCheckForUpdates)
        {
            return Status;
        }

        try
        {
            var result = await Package.Current.CheckUpdateAvailabilityAsync();
            return result.Availability switch
            {
                PackageUpdateAvailability.NoUpdates => "Mango is up to date.",
                PackageUpdateAvailability.Available or PackageUpdateAvailability.Required =>
                    "An update is available. Windows will install it automatically when Mango is not in use. You can keep using Mango until then.",
                _ => "Could not check for updates. Check your internet connection and try again. Windows will also retry in the background.",
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Update check failed: {ex}");
            return "Could not check for updates. Check your internet connection and try again. Windows will also retry in the background.";
        }
    }
}
