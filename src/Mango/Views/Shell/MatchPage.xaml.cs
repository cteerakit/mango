using Mango.Models;
using Mango.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Mango.Views.Shell;

public sealed partial class MatchPage : Page
{
    private readonly Dictionary<string, PlayerMatchEnrichment> _enrichmentBySteamId =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _enrichCts;
    private string _enrichmentRosterKey = string.Empty;
    private string _boundRosterKey = string.Empty;
    private int _enrichmentGeneration;

    public MatchPage()
    {
        InitializeComponent();
        ShellContentLayout.AttachExpanding(LayoutRoot, ContentColumn);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppServices.MatchStore.SnapshotUpdated += OnSnapshotUpdated;
        _boundRosterKey = string.Empty;
        BindPlayers(AppServices.MatchStore.Snapshot, requestEnrichment: true, force: true);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppServices.MatchStore.SnapshotUpdated -= OnSnapshotUpdated;
        CancelEnrichment();
        base.OnNavigatedFrom(e);
    }

    private void OnSnapshotUpdated(MatchSnapshot snapshot) =>
        BindPlayers(snapshot, requestEnrichment: true, force: false);

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
        BindPlayers(AppServices.MatchStore.Snapshot, requestEnrichment: false, force: true);
    }

    private void OnLikeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MatchPlayer player })
        {
            CastVote(player, PlayerNoteVote.Like);
        }
    }

    private void OnDislikeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MatchPlayer player })
        {
            CastVote(player, PlayerNoteVote.Dislike);
        }
    }

    private void CastVote(MatchPlayer player, PlayerNoteVote vote)
    {
        var settings = AppServices.Settings;
        if (!PlayerNotesTransfer.TryVote(
                settings.PlayerNotes,
                player.Name,
                player.SteamId,
                AppServices.MatchStore.Snapshot.MatchId,
                vote))
        {
            return;
        }

        settings.Save();
        BindPlayers(AppServices.MatchStore.Snapshot, requestEnrichment: false, force: true);
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
        var players = snapshot.Players
            .Select(player =>
            {
                PlayerNotesTransfer.TryGetByPlayerId(notes, player.SteamId, out var note);
                player.Note = note?.Content ?? string.Empty;
                player.LikeCount = note?.LikeCount ?? 0;
                player.DislikeCount = note?.DislikeCount ?? 0;
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
                    player.WinRateLabel = enrichment.WinRateLabel;
                    player.MatchesLabel = enrichment.MatchesLabel;
                    player.IsProfilePrivate = enrichment.IsProfilePrivate;
                }
                else
                {
                    player.AvatarUrl = string.Empty;
                    player.RankIconUrl = string.Empty;
                    player.RankStarUrl = string.Empty;
                    player.RankLabel = string.Empty;
                    player.WinRateLabel = string.Empty;
                    player.MatchesLabel = string.Empty;
                    player.IsProfilePrivate = false;
                }

                player.HeroImageUrl = AssetUrls.HeroIcon(player.HeroIconKey);
                return player;
            })
            .ToList();

        RadiantPlayersList.ItemsSource = players
            .Where(player => player.TeamName.Contains("radiant", StringComparison.OrdinalIgnoreCase))
            .ToList();
        DirePlayersList.ItemsSource = players
            .Where(player => player.TeamName.Contains("dire", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var lobbyPlayers = players
            .Where(player =>
                !player.TeamName.Contains("radiant", StringComparison.OrdinalIgnoreCase)
                && !player.TeamName.Contains("dire", StringComparison.OrdinalIgnoreCase))
            .ToList();
        LobbyPlayersList.ItemsSource = lobbyPlayers;
        LobbyPlayersCard.Visibility = lobbyPlayers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SubtitleText.Text = DescribeRoster(snapshot, players.Count);

        if (requestEnrichment)
        {
            QueueEnrichment(players);
        }
    }

    private static string BuildRosterKey(MatchSnapshot snapshot)
    {
        var players = snapshot.Players
            .OrderBy(static player => player.TeamName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static player => player.TeamSlot)
            .ThenBy(static player => player.SteamId, StringComparer.OrdinalIgnoreCase)
            .Select(static player =>
                $"{player.SteamId}\u001f{player.Name}\u001f{player.TeamName}\u001f{player.HeroIconKey}");
        return $"{snapshot.MatchId}\u001e{string.Join('\u001e', players)}";
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

            var overviews = await Task.WhenAll(overviewTasks);
            cancellationToken.ThrowIfCancellationRequested();

            var avatars = await AppServices.SteamApi.GetPlayerSummariesAsync(
                steamIds,
                AppServices.Settings.SteamApiKey,
                cancellationToken);

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
                    overview?.MmrEstimate,
                    lobbyAverageTiers);

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
                    WinRateLabel = overview?.WinRatePercent is { } winRate
                        ? $"{winRate:0.#}% WR ({overview.Wins}-{overview.Losses})"
                        : string.Empty,
                    MatchesLabel = overview is null
                        ? string.Empty
                        : (overview.Wins + overview.Losses).ToString("N0"),
                    IsProfilePrivate = summary?.IsPrivate == true || overview?.IsPrivate == true,
                };
            }

            if (generation != _enrichmentGeneration || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
                BindPlayers(AppServices.MatchStore.Snapshot, requestEnrichment: false, force: true));
        }
        catch (OperationCanceledException)
        {
            // Navigation away or newer enrichment.
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
        if (AppServices.MatchStore.IsDebugPreview)
        {
            return "Debug preview roster.";
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

        return "Players and heroes update when the roster changes. Notes and OpenDota/Steam enrichment load in the background.";
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
        public string WinRateLabel { get; init; } = string.Empty;
        public string MatchesLabel { get; init; } = string.Empty;
        public bool IsProfilePrivate { get; init; }
    }
}
