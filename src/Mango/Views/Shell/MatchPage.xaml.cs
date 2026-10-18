using Mango.Models;
using Mango.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Mango.Views.Shell;

public sealed partial class MatchPage : Page
{
    private readonly Dictionary<string, PlayerMatchEnrichment> _enrichmentBySteamId =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Brush RadiantBrush = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x5F, 0xB0, 0x4A));
    private static readonly Brush DireBrush = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xD0, 0x4A, 0x3A));
    private static readonly Brush LobbyBrush = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x8A, 0x8A, 0x8A));

    private readonly HashSet<ScrollViewer> _statsScrollViewers = new();
    private double _statsHorizontalOffset;

    private CancellationTokenSource? _enrichCts;
    private string _enrichmentRosterKey = string.Empty;
    private string _boundRosterKey = string.Empty;
    private int _enrichmentGeneration;
    private readonly Dictionary<string, PlayerMatchHistory> _history = new();
    private CancellationTokenSource? _historyCts;
    private string _historyRosterKey = string.Empty;

    public MatchPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    private void OnStatsPointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.KeyModifiers != Windows.System.VirtualKeyModifiers.Shift)
        {
            return;
        }

        var properties = e.GetCurrentPoint((UIElement)sender).Properties;
        if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0)
        {
            return;
        }

        StatsScrollBar.ChangeView(
            Math.Clamp(_statsHorizontalOffset - properties.MouseWheelDelta, 0, StatsScrollBar.ScrollableWidth),
            null, null, disableAnimation: true);
        e.Handled = true;
    }

    private void OnStatsScrollLoaded(object sender, RoutedEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        _statsScrollViewers.Add(viewer);
        viewer.ChangeView(_statsHorizontalOffset, null, null, disableAnimation: true);
    }

    private void OnStatsScrollUnloaded(object sender, RoutedEventArgs e) =>
        _statsScrollViewers.Remove((ScrollViewer)sender);

    private void OnStatsScrollViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        var source = (ScrollViewer)sender;
        // Ignore notifications from viewers catching up to the shared offset.
        if (Math.Abs(source.HorizontalOffset - _statsHorizontalOffset) < 0.5)
        {
            return;
        }

        _statsHorizontalOffset = source.HorizontalOffset;
        foreach (var viewer in _statsScrollViewers.Append(StatsScrollBar))
        {
            if (viewer != source && Math.Abs(viewer.HorizontalOffset - _statsHorizontalOffset) >= 0.5)
            {
                viewer.ChangeView(_statsHorizontalOffset, null, null, disableAnimation: true);
            }
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppServices.MatchStore.SnapshotUpdated += OnSnapshotUpdated;
        BindPlayers(AppServices.MatchStore.MatchPageSnapshot, requestEnrichment: true, force: false);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppServices.MatchStore.SnapshotUpdated -= OnSnapshotUpdated;
        base.OnNavigatedFrom(e);
    }

    private void OnSnapshotUpdated(MatchSnapshot _) =>
        BindPlayers(AppServices.MatchStore.MatchPageSnapshot, requestEnrichment: true, force: false);

    private void OnPlayerNameClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MatchPlayer player }
            || string.IsNullOrWhiteSpace(player.SteamId))
        {
            return;
        }

        Frame.Navigate(typeof(PlayerPage), new PlayerPageNavigationArgs
        {
            SteamId = player.SteamId,
            Name = player.Name,
        });
    }

    private async void OnEditRankClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MatchPlayer player }
            || !player.CanEditRank
            || SteamIdConverter.ToAccountId(player.SteamId) is not { } accountId)
        {
            return;
        }

        var settings = AppServices.Settings;
        var savedRank = PlayerRanks.Get(settings.PlayerRanks, player.SteamId, player.IsProfilePrivate, player.ReportedRankTier);
        var options = PlayerRanks.Tiers
            .Select(tier => new RankOption(tier, OpenDotaService.FormatRankTier(tier))).ToList();
        var picker = new ComboBox
        {
            Header = "Manual rank",
            PlaceholderText = "Choose a rank",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = options,
            DisplayMemberPath = nameof(RankOption.Label),
            SelectedItem = options.FirstOrDefault(option => option.Tier == savedRank?.RankTier),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(picker, "ManualRankPicker");
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "Save a manual rank for a private profile or a player with no reported rank, or clear the saved rank.",
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        content.Children.Add(picker);
        var dialog = new ContentDialog
        {
            Title = string.IsNullOrWhiteSpace(player.Name) ? "Player rank" : $"Rank — {player.Name}",
            Content = content,
            PrimaryButtonText = "Save rank",
            SecondaryButtonText = savedRank is null ? string.Empty : "Clear override",
            CloseButtonText = "Cancel",
            IsPrimaryButtonEnabled = picker.SelectedItem is RankOption,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        picker.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = picker.SelectedItem is RankOption;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None
            || !_enrichmentBySteamId.TryGetValue(player.SteamId, out var latestProfile)
            || !PlayerRanks.CanOverride(latestProfile.IsProfilePrivate, latestProfile.ReportedRankTier))
        {
            return;
        }

        var previous = new Dictionary<string, PlayerRankOverride>(settings.PlayerRanks);
        var key = accountId.ToString();
        if (result == ContentDialogResult.Primary && picker.SelectedItem is RankOption selected)
        {
            settings.PlayerRanks[key] = new PlayerRankOverride
            {
                PlayerId = player.SteamId,
                PlayerName = player.Name,
                RankTier = selected.Tier,
            };
        }
        else
        {
            settings.PlayerRanks.Remove(key);
        }

        try
        {
            settings.Save();
            BindPlayers(AppServices.MatchStore.MatchPageSnapshot, requestEnrichment: false, force: true);
            RankSaveStatus.Severity = InfoBarSeverity.Success;
            RankSaveStatus.Message = result == ContentDialogResult.Primary
                ? "Rank saved for this player." : "Rank override cleared.";
        }
        catch (Exception)
        {
            settings.PlayerRanks = previous;
            RankSaveStatus.Severity = InfoBarSeverity.Error;
            RankSaveStatus.Message = "Could not save the rank. Please try again.";
        }

        RankSaveStatus.IsOpen = true;
    }

    private sealed record RankOption(int Tier, string Label);

    private async void OnAddNoteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MatchPlayer player })
        {
            return;
        }

        var input = new TextBox
        {
            AcceptsReturn = true,
            Height = 140,
            Text = player.Note,
            TextWrapping = TextWrapping.Wrap,
        };

        var dialog = new ContentDialog
        {
            Title = string.IsNullOrWhiteSpace(player.Name) ? "Player note" : $"Note — {player.Name}",
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        if (!string.IsNullOrWhiteSpace(player.Note))
        {
            dialog.SecondaryButtonText = "Clear";
        }

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
        {
            return;
        }

        var note = result == ContentDialogResult.Primary ? input.Text.Trim() : string.Empty;
        SavePlayerNote(player, note);
        BindPlayers(AppServices.MatchStore.MatchPageSnapshot, requestEnrichment: false, force: true);
    }

    private void OnLikeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MatchPlayer player })
        {
            CastVote(player, PlayerNoteVote.Like);
        }
    }

    private void OnDislikeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MatchPlayer player })
        {
            CastVote(player, PlayerNoteVote.Dislike);
        }
    }

    private void OnToxicClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MatchPlayer player }
            && PlayerNotesTransfer.TryToggleToxic(AppServices.Settings.PlayerNotes, player.Name, player.SteamId))
        {
            AppServices.Settings.Save();
            BindPlayers(AppServices.MatchStore.MatchPageSnapshot, requestEnrichment: false, force: true);
        }
    }

    private void CastVote(MatchPlayer player, PlayerNoteVote vote)
    {
        var settings = AppServices.Settings;
        if (!PlayerNotesTransfer.TryVote(
                settings.PlayerNotes,
                player.Name,
                player.SteamId,
                AppServices.MatchStore.MatchPageSnapshot.MatchId,
                vote))
        {
            return;
        }

        settings.Save();
        BindPlayers(AppServices.MatchStore.MatchPageSnapshot, requestEnrichment: false, force: true);
    }

    private void BindPlayers(MatchSnapshot snapshot, bool requestEnrichment, bool force)
    {
        var rosterKey = BuildRosterKey(snapshot);
        if (!force && string.Equals(rosterKey, _boundRosterKey, StringComparison.Ordinal))
        {
            return;
        }

        _boundRosterKey = rosterKey;

        var notes = AppServices.Settings.PlayerNotes;
        var localAccount = SteamIdConverter.ToAccountId(LocalSteamId(snapshot));
        var players = snapshot.Players
            .Select(player =>
            {
                PlayerNotesTransfer.TryGetByPlayerId(notes, player.SteamId, out var note);
                player.Note = note?.Content ?? string.Empty;
                player.LikeCount = note?.LikeCount ?? 0;
                player.DislikeCount = note?.DislikeCount ?? 0;
                player.IsToxic = note?.IsToxic ?? false;
                var (canVote, reason) = VoteAvailability(player, snapshot.MatchId);
                player.CanVote = canVote;
                player.VoteUnavailableReason = reason;
                player.CurrentMatchVote = note?.HasVotedInMatch(snapshot.MatchId) == true
                    ? note.LastVote
                    : string.Empty;

                if (_enrichmentBySteamId.TryGetValue(player.SteamId, out var enrichment))
                {
                    player.AvatarUrl = enrichment.AvatarUrl;
                    player.RankIconUrl = enrichment.RankIconUrl;
                    player.RankStarUrl = enrichment.RankStarUrl;
                    player.RankLabel = enrichment.RankLabel;
                    player.RankIsEstimated = enrichment.RankIsEstimated;
                    player.MatchesLabel = enrichment.MatchesLabel;
                    player.IsProfilePrivate = enrichment.IsProfilePrivate;
                    player.ReportedRankTier = enrichment.ReportedRankTier;
                }
                else
                {
                    player.AvatarUrl = string.Empty;
                    player.RankIconUrl = string.Empty;
                    player.RankStarUrl = string.Empty;
                    player.RankLabel = string.Empty;
                    player.RankIsEstimated = false;
                    player.MatchesLabel = string.Empty;
                    player.IsProfilePrivate = false;
                    player.ReportedRankTier = null;
                }

                if (PlayerRanks.Get(AppServices.Settings.PlayerRanks, player.SteamId, player.IsProfilePrivate, player.ReportedRankTier) is { } savedRank)
                {
                    player.RankIconUrl = AssetUrls.RankIcon(savedRank.RankTier);
                    player.RankStarUrl = AssetUrls.RankStar(savedRank.RankTier);
                    player.RankLabel = $"Manual: {OpenDotaService.FormatRankTier(savedRank.RankTier)}";
                    player.RankIsEstimated = false;
                }

                player.IsLocalPlayer = localAccount is not null
                    && localAccount == SteamIdConverter.ToAccountId(player.SteamId);
                player.HeroImageUrl = AssetUrls.HeroIcon(player.HeroIconKey);
                player.History = _history.GetValueOrDefault(HistoryKey(snapshot, player)) ?? new();
                if (AppServices.MatchStore.IsUsingMockData)
                {
                    MockMatchData.ApplyPreviewDetails(player);
                }

                return player;
            })
            .ToList();

        var radiantPlayers = players
            .Where(player => player.TeamName.Contains("radiant", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var direPlayers = players
            .Where(player => player.TeamName.Contains("dire", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var lobbyPlayers = players
            .Where(player =>
                !player.TeamName.Contains("radiant", StringComparison.OrdinalIgnoreCase)
                && !player.TeamName.Contains("dire", StringComparison.OrdinalIgnoreCase))
            .ToList();
        RadiantPlayersList.ItemsSource = radiantPlayers;
        DirePlayersList.ItemsSource = direPlayers;
        RadiantPlayersCard.Visibility = radiantPlayers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DirePlayersCard.Visibility = direPlayers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LobbyPlayersList.ItemsSource = lobbyPlayers;
        LobbyPlayersCard.Visibility = lobbyPlayers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RadiantHeader.Content = new TeamHeader
        {
            Title = "The Radiant",
            AccentBrush = RadiantBrush,
        };
        DireHeader.Content = new TeamHeader
        {
            Title = "The Dire",
            AccentBrush = DireBrush,
        };
        LobbyHeader.Content = new TeamHeader { Title = "Lobby", AccentBrush = LobbyBrush };
        SubtitleText.Text = DescribeRoster(snapshot, players.Count);

        if (requestEnrichment && !AppServices.MatchStore.IsUsingMockData)
        {
            QueueEnrichment(players);
            QueueHistory(snapshot, players);
        }
    }

    private static string LocalSteamId(MatchSnapshot snapshot) =>
        SteamIdConverter.ToAccountId(snapshot.SteamId) is not null
            ? snapshot.SteamId : AppServices.Settings.SteamId ?? string.Empty;

    private static string HistoryKey(MatchSnapshot snapshot, MatchPlayer player) =>
        $"{LocalSteamId(snapshot)}|{snapshot.MatchId}|{player.SteamId}|{player.HeroIconKey}";

    private void QueueHistory(MatchSnapshot snapshot, IReadOnlyList<MatchPlayer> players)
    {
        var requests = players.Select(player => (
            Key: HistoryKey(snapshot, player), SteamId: player.SteamId, Hero: player.HeroIconKey)).ToList();
        var rosterKey = string.Join('\u001e', requests.Select(request => request.Key));
        if (_historyRosterKey == rosterKey) return;
        _historyRosterKey = rosterKey;
        _historyCts?.Cancel();
        _historyCts?.Dispose();
        _historyCts = new CancellationTokenSource();
        _ = LoadHistoryAsync(requests, LocalSteamId(snapshot), snapshot.MatchId, _historyCts.Token);
    }

    private async Task LoadHistoryAsync(
        IReadOnlyList<(string Key, string SteamId, string Hero)> requests,
        string localSteamId, string matchId, CancellationToken cancellationToken)
    {
        try
        {
            var stratzToken = AppServices.Settings.StratzApiToken;
            var results = await new MatchHistoryLoader(AppServices.OpenDota).LoadAsync(
                requests.Where(request => !_history.ContainsKey(request.Key)).ToList(),
                localSteamId, matchId, cancellationToken,
                string.IsNullOrWhiteSpace(stratzToken) ? null
                    : (id, token) => AppServices.Stratz.GetMatchPositionsAsync(id, stratzToken, token),
                string.IsNullOrWhiteSpace(stratzToken) ? null
                    : (id, account, token) => AppServices.Stratz.GetPlayerPurchasesAsync(id, account, stratzToken, token));
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (key, history) in results)
                _history[key] = history;
            BindPlayers(AppServices.MatchStore.MatchPageSnapshot, requestEnrichment: false, force: true);
        }
        catch (OperationCanceledException) { }
    }

    private static string BuildRosterKey(MatchSnapshot snapshot)
    {
        var players = snapshot.Players
            .OrderBy(static player => player.TeamName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static player => player.TeamSlot)
            .ThenBy(static player => player.SteamId, StringComparer.OrdinalIgnoreCase)
            .Select(static player =>
            {
                PlayerNotesTransfer.TryGetByPlayerId(AppServices.Settings.PlayerNotes, player.SteamId, out var note);
                var rank = PlayerRanks.Get(AppServices.Settings.PlayerRanks, player.SteamId, isProfilePrivate: true);
                return $"{player.SteamId}\u001f{player.Name}\u001f{player.TeamName}\u001f{player.TeamSlot}\u001f{player.HeroIconKey}"
                    + $"\u001f{note?.Content}\u001f{note?.LikeCount}\u001f{note?.DislikeCount}\u001f{note?.IsToxic}\u001f{note?.LastVoteMatchId}\u001f{note?.LastVote}\u001f{rank?.RankTier}";
            });
        return $"{LocalSteamId(snapshot)}\u001e{snapshot.MatchId}\u001e{AppServices.MatchStore.IsShowingPreviousMatch}\u001e{string.Join('\u001e', players)}";
    }

    private void QueueEnrichment(IReadOnlyList<MatchPlayer> players)
    {
        var steamIds = players
            .Select(static player => player.SteamId?.Trim() ?? string.Empty)
            .Where(static id => !string.IsNullOrWhiteSpace(id) && SteamIdConverter.ToAccountId(id) is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rosterKey = string.Join('|', steamIds);
        if (steamIds.Count == 0)
        {
            _enrichmentRosterKey = rosterKey;
            return;
        }

        var missing = steamIds.Any(id => !_enrichmentBySteamId.ContainsKey(id));
        if (!missing && string.Equals(rosterKey, _enrichmentRosterKey, StringComparison.Ordinal))
        {
            return;
        }

        _enrichmentRosterKey = rosterKey;
        CancelEnrichment();
        _enrichCts = new CancellationTokenSource();
        var generation = ++_enrichmentGeneration;
        var token = _enrichCts.Token;
        _ = EnrichRosterAsync(steamIds, generation, token);
    }

    private async Task EnrichRosterAsync(
        IReadOnlyList<string> steamIds,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var avatarsTask = AppServices.SteamApi.GetPlayerSummariesAsync(
                steamIds,
                AppServices.Settings.SteamApiKey,
                cancellationToken);
            var overviewTasks = steamIds.Select(async steamId =>
            {
                try
                {
                    var overview = await AppServices.OpenDota.GetPlayerOverviewAsync(steamId, cancellationToken);
                    return (SteamId: steamId, Overview: overview);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    return (SteamId: steamId, Overview: (PlayerOverview?)null);
                }
            });

            var overviewsTask = Task.WhenAll(overviewTasks);
            await Task.WhenAll(overviewsTask, avatarsTask);
            var overviews = await overviewsTask;
            cancellationToken.ThrowIfCancellationRequested();

            var avatars = await avatarsTask;
            cancellationToken.ThrowIfCancellationRequested();

            var lobbyAverageTiers = RankEstimator.AverageRankTier(
                overviews
                    .Select(static row => row.Overview?.RankTier)
                    .Where(static tier => tier is > 0)
                    .Select(static tier => tier!.Value));

            foreach (var (steamId, overview) in overviews)
            {
                avatars.TryGetValue(steamId, out var summary);
                var resolved = RankEstimator.Resolve(
                    overview?.RankTier,
                    overview?.LeaderboardRank,
                    overview?.HistoryRankEstimate,
                    overview?.MmrEstimate,
                    lobbyAverageTiers,
                    isProfilePrivate: overview?.IsPrivate == true);

                _enrichmentBySteamId[steamId] = new PlayerMatchEnrichment
                {
                    AvatarUrl = summary?.AvatarUrl ?? string.Empty,
                    RankIconUrl = resolved.RankTier is > 0
                        ? AssetUrls.RankIcon(resolved.RankTier)
                        : string.Empty,
                    RankStarUrl = resolved.RankTier is > 0
                        ? AssetUrls.RankStar(resolved.RankTier)
                        : string.Empty,
                    RankLabel = resolved.Label,
                    RankIsEstimated = resolved.IsEstimated,
                    MatchesLabel = overview?.IsPrivate == true
                        ? "\u2014"
                        : overview is null
                        ? string.Empty
                        : overview.WinRatePercent is { } winRate
                            ? $"{overview.Wins + overview.Losses:N0} ({winRate:0.#}%)"
                            : (overview.Wins + overview.Losses).ToString("N0"),
                    IsProfilePrivate = overview?.IsPrivate == true,
                    ReportedRankTier = overview?.RankTier,
                };
            }

            if (generation != _enrichmentGeneration || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
                BindPlayers(AppServices.MatchStore.MatchPageSnapshot, requestEnrichment: false, force: true));
        }
        catch (OperationCanceledException)
        {
            // A newer roster superseded this enrichment.
        }
    }

    private void CancelEnrichment()
    {
        if (_enrichCts is null)
        {
            return;
        }

        _enrichCts.Cancel();
        _enrichCts.Dispose();
        _enrichCts = null;
    }

    private static void SavePlayerNote(MatchPlayer player, string note)
    {
        if (string.IsNullOrWhiteSpace(player.SteamId))
        {
            return;
        }

        var settings = AppServices.Settings;
        PlayerNotesTransfer.Upsert(settings.PlayerNotes, player.Name, player.SteamId, note);
        settings.Save();
    }

    private static string DescribeRoster(MatchSnapshot snapshot, int playerCount)
    {
        if (AppServices.MatchStore.IsUsingMockData)
        {
            return "Sample roster � mock data is enabled.";
        }

        if (AppServices.MatchStore.IsShowingPreviousMatch)
        {
            return "Last match — stays here until the next match starts.";
        }

        if (!snapshot.IsConnected)
        {
            return "Waiting for Dota 2.";
        }

        if (playerCount == 0)
        {
            return "Connected, but this GSI payload has no player roster yet.";
        }

        if (playerCount == 1)
        {
            if (!string.IsNullOrWhiteSpace(AppServices.MatchStore.LastRosterFetchMessage))
            {
                return AppServices.MatchStore.LastRosterFetchMessage;
            }

            if (AppServices.MatchStore.LastRosterFetchFailed)
            {
                return PlayerNotesMatch.IsUsableMatchId(snapshot.MatchId)
                    ? "Only your player is in GSI. Could not load the full lobby yet — retrying automatically."
                    : "Only your local player is in GSI. Waiting for a match ID, then the lobby will load from Steam/OpenDota.";
            }

            return PlayerNotesMatch.IsUsableMatchId(snapshot.MatchId)
                ? "Only your player is in GSI so far. Loading the full lobby from Steam…"
                : snapshot.IsInDraft
                    ? "While playing, Dota GSI usually only sends your own player during pick. Waiting for a match ID to load the lobby."
                    : "Only your local player is in this GSI payload. Loading the full lobby from Steam…";
        }

        return snapshot.Players.Any(player => string.IsNullOrWhiteSpace(player.TeamName))
            ? $"Showing {playerCount} players. Team and hero details are not available for every player yet."
            : "KDA, position and items are from each player's latest completed match on their hero. History loads in the background.";
    }

    private static (bool CanVote, string Reason) VoteAvailability(MatchPlayer player, string matchId)
    {
        if (string.IsNullOrWhiteSpace(player.SteamId))
        {
            return (false, "Player has no ID.");
        }

        if (!PlayerNotesMatch.IsUsableMatchId(matchId))
        {
            return (false, "Waiting for a match ID.");
        }

        return (true, string.Empty);
    }

    private sealed class PlayerMatchEnrichment
    {
        public string AvatarUrl { get; init; } = string.Empty;
        public string RankIconUrl { get; init; } = string.Empty;
        public string RankStarUrl { get; init; } = string.Empty;
        public string RankLabel { get; init; } = string.Empty;
        public bool RankIsEstimated { get; init; }
        public string MatchesLabel { get; init; } = string.Empty;
        public bool IsProfilePrivate { get; init; }
        public int? ReportedRankTier { get; init; }
    }
}
