using Mango.Models;
using Mango.Models.Gsi;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace Mango.Services;

public sealed partial class MatchStore : ObservableObject
{
    private static readonly TimeSpan MinPublishInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinRosterFetchInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FailedRosterFetchInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RosterFetchTimeout = TimeSpan.FromSeconds(25);

    private DateTimeOffset _lastPublishUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRosterFetchUtc = DateTimeOffset.MinValue;
    private string _lastRosterFetchKey = string.Empty;
    private string? _rosterFetchInFlightKey;
    private bool _lastRosterFetchFailed;
    private int _rosterFetchGeneration;
    private CancellationTokenSource? _rosterFetchCts;
    private DispatcherQueue? _dispatcherQueue;
    private Func<MatchSnapshot, CancellationToken, Task<MatchRosterResolution?>>? _rosterResolver;
    private string _lastRosterFetchMessage = string.Empty;

    [ObservableProperty]
    private MatchSnapshot _snapshot = new();

    [ObservableProperty]
    private bool _isGsiConnected;

    [ObservableProperty]
    private bool _isDebugPreview;

    [ObservableProperty]
    private string _connectionStatus = "Waiting for Dota 2";

    [ObservableProperty]
    private DateTimeOffset? _lastPayloadUtc;

    public bool LastRosterFetchFailed => _lastRosterFetchFailed;

    public string LastRosterFetchMessage => _lastRosterFetchMessage;

    public event Action<MatchSnapshot>? SnapshotUpdated;

    public void Configure(
        DispatcherQueue dispatcherQueue,
        Func<MatchSnapshot, CancellationToken, Task<MatchRosterResolution?>> rosterResolver)
    {
        _dispatcherQueue = dispatcherQueue;
        _rosterResolver = rosterResolver;
    }

    public void ApplyPayload(GsiPayload payload)
    {
        var snapshot = RetainRicherRoster(GsiNormalizer.Normalize(payload));
        IsDebugPreview = false;
        PublishThrottled(snapshot, snapshot.IsInGame || snapshot.IsInDraft ? "Live match" : "Connected (menu/lobby)", connected: true);
        QueueExternalRosterFetch(snapshot);
    }

    public void ApplyDebugPreview()
    {
        CancelRosterFetch();
        IsDebugPreview = true;
        var snapshot = MockMatchData.CreateSnapshot();
        snapshot.MatchId = "debug-" + Guid.NewGuid().ToString("N");
        Publish(snapshot, "Debug preview", connected: true);
    }

    public void ClearDebugPreview()
    {
        if (!IsDebugPreview)
        {
            return;
        }

        IsDebugPreview = false;
        MarkDisconnected();
    }

    public void MarkDisconnected()
    {
        CancelRosterFetch();
        IsGsiConnected = false;
        IsDebugPreview = false;
        ConnectionStatus = "Waiting for Dota 2";
        Snapshot = new MatchSnapshot { IsConnected = false };
        LastPayloadUtc = null;
        _lastPublishUtc = DateTimeOffset.MinValue;
        _lastRosterFetchKey = string.Empty;
        _lastRosterFetchUtc = DateTimeOffset.MinValue;
        _rosterFetchInFlightKey = null;
        _lastRosterFetchFailed = false;
        _lastRosterFetchMessage = string.Empty;
        SnapshotUpdated?.Invoke(Snapshot);
    }

    private void QueueExternalRosterFetch(MatchSnapshot snapshot)
    {
        if (_rosterResolver is null || _dispatcherQueue is null || IsDebugPreview || !snapshot.IsConnected)
        {
            return;
        }

        if (MatchRosterMerger.CountIdentified(snapshot.Players) >= 10)
        {
            return;
        }

        var localAccountId = SteamIdConverter.ToAccountId(snapshot.SteamId);
        if (!PlayerNotesMatch.IsUsableMatchId(snapshot.MatchId)
            && localAccountId is null
            && string.IsNullOrWhiteSpace(snapshot.ServerSteamId))
        {
            return;
        }

        var fetchKey = $"{snapshot.MatchId}|{snapshot.ServerSteamId}|{localAccountId}";
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(fetchKey, _rosterFetchInFlightKey, StringComparison.Ordinal))
        {
            return;
        }

        var retryInterval = _lastRosterFetchFailed
            ? FailedRosterFetchInterval
            : MinRosterFetchInterval;
        if (string.Equals(fetchKey, _lastRosterFetchKey, StringComparison.Ordinal)
            && now - _lastRosterFetchUtc < retryInterval)
        {
            return;
        }

        CancelRosterFetch();
        var timeoutCts = new CancellationTokenSource(RosterFetchTimeout);
        _rosterFetchCts = timeoutCts;
        var token = timeoutCts.Token;
        var fetchGeneration = _rosterFetchGeneration;
        var matchSnapshot = snapshot;
        var gsiPlayers = snapshot.Players;
        _lastRosterFetchKey = fetchKey;
        _lastRosterFetchUtc = DateTimeOffset.UtcNow;
        _rosterFetchInFlightKey = fetchKey;

        _ = Task.Run(async () =>
        {
            try
            {
                var resolution = await _rosterResolver(matchSnapshot, token);
                var external = resolution?.Players;
                if (external is null
                    || MatchRosterMerger.CountIdentified(external) <= MatchRosterMerger.CountIdentified(gsiPlayers))
                {
                    _lastRosterFetchFailed = true;
                    _lastRosterFetchMessage = string.IsNullOrWhiteSpace(resolution?.ServerSteamId)
                        ? "This game is not in Steam's public live list, so the other players stay hidden until the match is parsed."
                        : "Found the live server, but Steam did not return the other players yet.";
                    PersistServerSteamId(matchSnapshot, resolution?.ServerSteamId);
                    NotifySnapshot();
                    return;
                }

                _lastRosterFetchFailed = false;
                _lastRosterFetchMessage = string.Empty;
                var merged = MatchRosterMerger.Merge(gsiPlayers, external);
                token.ThrowIfCancellationRequested();

                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested || IsDebugPreview || !Snapshot.IsConnected)
                    {
                        return;
                    }

                    if (PlayerNotesMatch.IsUsableMatchId(matchSnapshot.MatchId)
                        && !string.Equals(Snapshot.MatchId, matchSnapshot.MatchId, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (MatchRosterMerger.CountIdentified(merged) <= MatchRosterMerger.CountIdentified(Snapshot.Players)
                        && string.IsNullOrWhiteSpace(resolution?.ServerSteamId))
                    {
                        return;
                    }

                    var updated = CloneWithPlayers(Snapshot, merged);
                    if (!string.IsNullOrWhiteSpace(resolution?.ServerSteamId))
                    {
                        updated.ServerSteamId = resolution.ServerSteamId;
                    }

                    Publish(updated, ConnectionStatus, connected: true);
                });
            }
            catch (OperationCanceledException)
            {
                if (fetchGeneration == _rosterFetchGeneration && !IsDebugPreview && Snapshot.IsConnected)
                {
                    _lastRosterFetchFailed = true;
                    _lastRosterFetchMessage = "Lobby lookup timed out — retrying.";
                    NotifySnapshot();
                }
            }
            catch
            {
                _lastRosterFetchFailed = true;
                _lastRosterFetchMessage = "Lobby lookup failed — retrying.";
                NotifySnapshot();
            }
            finally
            {
                if (string.Equals(_rosterFetchInFlightKey, fetchKey, StringComparison.Ordinal))
                {
                    _rosterFetchInFlightKey = null;
                }
            }
        }, token);
    }

    private void PersistServerSteamId(MatchSnapshot matchSnapshot, string? serverSteamId)
    {
        if (string.IsNullOrWhiteSpace(serverSteamId) || _dispatcherQueue is null)
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (IsDebugPreview || !Snapshot.IsConnected)
            {
                return;
            }

            if (PlayerNotesMatch.IsUsableMatchId(matchSnapshot.MatchId)
                && !string.Equals(Snapshot.MatchId, matchSnapshot.MatchId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(Snapshot.ServerSteamId, serverSteamId, StringComparison.Ordinal))
            {
                return;
            }

            var updated = CloneWithPlayers(Snapshot, Snapshot.Players);
            updated.ServerSteamId = serverSteamId;
            Publish(updated, ConnectionStatus, connected: true);
        });
    }

    private static MatchSnapshot CloneWithPlayers(MatchSnapshot source, IReadOnlyList<MatchPlayer> players) =>
        new()
        {
            ReceivedAtUtc = source.ReceivedAtUtc,
            IsConnected = source.IsConnected,
            GameState = source.GameState,
            ClockTimeSeconds = source.ClockTimeSeconds,
            GameTimeSeconds = source.GameTimeSeconds,
            IsDaytime = source.IsDaytime,
            MatchId = source.MatchId,
            ServerSteamId = source.ServerSteamId,
            RadiantScore = source.RadiantScore,
            DireScore = source.DireScore,
            NetWorthLead = source.NetWorthLead,
            PlayerName = source.PlayerName,
            SteamId = source.SteamId,
            Kills = source.Kills,
            Deaths = source.Deaths,
            Assists = source.Assists,
            LastHits = source.LastHits,
            Denies = source.Denies,
            Gpm = source.Gpm,
            Xpm = source.Xpm,
            Gold = source.Gold,
            TeamName = source.TeamName,
            HeroName = source.HeroName,
            HeroLevel = source.HeroLevel,
            Health = source.Health,
            MaxHealth = source.MaxHealth,
            Mana = source.Mana,
            MaxMana = source.MaxMana,
            HeroAlive = source.HeroAlive,
            Items = source.Items,
            Abilities = source.Abilities,
            Draft = source.Draft,
            Players = players,
        };

    private void CancelRosterFetch()
    {
        if (_rosterFetchCts is null)
        {
            return;
        }

        _rosterFetchCts.Cancel();
        _rosterFetchCts.Dispose();
        _rosterFetchCts = null;
        _rosterFetchInFlightKey = null;
        _rosterFetchGeneration++;
    }

    private void NotifySnapshot()
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (IsDebugPreview || !Snapshot.IsConnected)
            {
                return;
            }

            SnapshotUpdated?.Invoke(Snapshot);
        });
    }

    private void PublishThrottled(MatchSnapshot snapshot, string connectionStatus, bool connected)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPublishUtc < MinPublishInterval)
        {
            return;
        }

        Publish(snapshot, connectionStatus, connected);
    }

    private MatchSnapshot RetainRicherRoster(MatchSnapshot incoming)
    {
        var current = Snapshot;
        if (!current.IsConnected || (!incoming.IsInGame && !incoming.IsInDraft))
        {
            return incoming;
        }

        incoming.Players = MatchRosterMerger.RetainIfRicher(
            incoming.Players,
            current.Players,
            incoming.MatchId,
            current.MatchId);

        if (string.IsNullOrWhiteSpace(incoming.ServerSteamId))
        {
            incoming.ServerSteamId = current.ServerSteamId;
        }

        return incoming;
    }

    private void Publish(MatchSnapshot snapshot, string connectionStatus, bool connected)
    {
        _lastPublishUtc = DateTimeOffset.UtcNow;
        Snapshot = snapshot;
        IsGsiConnected = connected;
        ConnectionStatus = connectionStatus;
        LastPayloadUtc = snapshot.ReceivedAtUtc;
        SnapshotUpdated?.Invoke(snapshot);
    }
}
