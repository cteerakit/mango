using Mango.Services;
using Xunit;

namespace Mango.Tests;

public sealed class SettingsMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mango-migration-{Guid.NewGuid()}");

    [Fact]
    public void ImportsPackagedSettingsWithoutChangingTheOriginal()
    {
        var source = WritePackagedSettings();
        SettingsMigration.ImportMsixSettings(_root);
        var destination = Path.Combine(_root, "Mango", "settings.json");
        Assert.Equal(File.ReadAllText(source), File.ReadAllText(destination));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void KeepsExistingUnpackagedSettings()
    {
        WritePackagedSettings();
        var destination = Path.Combine(_root, "Mango", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "existing settings");
        SettingsMigration.ImportMsixSettings(_root);
        Assert.Equal("existing settings", File.ReadAllText(destination));
    }

    [Fact]
    public void FreshInstallDoesNotCreateSettings()
    {
        SettingsMigration.ImportMsixSettings(_root);
        Assert.False(Directory.Exists(_root));
    }

    private string WritePackagedSettings()
    {
        var source = Path.Combine(_root, "Packages", "D9984A3D-08FB-4EF1-BEF8-0B6EE6E0A12D_1z32rh13vfry6",
            "LocalCache", "Local", "Mango", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "{\"Theme\":2,\"PlayerNotes\":{\"123\":{\"Note\":\"saved note\"}}}");
        return source;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
