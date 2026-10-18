namespace Mango.Services;

public static class SettingsMigration
{
    public static void ImportMsixSettings(string localAppData)
    {
        var destination = Path.Combine(localAppData, "Mango", "settings.json");
        if (File.Exists(destination)) { return; }

        var source = Path.Combine(localAppData, "Packages",
            "D9984A3D-08FB-4EF1-BEF8-0B6EE6E0A12D_1z32rh13vfry6",
            "LocalCache", "Local", "Mango", "settings.json");
        if (!File.Exists(source)) { return; }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: false);
    }
}
