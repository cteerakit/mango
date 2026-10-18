using System.Security.Cryptography;
using System.Text.Json;
using Mango.Models;

namespace Mango.Services;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class AppSettings
{
    public const int DefaultGsiPort = 40000;
    public const string SettingsFileName = "settings.json";

    public int GsiPort { get; set; } = DefaultGsiPort;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public string GsiToken { get; set; } = string.Empty;
    public bool ShowPlayerPanel { get; set; } = true;
    public bool ShowBountyTimer { get; set; } = true;
    public bool ShowPowerTimer { get; set; } = true;
    public bool ShowWisdomTimer { get; set; } = true;
    public bool ShowLotusTimer { get; set; } = true;
    public bool ShowBuildPanel { get; set; } = true;
    public bool OverlayVisible { get; set; }
    public bool OverlayInteractive { get; set; }
    public double OverlayOpacity { get; set; } = 0.92;
    public bool TimerSoundsEnabled { get; set; } = true;
    public bool IsTurboMode { get; set; }
    /// <summary>How many seconds before a rune spawns its countdown widget appears.</summary>
    public int RuneTimerLeadSeconds { get; set; } = 15;
    // Keep the stored key so existing developer settings carry over.
    [System.Text.Json.Serialization.JsonPropertyName("DebugOverlayPreview")]
    public bool ShowMockData { get; set; }
    public string? SteamId { get; set; }
    /// <summary>Steam Web API key used for roster avatars (GetPlayerSummaries).</summary>
    public string SteamApiKey { get; set; } = string.Empty;
    /// <summary>STRATZ API token used for replay-analysed player positions.</summary>
    public string StratzApiToken { get; set; } = string.Empty;
    public Dictionary<string, PlayerNote> PlayerNotes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PlayerRankOverride> PlayerRanks { get; set; } = new();

    public HotkeyBinding ToggleOverlayHotkey { get; set; } = HotkeyBinding.DefaultToggleOverlay();
    public HotkeyBinding ToggleInteractiveHotkey { get; set; } = HotkeyBinding.DefaultToggleInteractive();

    public PanelLayoutSettings PanelLayout { get; set; } = new();

    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mango");

    public static string SettingsPath => Path.Combine(SettingsDirectory, SettingsFileName);

    private static string LegacySettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "APEM", SettingsFileName);

    public static AppSettings Load()
    {
        try
        {
            var path = File.Exists(SettingsPath) ? SettingsPath
                : File.Exists(LegacySettingsPath) ? LegacySettingsPath
                : null;

            if (path is not null)
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? CreateDefault();
                settings.Normalize();
                return settings;
            }
        }
        catch
        {
            // Fall through to defaults.
        }

        return CreateDefault();
    }

    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    public void Normalize()
    {
        if (!Enum.IsDefined(Theme))
        {
            Theme = AppTheme.System;
        }

        ToggleOverlayHotkey ??= HotkeyBinding.DefaultToggleOverlay();
        ToggleInteractiveHotkey ??= HotkeyBinding.DefaultToggleInteractive();
        PanelLayout ??= new PanelLayoutSettings();
        PlayerNotes = NormalizeNotes(PlayerNotes);
        PlayerRanks = Mango.Services.PlayerRanks.Normalize(PlayerRanks);

        if (ToggleOverlayHotkey.VirtualKey == 0)
        {
            ToggleOverlayHotkey = HotkeyBinding.DefaultToggleOverlay();
        }

        if (ToggleInteractiveHotkey.VirtualKey == 0)
        {
            ToggleInteractiveHotkey = HotkeyBinding.DefaultToggleInteractive();
        }

        RuneTimerLeadSeconds = Math.Clamp(RuneTimerLeadSeconds, 5, 120);
        SteamApiKey = SteamApiKey?.Trim() ?? string.Empty;
        StratzApiToken = StratzApiToken?.Trim() ?? string.Empty;
    }

    private static Dictionary<string, PlayerNote> NormalizeNotes(Dictionary<string, PlayerNote>? notes)
    {
        var normalized = new Dictionary<string, PlayerNote>(StringComparer.OrdinalIgnoreCase);
        if (notes is null)
        {
            return normalized;
        }

        foreach (var (key, value) in notes)
        {
            var note = value ?? new PlayerNote();
            note.FillIdentityFromKey(key);
            if (!note.HasSavedData)
            {
                continue;
            }

            var storageKey = string.IsNullOrWhiteSpace(note.StorageKey) ? key.Trim() : note.StorageKey;
            if (string.IsNullOrWhiteSpace(storageKey))
            {
                continue;
            }

            normalized[storageKey] = note;
        }

        return normalized;
    }

    private static AppSettings CreateDefault()
    {
        var settings = new AppSettings
        {
            GsiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
        };
        settings.Save();
        return settings;
    }
}

/// <summary>
/// Remembered widget positions. A null coordinate means the widget has never been
/// dragged, so the overlay places it at its default screen-edge anchor instead.
/// </summary>
public sealed class PanelLayoutSettings
{
    public double? PlayerX { get; set; }
    public double? PlayerY { get; set; }
    public double? BountyX { get; set; }
    public double? BountyY { get; set; }
    public double? PowerX { get; set; }
    public double? PowerY { get; set; }
    public double? WisdomX { get; set; }
    public double? WisdomY { get; set; }
    public double? LotusX { get; set; }
    public double? LotusY { get; set; }
    public double? BuildX { get; set; }
    public double? BuildY { get; set; }
}
