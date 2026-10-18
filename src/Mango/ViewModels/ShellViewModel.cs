using Mango.Models;
using Mango.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Mango.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AppSettings _settings = AppServices.Settings;

    [ObservableProperty]
    private string _gsiStatus = "Starting…";

    [ObservableProperty]
    private string _installMessage = string.Empty;

    [ObservableProperty]
    private bool _gsiConfigInstalled;

    [ObservableProperty]
    private string _connectionStatus = "Waiting for Dota 2";

    [ObservableProperty]
    private string _lastUpdate = "—";

    [ObservableProperty]
    private double _overlayOpacity;

    [ObservableProperty]
    private bool _isTurboMode;

    [ObservableProperty]
    private int _runeTimerLeadSeconds = 15;

    [ObservableProperty]
    private bool _timerSoundsEnabled;

    [ObservableProperty]
    private bool _showMockData;

    [ObservableProperty]
    private bool _showPlayerPanel;

    [ObservableProperty]
    private bool _showBountyTimer;

    [ObservableProperty]
    private bool _showPowerTimer;

    [ObservableProperty]
    private bool _showWisdomTimer;

    [ObservableProperty]
    private bool _showLotusTimer;

    [ObservableProperty]
    private bool _showBuildPanel;

    [ObservableProperty]
    private string _toggleOverlayHotkeyText = string.Empty;

    [ObservableProperty]
    private string _toggleInteractiveHotkeyText = string.Empty;

    [ObservableProperty]
    private string _hotkeyStatusMessage = string.Empty;

    [ObservableProperty]
    private string _panelLayoutStatusMessage = string.Empty;

    public SolidColorBrush ConnectionDotBrush { get; } = new(Colors.Gray);

    public HotkeyBinding EditingToggleOverlay { get; } = HotkeyBinding.DefaultToggleOverlay();
    public HotkeyBinding EditingToggleInteractive { get; } = HotkeyBinding.DefaultToggleInteractive();

    public ShellViewModel()
    {
        OverlayOpacity = _settings.OverlayOpacity;
        IsTurboMode = _settings.IsTurboMode;
        RuneTimerLeadSeconds = _settings.RuneTimerLeadSeconds;
        TimerSoundsEnabled = _settings.TimerSoundsEnabled;
        ShowMockData = _settings.ShowMockData;
        ShowPlayerPanel = _settings.ShowPlayerPanel;
        ShowBountyTimer = _settings.ShowBountyTimer;
        ShowPowerTimer = _settings.ShowPowerTimer;
        ShowWisdomTimer = _settings.ShowWisdomTimer;
        ShowLotusTimer = _settings.ShowLotusTimer;
        ShowBuildPanel = _settings.ShowBuildPanel;
        ReloadHotkeysFromSettings();

        GsiStatus = $"Listening on 127.0.0.1:{_settings.GsiPort}";
        AppServices.GsiListener.StatusChanged += status => GsiStatus = status;
        AppServices.MatchStore.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MatchStore.ConnectionStatus)
                or nameof(MatchStore.IsGsiConnected)
                or nameof(MatchStore.IsUsingMockData)
                or null)
            {
                RefreshBriefConnectionStatus();
            }

            if (e.PropertyName is nameof(MatchStore.LastPayloadUtc) or null)
            {
                LastUpdate = AppServices.MatchStore.LastPayloadUtc?.ToLocalTime().ToString("T") ?? "—";
            }
        };

        var install = AppServices.EnsureGsiConfigInstalled();
        GsiConfigInstalled = install.Success;
        InstallMessage = install.Message;
        RefreshBriefConnectionStatus();
    }

    private void RefreshBriefConnectionStatus()
    {
        var store = AppServices.MatchStore;
        ConnectionStatus = store.ConnectionStatus;

        if (store.IsUsingMockData)
        {
            SetDotColor(Color.FromArgb(255, 59, 130, 246));
            return;
        }

        if (store.IsGsiConnected
            && store.ConnectionStatus.Contains("Live", StringComparison.OrdinalIgnoreCase))
        {
            SetDotColor(Color.FromArgb(255, 22, 163, 74));
            return;
        }

        if (store.IsGsiConnected)
        {
            SetDotColor(Color.FromArgb(255, 202, 138, 4));
            return;
        }

        SetDotColor(Color.FromArgb(255, 156, 163, 175));
    }

    private void SetDotColor(Color color)
    {
        if (ConnectionDotBrush.Color == color)
        {
            return;
        }

        ConnectionDotBrush.Color = color;
    }

    public void ReloadHotkeysFromSettings()
    {
        _settings.Normalize();
        EditingToggleOverlay.CopyFrom(_settings.ToggleOverlayHotkey);
        EditingToggleInteractive.CopyFrom(_settings.ToggleInteractiveHotkey);
        RefreshHotkeyTexts();
    }

    public void RefreshHotkeyTexts()
    {
        ToggleOverlayHotkeyText = HotkeyFormatting.ToDisplayString(EditingToggleOverlay);
        ToggleInteractiveHotkeyText = HotkeyFormatting.ToDisplayString(EditingToggleInteractive);
    }

    public string HotkeyHintText =>
        $"Hotkeys: {HotkeyFormatting.ToDisplayString(_settings.ToggleOverlayHotkey)} toggle overlay, " +
        $"{HotkeyFormatting.ToDisplayString(_settings.ToggleInteractiveHotkey)} interactive mode. " +
        $"If the overlay blocks clicks, press {HotkeyFormatting.ToDisplayString(_settings.ToggleOverlayHotkey)} to hide it.";

    [RelayCommand]
    private void ReinstallGsiConfig()
    {
        var result = AppServices.GsiInstaller.Install(_settings);
        GsiConfigInstalled = result.Success;
        InstallMessage = result.Message;
    }

    [RelayCommand]
    private void ShowOverlay()
    {
        AppServices.OverlayService.ShowOverlay();
    }

    [RelayCommand]
    private void HideOverlay()
    {
        AppServices.OverlayService.HideOverlay();
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _settings.OverlayOpacity = OverlayOpacity;
        _settings.IsTurboMode = IsTurboMode;
        _settings.RuneTimerLeadSeconds = RuneTimerLeadSeconds;
        _settings.TimerSoundsEnabled = TimerSoundsEnabled;
        _settings.ShowMockData = ShowMockData;
        _settings.ShowPlayerPanel = ShowPlayerPanel;
        _settings.ShowBountyTimer = ShowBountyTimer;
        _settings.ShowPowerTimer = ShowPowerTimer;
        _settings.ShowWisdomTimer = ShowWisdomTimer;
        _settings.ShowLotusTimer = ShowLotusTimer;
        _settings.ShowBuildPanel = ShowBuildPanel;
        _settings.Save();
        AppServices.OverlayService.ApplySettings();
        AppServices.ApplyMockDataMode();
    }

    [RelayCommand]
    private void ResetPanelPositions()
    {
        AppServices.OverlayService.ResetPanelPositions();
        PanelLayoutStatusMessage = "Widget positions restored to defaults.";
    }

    [RelayCommand]
    private void SaveHotkeys()
    {
        if (EditingToggleOverlay.EqualsBinding(EditingToggleInteractive))
        {
            HotkeyStatusMessage = "Overlay and interactive shortcuts must be different.";
            return;
        }

        if (!HasModifier(EditingToggleOverlay) || !HasModifier(EditingToggleInteractive))
        {
            HotkeyStatusMessage = "Each shortcut needs at least one modifier (Ctrl, Alt, Shift, or Win).";
            return;
        }

        _settings.ToggleOverlayHotkey = EditingToggleOverlay.Clone();
        _settings.ToggleInteractiveHotkey = EditingToggleInteractive.Clone();
        _settings.Save();

        var applied = AppServices.HotkeyService.ApplyRegistrations();
        RefreshHotkeyTexts();
        OnPropertyChanged(nameof(HotkeyHintText));
        HotkeyStatusMessage = applied
            ? "Shortcuts saved and applied."
            : "Saved, but Windows rejected one or both hotkeys (they may already be in use).";
    }

    [RelayCommand]
    private void ResetHotkeys()
    {
        EditingToggleOverlay.CopyFrom(HotkeyBinding.DefaultToggleOverlay());
        EditingToggleInteractive.CopyFrom(HotkeyBinding.DefaultToggleInteractive());
        RefreshHotkeyTexts();
        HotkeyStatusMessage = "Defaults restored — click Save shortcuts to apply.";
    }

    private static bool HasModifier(HotkeyBinding binding) =>
        binding.Ctrl || binding.Alt || binding.Shift || binding.Win;
}
