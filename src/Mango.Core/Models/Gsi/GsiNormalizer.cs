namespace Mango.Models.Gsi;

public static class GsiNormalizer
{
    public static MatchSnapshot Normalize(GsiPayload payload)
    {
        var localPlayer = payload.Player?.Local;
        var localHero = payload.Hero?.Local;

        var snapshot = new MatchSnapshot
        {
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            IsConnected = true,
            GameState = payload.Map?.GameState ?? string.Empty,
            ClockTimeSeconds = payload.Map?.ClockTime ?? 0,
            GameTimeSeconds = payload.Map?.GameTime ?? 0,
            IsDaytime = payload.Map?.Daytime ?? true,
            MatchId = PlayerNotesMatch.NormalizeMatchId(payload.Map?.MatchId),
            ServerSteamId = payload.Map?.ServerSteamId?.Trim() ?? string.Empty,
            RadiantScore = payload.Map?.RadiantScore ?? 0,
            DireScore = payload.Map?.DireScore ?? 0,
            NetWorthLead = payload.Map?.RadiantGoldAdv is { Length: > 0 } adv ? adv[^1] : 0,
            PlayerName = localPlayer?.Name ?? string.Empty,
            SteamId = localPlayer?.SteamId ?? string.Empty,
            Kills = localPlayer?.Kills ?? 0,
            Deaths = localPlayer?.Deaths ?? 0,
            Assists = localPlayer?.Assists ?? 0,
            LastHits = localPlayer?.LastHits ?? 0,
            Denies = localPlayer?.Denies ?? 0,
            Gpm = localPlayer?.Gpm ?? 0,
            Xpm = localPlayer?.Xpm ?? 0,
            Gold = localPlayer?.Gold ?? 0,
            TeamName = localPlayer?.TeamName ?? string.Empty,
            HeroName = FormatInternalName(localHero?.Name),
            HeroLevel = localHero?.Level ?? 0,
            Health = localHero?.Health ?? 0,
            MaxHealth = localHero?.MaxHealth ?? 0,
            Mana = localHero?.Mana ?? 0,
            MaxMana = localHero?.MaxMana ?? 0,
            HeroAlive = localHero?.Alive ?? true,
        };

        snapshot.Items = NormalizeItems(payload.Items);
        snapshot.Abilities = NormalizeAbilities(payload.Abilities);
        snapshot.Draft = NormalizeDraft(payload.Draft, snapshot.TeamName);
        snapshot.Players = NormalizePlayers(payload);

        return snapshot;
    }

    internal static IReadOnlyList<MatchPlayer> NormalizePlayers(GsiPayload payload)
    {
        var candidates = new List<IReadOnlyList<MatchPlayer>>(3);

        var fromAllPlayers = NormalizeAllPlayers(payload.AllPlayers);
        if (fromAllPlayers.Count > 0)
        {
            candidates.Add(fromAllPlayers);
        }

        var fromTeams = NormalizeSpectatingTeams(payload);
        if (fromTeams.Count > 0)
        {
            candidates.Add(fromTeams);
        }

        var fromLocal = NormalizeLocalPlayer(payload);
        if (fromLocal.Count > 0)
        {
            candidates.Add(fromLocal);
        }

        // Playing GSI often sends a 1-entry allplayers; prefer the richest roster.
        return candidates
            .OrderByDescending(static list => list.Count(HasIdentity))
            .ThenByDescending(static list => list.Count)
            .FirstOrDefault()
            ?? Array.Empty<MatchPlayer>();
    }

    private static IReadOnlyList<MatchPlayer> NormalizeAllPlayers(Dictionary<string, GsiPlayer>? allPlayers)
    {
        if (allPlayers is not { Count: > 0 })
        {
            return Array.Empty<MatchPlayer>();
        }

        return allPlayers
            .Select(static pair =>
            {
                ApplyTeamHints(pair.Value, pair.Key);
                return ToMatchPlayer(pair.Value, pair.Value.Hero);
            })
            .Where(HasIdentity)
            .OrderBy(static player => TeamSortOrder(player.TeamName))
            .ThenBy(static player => player.TeamSlot)
            .ThenBy(static player => player.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<MatchPlayer> NormalizeSpectatingTeams(GsiPayload payload)
    {
        if (payload.Player?.Teams is not { Count: > 0 } teams)
        {
            return Array.Empty<MatchPlayer>();
        }

        var heroes = payload.Hero?.Teams;
        return teams
            .SelectMany(team => team.Value.Select(slot =>
            {
                var player = slot.Value;
                ApplyTeamFromKey(player, team.Key);
                ApplyTeamHints(player, slot.Key);
                var hero = FindSpectatedHero(heroes, team.Key, slot.Key) ?? player.Hero;
                return ToMatchPlayer(player, hero);
            }))
            .Where(HasIdentity)
            .OrderBy(static player => TeamSortOrder(player.TeamName))
            .ThenBy(static player => player.TeamSlot)
            .ThenBy(static player => player.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<MatchPlayer> NormalizeLocalPlayer(GsiPayload payload)
    {
        if (payload.Player?.Local is null)
        {
            return Array.Empty<MatchPlayer>();
        }

        var local = ToMatchPlayer(payload.Player.Local, payload.Hero?.Local ?? payload.Player.Local.Hero);
        return HasIdentity(local) ? [local] : Array.Empty<MatchPlayer>();
    }

    private static bool HasIdentity(MatchPlayer player) =>
        !string.IsNullOrWhiteSpace(player.SteamId) || !string.IsNullOrWhiteSpace(player.Name);

    private static MatchPlayer ToMatchPlayer(GsiPlayer player, GsiHero? hero) =>
        new()
        {
            SteamId = player.SteamId ?? string.Empty,
            Name = string.IsNullOrWhiteSpace(player.Name)
                ? (string.IsNullOrWhiteSpace(player.SteamId) ? "Unknown" : player.SteamId)
                : player.Name,
            TeamName = ResolveTeamName(player),
            TeamSlot = player.TeamSlot,
            HeroName = hero?.Name ?? string.Empty,
            HeroLevel = hero?.Level ?? 0,
            Kills = player.Kills,
            Deaths = player.Deaths,
            Assists = player.Assists,
            LastHits = player.LastHits,
            Denies = player.Denies,
            Gpm = player.Gpm,
            Xpm = player.Xpm,
            Gold = player.Gold,
        };

    private static void ApplyTeamFromKey(GsiPlayer player, string teamKey)
    {
        if (string.IsNullOrWhiteSpace(player.TeamName))
        {
            player.TeamName = teamKey switch
            {
                var key when key.Equals("team2", StringComparison.OrdinalIgnoreCase) => "radiant",
                var key when key.Equals("team3", StringComparison.OrdinalIgnoreCase) => "dire",
                _ => player.TeamName,
            };
        }

        if (player.Team == 0)
        {
            player.Team = teamKey switch
            {
                var key when key.Equals("team2", StringComparison.OrdinalIgnoreCase) => 2,
                var key when key.Equals("team3", StringComparison.OrdinalIgnoreCase) => 3,
                _ => player.Team,
            };
        }
    }

    private static void ApplyTeamHints(GsiPlayer player, string mapKey)
    {
        if (!string.IsNullOrWhiteSpace(player.TeamName) || player.Team is 2 or 3)
        {
            return;
        }

        // Keys like "team2.player0" from flattened spectator allplayers.
        if (mapKey.Contains("team2", StringComparison.OrdinalIgnoreCase))
        {
            player.TeamName = "radiant";
            player.Team = 2;
            return;
        }

        if (mapKey.Contains("team3", StringComparison.OrdinalIgnoreCase))
        {
            player.TeamName = "dire";
            player.Team = 3;
            return;
        }

        if (TryParsePlayerIndex(mapKey, out var index))
        {
            // Conventional GSI player0–player4 radiant, player5–player9 dire.
            player.TeamName = index < 5 ? "radiant" : "dire";
            player.Team = index < 5 ? 2 : 3;
            if (player.TeamSlot == 0)
            {
                player.TeamSlot = index % 5;
            }

            return;
        }

        if (player.PlayerSlot is int slot)
        {
            if (slot is >= 128 and <= 132)
            {
                player.TeamName = "dire";
                player.Team = 3;
                player.TeamSlot = slot - 128;
            }
            else if (slot is >= 0 and <= 4)
            {
                player.TeamName = "radiant";
                player.Team = 2;
                player.TeamSlot = slot;
            }
            else if (slot is >= 5 and <= 9)
            {
                player.TeamName = "dire";
                player.Team = 3;
                player.TeamSlot = slot - 5;
            }
        }
    }

    private static bool TryParsePlayerIndex(string key, out int index)
    {
        index = -1;
        const string prefix = "player";
        var start = key.LastIndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        var number = key[(start + prefix.Length)..];
        return int.TryParse(number, out index);
    }

    private static GsiHero? FindSpectatedHero(
        Dictionary<string, Dictionary<string, GsiHero>>? heroes,
        string teamKey,
        string playerKey)
    {
        if (heroes is null)
        {
            return null;
        }

        if (heroes.TryGetValue(teamKey, out var teamHeroes)
            && teamHeroes.TryGetValue(playerKey, out var hero))
        {
            return hero;
        }

        foreach (var team in heroes.Values)
        {
            if (team.TryGetValue(playerKey, out hero))
            {
                return hero;
            }
        }

        return null;
    }

    private static string ResolveTeamName(GsiPlayer player)
    {
        if (!string.IsNullOrWhiteSpace(player.TeamName))
        {
            return player.TeamName;
        }

        return player.Team switch
        {
            2 => "radiant",
            3 => "dire",
            _ => string.Empty,
        };
    }

    private static int TeamSortOrder(string teamName)
    {
        if (teamName.Contains("radiant", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (teamName.Contains("dire", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
    }

    private static IReadOnlyList<SlotSnapshot> NormalizeItems(Dictionary<string, GsiItemSlot>? items)
    {
        if (items is null)
        {
            return Array.Empty<SlotSnapshot>();
        }

        return items
            .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
            .Select(static kv => new SlotSnapshot
            {
                Slot = kv.Key,
                Name = FormatInternalName(kv.Value.Name),
                Cooldown = kv.Value.Cooldown,
                CanCast = kv.Value.CanCast,
            })
            .Where(static s => !string.IsNullOrWhiteSpace(s.Name) && s.Name != "empty")
            .ToList();
    }

    private static IReadOnlyList<SlotSnapshot> NormalizeAbilities(Dictionary<string, GsiAbilitySlot>? abilities)
    {
        if (abilities is null)
        {
            return Array.Empty<SlotSnapshot>();
        }

        return abilities
            .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
            .Select(static kv => new SlotSnapshot
            {
                Slot = kv.Key,
                Name = FormatInternalName(kv.Value.Name),
                Level = kv.Value.Level,
                Cooldown = kv.Value.Cooldown,
                CanCast = kv.Value.CanCast,
            })
            .Where(static s => !string.IsNullOrWhiteSpace(s.Name) && s.Name != "empty")
            .ToList();
    }

    private static DraftSnapshot NormalizeDraft(GsiDraft? draft, string teamName)
    {
        if (draft is null)
        {
            return new DraftSnapshot();
        }

        var radiant = draft.Team2;
        var dire = draft.Team3;
        var isRadiant = teamName.Contains("radiant", StringComparison.OrdinalIgnoreCase);

        return new DraftSnapshot
        {
            IsActive = true,
            ActiveTeam = draft.ActiveTeam,
            IsPickPhase = draft.Pick,
            TimeRemaining = draft.ActiveTeamTimeRemaining,
            RadiantHeroIds = ExtractHeroIds(radiant),
            DireHeroIds = ExtractHeroIds(dire),
            PlayerIsRadiant = isRadiant,
        };
    }

    private static IReadOnlyList<int> ExtractHeroIds(GsiDraftTeam? team)
    {
        if (team is null)
        {
            return Array.Empty<int>();
        }

        return new[]
        {
            team.Pick0Id, team.Pick1Id, team.Pick2Id, team.Pick3Id, team.Pick4Id,
        }.Where(static id => id > 0).ToList();
    }

    public static string FormatInternalName(string? internalName)
    {
        if (string.IsNullOrWhiteSpace(internalName))
        {
            return string.Empty;
        }

        return internalName
            .Replace("npc_dota_hero_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("item_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("ability_", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatClock(int clockSeconds)
    {
        var abs = Math.Abs(clockSeconds);
        var minutes = abs / 60;
        var seconds = abs % 60;
        var sign = clockSeconds < 0 ? "-" : string.Empty;
        return $"{sign}{minutes:00}:{seconds:00}";
    }
}
