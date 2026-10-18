using Microsoft.UI.Xaml.Media;

namespace Mango.Services;

/// <summary>
/// Uses Dota's own Radiance HUD font when the game is installed locally so overlay
/// text matches the in-game panels exactly. Falls back to Segoe UI otherwise.
/// </summary>
public sealed class HudFontService
{
    private const string FontsSubfolder = "Fonts";
    private const string RegularFile = "radiance-regular.otf";
    private const string SemiboldFile = "radiance-semibold.otf";

    public FontFamily LabelFont { get; private set; } = new("Segoe UI");

    public FontFamily ValueFont { get; private set; } = new("Segoe UI");

    public bool UsesGameFont { get; private set; }

    public void Initialize()
    {
        try
        {
            var sourceFolder = FindPanoramaFontFolder();
            if (sourceFolder is null)
            {
                return;
            }

            var targetFolder = Path.Combine(AppContext.BaseDirectory, "Assets", FontsSubfolder);
            Directory.CreateDirectory(targetFolder);

            if (!TryStageFont(sourceFolder, targetFolder, RegularFile) ||
                !TryStageFont(sourceFolder, targetFolder, SemiboldFile))
            {
                return;
            }

            LabelFont = new FontFamily($"ms-appx:///Assets/{FontsSubfolder}/{RegularFile}#Radiance");
            ValueFont = new FontFamily($"ms-appx:///Assets/{FontsSubfolder}/{SemiboldFile}#Radiance Semibold");
            UsesGameFont = true;
        }
        catch
        {
            // Keep the Segoe UI fallback if anything about the copy fails.
        }
    }

    private static bool TryStageFont(string sourceFolder, string targetFolder, string fileName)
    {
        var source = Path.Combine(sourceFolder, fileName);
        if (!File.Exists(source))
        {
            return false;
        }

        var target = Path.Combine(targetFolder, fileName);
        if (!File.Exists(target) || File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(target))
        {
            File.Copy(source, target, overwrite: true);
        }

        return true;
    }

    private static string? FindPanoramaFontFolder()
    {
        foreach (var dotaPath in GsiConfigInstaller.FindDotaInstallPaths())
        {
            var fonts = Path.Combine(dotaPath, "game", "dota", "panorama", "fonts");
            if (File.Exists(Path.Combine(fonts, SemiboldFile)))
            {
                return fonts;
            }
        }

        return null;
    }
}
