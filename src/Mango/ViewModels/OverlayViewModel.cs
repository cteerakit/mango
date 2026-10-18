using System.Collections.ObjectModel;
using Mango.Models;
using Mango.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mango.ViewModels;

public sealed partial class OverlayViewModel : ObservableObject
{
    private readonly MatchStore _store = AppServices.MatchStore;
    private readonly OpenDotaService _openDota = AppServices.OpenDota;
    private readonly AppSettings _settings = AppServices.Settings;
    private string? _buildHeroKey;
    private int _buildRequestId;

    [ObservableProperty]
    private MatchSnapshot _snapshot = new();

    [ObservableProperty]
    private double _overlayOpacity = 0.92;

    [ObservableProperty]
    private bool _isInteractive;

    [ObservableProperty]
    private bool _showPlayerPanel = true;

    [ObservableProperty]
    private bool _showBountyTimer = true;

    [ObservableProperty]
    private bool _showPowerTimer = true;

    [ObservableProperty]
    private bool _showWisdomTimer = true;

    [ObservableProperty]
    private bool _showLotusTimer = true;

    [ObservableProperty]
    private bool _showBuildPanel = true;

    public ObservableCollection<BuildSuggestionItem> BuildSuggestions { get; } = [];

    public PanelLayoutSettings Layout => _settings.PanelLayout;

    public OverlayViewModel()
    {
        ApplySettings();
        _store.SnapshotUpdated += OnSnapshotUpdated;
        OnSnapshotUpdated(_store.Snapshot);
    }

    private async void OnSnapshotUpdated(MatchSnapshot snapshot)
    {
        Snapshot = snapshot;

        if (_store.IsUsingMockData)
        {
            ++_buildRequestId;
            ApplyMockBuild();
            return;
        }

        if (string.IsNullOrWhiteSpace(snapshot.HeroName))
        {
            ++_buildRequestId;
            _buildHeroKey = null;
            BuildSuggestions.Clear();
        }
        else
        {
            await RefreshBuildDataAsync(snapshot.HeroName);
        }
    }

    private void ApplyMockBuild()
    {
        const string mockKey = "__mock__";
        if (_buildHeroKey == mockKey && BuildSuggestions.Count > 0)
        {
            return;
        }

        _buildHeroKey = mockKey;
        ReplaceBuildSuggestions(MockMatchData.CreateBuildSuggestions().Select(static i => i.Name));
    }

    private async Task RefreshBuildDataAsync(string heroInternalName)
    {
        if (string.Equals(_buildHeroKey, heroInternalName, StringComparison.OrdinalIgnoreCase) &&
            BuildSuggestions.Count > 0)
        {
            return;
        }

        var requestId = ++_buildRequestId;
        var heroes = await _openDota.GetHeroesAsync();
        if (requestId != _buildRequestId)
        {
            return;
        }

        var heroId = _openDota.ResolveHeroId(heroInternalName, heroes);
        if (heroId is null)
        {
            return;
        }

        var items = await _openDota.GetSuggestedItemsAsync(heroId.Value);
        if (requestId != _buildRequestId)
        {
            return;
        }

        _buildHeroKey = heroInternalName;
        ReplaceBuildSuggestions(items);
    }

    private void ReplaceBuildSuggestions(IEnumerable<string> names)
    {
        var next = names.ToList();
        if (BuildSuggestions.Count == next.Count &&
            BuildSuggestions.Select(static b => b.Name).SequenceEqual(next, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        BuildSuggestions.Clear();
        foreach (var name in next)
        {
            BuildSuggestions.Add(new BuildSuggestionItem { Name = name });
        }
    }

    public void RefreshSettings() => ApplySettings();

    private void ApplySettings()
    {
        OverlayOpacity = _settings.OverlayOpacity;
        IsInteractive = _settings.OverlayInteractive;
        ShowPlayerPanel = _settings.ShowPlayerPanel;
        ShowBountyTimer = _settings.ShowBountyTimer;
        ShowPowerTimer = _settings.ShowPowerTimer;
        ShowWisdomTimer = _settings.ShowWisdomTimer;
        ShowLotusTimer = _settings.ShowLotusTimer;
        ShowBuildPanel = _settings.ShowBuildPanel;
    }
}
