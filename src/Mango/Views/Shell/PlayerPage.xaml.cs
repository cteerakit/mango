using Mango.Models;
using Mango.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Mango.Views.Shell;

public sealed partial class PlayerPage : Page
{
    private CancellationTokenSource? _loadCts;
    private bool _suppressPickerChange;
    private string? _selectedSteamId;
    private string? _loadedSteamId;
    private string? _loadingSteamId;
    private IReadOnlyList<PlayerPickerItem> _pickerItems = Array.Empty<PlayerPickerItem>();

    public PlayerPage()
    {
        InitializeComponent();
        ShellContentLayout.Attach(LayoutRoot, ContentColumn);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppServices.MatchStore.SnapshotUpdated += OnSnapshotUpdated;

        // Explicit navigation (e.g. Match name click) can open another player;
        // otherwise always default to the local user.
        var requestedSteamId = e.Parameter is PlayerPageNavigationArgs args
            ? args.SteamId?.Trim() ?? string.Empty
            : string.Empty;
        var steamId = FirstNonEmpty(requestedSteamId, ResolveLocalSteamId());

        ApplyPlayerPicker(steamId);
        steamId = FirstNonEmpty(steamId, ResolveSelectedSteamId(steamId));
        if (string.IsNullOrWhiteSpace(steamId))
        {
            ClearProfile("No Steam ID found. Launch Dota 2 and connect to a match, or click a player name on the Match page to open them here.");
            return;
        }

        SelectPlayer(steamId);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppServices.MatchStore.SnapshotUpdated -= OnSnapshotUpdated;
        CancelLoad();
        base.OnNavigatedFrom(e);
    }

    private void OnSnapshotUpdated(MatchSnapshot snapshot)
    {
        var localId = ResolveLocalSteamId();
        if (string.IsNullOrWhiteSpace(_selectedSteamId) && !string.IsNullOrWhiteSpace(localId))
        {
            ApplyPlayerPicker(localId);
            SelectPlayer(localId);
            return;
        }

        ApplyPlayerPicker(_selectedSteamId);
        if (!string.IsNullOrWhiteSpace(_selectedSteamId))
        {
            RefreshLocalIdentity(_selectedSteamId);
        }
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedSteamId))
        {
            return;
        }

        _loadedSteamId = null;
        SelectPlayer(_selectedSteamId, forceReload: true);
    }

    private void OnPlayerPickerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPickerChange || PlayerPicker.SelectedItem is not PlayerPickerItem selected)
        {
            return;
        }

        SelectPlayer(selected.SteamId);
    }

    private void ApplyPlayerPicker(string? preferredSteamId)
    {
        var players = BuildPlayerOptions();
        var hasPlayers = players.Count > 0;

        PickerEmptyText.Visibility = hasPlayers ? Visibility.Collapsed : Visibility.Visible;

        if (!PickerListsEqual(_pickerItems, players))
        {
            _pickerItems = players;
            _suppressPickerChange = true;
            PlayerPicker.ItemsSource = players;
            _suppressPickerChange = false;
        }

        var localId = ResolveLocalSteamId();
        var target = players.FirstOrDefault(player =>
                string.Equals(player.SteamId, preferredSteamId, StringComparison.OrdinalIgnoreCase))
            ?? players.FirstOrDefault(player =>
                string.Equals(player.SteamId, _selectedSteamId, StringComparison.OrdinalIgnoreCase))
            ?? players.FirstOrDefault(player =>
                string.Equals(player.SteamId, localId, StringComparison.OrdinalIgnoreCase))
            ?? players.FirstOrDefault();

        _suppressPickerChange = true;
        PlayerPicker.SelectedItem = target;
        _suppressPickerChange = false;

        if (target is not null)
        {
            _selectedSteamId = target.SteamId;
        }

        PlayerPicker.IsEnabled = hasPlayers && !LoadingRing.IsActive;
    }

    private string? ResolveSelectedSteamId(string? preferredSteamId)
    {
        if (!string.IsNullOrWhiteSpace(preferredSteamId))
        {
            return preferredSteamId;
        }

        if (PlayerPicker.SelectedItem is PlayerPickerItem selected)
        {
            return selected.SteamId;
        }

        return FirstNonEmpty(_selectedSteamId, ResolveLocalSteamId());
    }

    private void SelectPlayer(string steamId, bool forceReload = false)
    {
        steamId = steamId.Trim();
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return;
        }

        _selectedSteamId = steamId;

        if (!forceReload && string.Equals(steamId, _loadedSteamId, StringComparison.OrdinalIgnoreCase))
        {
            RefreshLocalIdentity(steamId);
            return;
        }

        if (!forceReload && string.Equals(steamId, _loadingSteamId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = LoadPlayerAsync(steamId);
    }

    /// <summary>Local/self Steam ID from live GSI, falling back to the last known self ID in settings.</summary>
    private static string ResolveLocalSteamId()
    {
        var fromGsi = AppServices.MatchStore.Snapshot.SteamId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(fromGsi))
        {
            if (!string.Equals(AppServices.Settings.SteamId, fromGsi, StringComparison.OrdinalIgnoreCase))
            {
                AppServices.Settings.SteamId = fromGsi;
                AppServices.Settings.Save();
            }

            return fromGsi;
        }

        return AppServices.Settings.SteamId?.Trim() ?? string.Empty;
    }

    private async Task LoadPlayerAsync(string steamId)
    {
        CancelLoad();
        var loadCts = new CancellationTokenSource();
        _loadCts = loadCts;
        var token = loadCts.Token;
        _selectedSteamId = steamId;
        _loadingSteamId = steamId;

        SetLoading(true);
        RefreshLocalIdentity(steamId);

        try
        {
            var overview = await AppServices.OpenDota.GetPlayerOverviewAsync(steamId, token);
            if (!ReferenceEquals(loadCts, _loadCts) || token.IsCancellationRequested)
            {
                return;
            }

            if (overview is null)
            {
                _loadedSteamId = null;
                BindRemoteUnavailable(
                    SteamIdConverter.ToAccountId(steamId) is null
                        ? "This player ID is not a valid Steam or OpenDota account ID."
                        : "OpenDota is temporarily unavailable. Check your internet connection and tap Retry, or try again in a few minutes.");
                return;
            }

            _loadedSteamId = steamId;
            BindOverview(overview);
            SubtitleText.Text = $"Overall stats and recent matches for {overview.DisplayName}.";
        }
        catch (OperationCanceledException)
        {
            // Ignore navigation away or a newer load superseding this one.
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(loadCts, _loadCts) && !token.IsCancellationRequested)
            {
                _loadedSteamId = null;
                BindRemoteUnavailable($"Could not load OpenDota data: {ex.Message}");
            }
        }
        finally
        {
            if (ReferenceEquals(loadCts, _loadCts))
            {
                _loadCts = null;
                _loadingSteamId = null;
                SetLoading(false);
            }
        }
    }

    private void RefreshLocalIdentity(string steamId)
    {
        PlayerNotesTransfer.TryGetByPlayerId(AppServices.Settings.PlayerNotes, steamId, out var note);

        var snapshotPlayer = AppServices.MatchStore.Snapshot.Players
            .FirstOrDefault(player => string.Equals(player.SteamId, steamId, StringComparison.OrdinalIgnoreCase));

        var name = FirstNonEmpty(note?.PlayerName, snapshotPlayer?.Name);
        NameText.Text = DisplayOrDash(name);
        SteamIdText.Text = DisplayOrDash(steamId);
        NoteText.Text = DisplayOrDash(note?.Content ?? string.Empty);
        LikesText.Text = (note?.LikeCount ?? 0).ToString();
        DislikesText.Text = (note?.DislikeCount ?? 0).ToString();

        IdentityCard.Visibility = Visibility.Visible;
    }

    private void BindOverview(PlayerOverview overview)
    {
        if (!string.IsNullOrWhiteSpace(overview.DisplayName) && overview.DisplayName != "—")
        {
            NameText.Text = overview.DisplayName;
        }

        RankText.Text = overview.RankLabel;
        WinsText.Text = overview.Wins.ToString();
        LossesText.Text = overview.Losses.ToString();
        WinRateText.Text = overview.WinRatePercent is { } rate ? $"{rate:0.#}%" : "—";

        var matches = overview.RecentMatches.ToList();
        MatchesList.ItemsSource = matches;
        MatchesEmptyText.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MatchesHeaderGrid.Visibility = matches.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        MatchesList.Visibility = matches.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Visibility = Visibility.Collapsed;

        OverallCard.Visibility = Visibility.Visible;
        MatchesCard.Visibility = Visibility.Visible;
    }

    private void BindRemoteUnavailable(string message)
    {
        RankText.Text = "—";
        WinsText.Text = "—";
        LossesText.Text = "—";
        WinRateText.Text = "—";
        MatchesList.ItemsSource = null;
        MatchesEmptyText.Text = message;
        MatchesEmptyText.Visibility = Visibility.Visible;
        MatchesHeaderGrid.Visibility = Visibility.Collapsed;
        MatchesList.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Visible;
        OverallCard.Visibility = Visibility.Visible;
        MatchesCard.Visibility = Visibility.Visible;
        SubtitleText.Text = message;
    }

    private void ClearProfile(string message)
    {
        CancelLoad();
        _selectedSteamId = null;
        _loadedSteamId = null;
        _loadingSteamId = null;
        IdentityCard.Visibility = Visibility.Collapsed;
        OverallCard.Visibility = Visibility.Collapsed;
        MatchesCard.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        SubtitleText.Text = message;
        PickerEmptyText.Visibility = _pickerItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        SetLoading(false);
    }

    private void SetLoading(bool isLoading)
    {
        LoadingRing.IsActive = isLoading;
        LoadingRing.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        PlayerPicker.IsEnabled = !isLoading && _pickerItems.Count > 0;
        RetryButton.IsEnabled = !isLoading;
    }

    private void CancelLoad()
    {
        if (_loadCts is null)
        {
            return;
        }

        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = null;
        _loadingSteamId = null;
    }

    private static bool PickerListsEqual(
        IReadOnlyList<PlayerPickerItem> left,
        IReadOnlyList<PlayerPickerItem> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].SteamId, right[i].SteamId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(left[i].Name, right[i].Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static List<PlayerPickerItem> BuildPlayerOptions()
    {
        var players = new Dictionary<string, PlayerPickerItem>(StringComparer.OrdinalIgnoreCase);
        var localId = ResolveLocalSteamId();
        var snapshot = AppServices.MatchStore.Snapshot;

        void Upsert(string steamId, string? name)
        {
            steamId = steamId.Trim();
            if (string.IsNullOrWhiteSpace(steamId))
            {
                return;
            }

            if (players.TryGetValue(steamId, out var existing))
            {
                if (string.IsNullOrWhiteSpace(existing.Name) && !string.IsNullOrWhiteSpace(name))
                {
                    players[steamId] = new PlayerPickerItem
                    {
                        SteamId = steamId,
                        Name = name.Trim(),
                    };
                }

                return;
            }

            players[steamId] = new PlayerPickerItem
            {
                SteamId = steamId,
                Name = name?.Trim() ?? string.Empty,
            };
        }

        // Always include the local user first when known.
        if (!string.IsNullOrWhiteSpace(localId))
        {
            var localName = FirstNonEmpty(
                snapshot.PlayerName,
                snapshot.Players
                    .FirstOrDefault(player =>
                        string.Equals(player.SteamId, localId, StringComparison.OrdinalIgnoreCase))
                    ?.Name);
            Upsert(localId, string.IsNullOrWhiteSpace(localName) ? "You" : localName);
        }

        foreach (var note in AppServices.Settings.PlayerNotes.Values)
        {
            Upsert(note.PlayerId ?? string.Empty, note.PlayerName);
        }

        foreach (var player in snapshot.Players)
        {
            Upsert(player.SteamId ?? string.Empty, player.Name);
        }

        return players.Values
            .OrderBy(player =>
                string.Equals(player.SteamId, localId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(player => player.SteamId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string DisplayOrDash(string value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;
}
