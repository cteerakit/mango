using Mango.Models;

namespace Mango.Services;

internal static class MockMatchData
{
    public static MatchSnapshot CreateSnapshot() => new()
    {
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        IsConnected = true,
        GameState = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS",
        ClockTimeSeconds = 228,
        GameTimeSeconds = (32 * 60) + 48,
        IsDaytime = true,
        MatchId = "debug-preview",
        RadiantScore = 24,
        DireScore = 19,
        NetWorthLead = 1840,
        PlayerName = "Mango Tester",
        SteamId = "76561198000000001",
        Kills = 8,
        Deaths = 2,
        Assists = 6,
        LastHits = 214,
        Denies = 12,
        Gpm = 612,
        Xpm = 704,
        Gold = 2840,
        TeamName = "radiant",
        HeroName = "npc_dota_hero_juggernaut",
        HeroLevel = 18,
        Health = 1680,
        MaxHealth = 2100,
        Mana = 420,
        MaxMana = 600,
        HeroAlive = true,
        Items =
        [
            new SlotSnapshot { Slot = "slot0", Name = "bfury" },
            new SlotSnapshot { Slot = "slot1", Name = "power_treads" },
            new SlotSnapshot { Slot = "slot2", Name = "manta" },
        ],
        Abilities =
        [
            new SlotSnapshot { Slot = "ability0", Name = "blade_fury", Level = 4 },
            new SlotSnapshot { Slot = "ability1", Name = "healing_ward", Level = 3 },
            new SlotSnapshot { Slot = "ability2", Name = "blade_dance", Level = 4 },
            new SlotSnapshot { Slot = "ability3", Name = "omni_slash", Level = 3 },
        ],
        Players = CreatePlayers(),
    };

    public static IReadOnlyList<BuildSuggestionItem> CreateBuildSuggestions() =>
    [
        new BuildSuggestionItem { Name = "quelling_blade" },
        new BuildSuggestionItem { Name = "power_treads" },
        new BuildSuggestionItem { Name = "bfury" },
        new BuildSuggestionItem { Name = "manta" },
        new BuildSuggestionItem { Name = "butterfly" },
        new BuildSuggestionItem { Name = "abyssal_blade" },
        new BuildSuggestionItem { Name = "satanic" },
    ];

    public static PlayerOverview CreatePlayerOverview(string steamId)
    {
        var player = CreatePlayers().FirstOrDefault(player => player.SteamId == steamId)
            ?? CreatePlayers()[0];
        ApplyPreviewDetails(player);
        return new PlayerOverview
        {
            SteamId = steamId,
            DisplayName = player.Name,
            RankTier = player.ReportedRankTier,
            RankLabel = player.RankLabel,
            IsPrivate = player.IsProfilePrivate,
            Wins = 2206,
            Losses = 2004,
            WinRatePercent = 52.4,
            RecentMatches =
            [
                new PlayerMatchRow
                {
                    MatchId = "8000000000",
                    HeroName = player.DisplayHeroName,
                    Result = "Win",
                    Kda = "12/3/9",
                    Duration = "31:12",
                    PlayedAt = "Today",
                },
            ],
        };
    }

    private sealed record PreviewDetail(
        int RankTier,
        string Matches,
        string Encounter,
        string HeroMatch,
        string[] Items,
        bool IsPrivate = false,
        string Kda = "—",
        bool IsEstimated = false);

    private static readonly Dictionary<string, PreviewDetail> PreviewDetails = new()
    {
        ["Mango Tester"] = new(75, "4,210 (52.4%)", "That's you.", "Latest on Juggernaut: Win · 31:12",
            ["bfury", "power_treads", "manta", "black_king_bar", "butterfly", "abyssal_blade"], Kda: "12/3/9"),
        ["Radiant Mid"] = new(63, "2,964 (50.1%)", "3/1", "Latest on Storm Spirit: Loss · 27:40",
            ["bloodstone", "kaya_and_sange", "blink", "travel_boots", "shivas_guard", "octarine_core"], Kda: "7/8/11"),
        ["Radiant Offlane"] = new(54, "1,508 (49.3%)", "0/0", "Latest on Tidehunter: Win · 35:02",
            ["blink", "pipe", "crimson_guard", "arcane_boots", "heart"], Kda: "4/5/23"),
        ["Radiant Support"] = new(41, "902 (47.8%)", "1/0", "Latest on Crystal Maiden: Win · 29:55",
            ["glimmer_cape", "force_staff", "tranquil_boots", "aether_lens"], Kda: "3/7/28"),
        ["Radiant Hard Support"] = new(35, "611 (51.0%)", "0/0", "Latest on Lich: Loss · 38:18",
            ["spirit_vessel", "glimmer_cape", "arcane_boots", "ultimate_scepter"], Kda: "2/10/17"),
        ["Dire Carry"] = new(71, "3,377 (54.9%)", "0/2", "Latest on Phantom Assassin: Win · 26:44",
            ["desolator", "phase_boots", "black_king_bar", "satanic", "abyssal_blade", "butterfly"], Kda: "18/2/7"),
        ["Dire Mid"] = new(62, "2,120 (48.6%)", "2/2", "Latest on Invoker: Win · 33:09",
            ["hand_of_midas", "witch_blade", "travel_boots", "ultimate_scepter", "sheepstick"], Kda: "9/4/16"),
        ["Dire Offlane"] = new(44, "1,244 (50.8%)", "0/0", "Latest on Axe: Loss · 30:21",
            ["vanguard", "blink", "blade_mail", "boots", "heart"], Kda: "5/9/12", IsEstimated: true),
        ["Dire Support"] = new(22, "—", "Encounter history unavailable.", "Hero match history unavailable.",
            [], IsPrivate: true),
        ["Dire Hard Support"] = new(53, "781 (46.2%)", "0/0", "Latest on Warlock: Loss · 41:07",
            ["glimmer_cape", "ultimate_scepter", "aether_lens", "arcane_boots"], Kda: "1/8/25"),
    };

    /// <summary>Fills the OpenDota/Steam-derived fields so the preview renders offline.</summary>
    public static void ApplyPreviewDetails(MatchPlayer player)
    {
        if (!PreviewDetails.TryGetValue(player.Name, out var detail))
        {
            return;
        }

        player.RankIconUrl = AssetUrls.RankIcon(detail.RankTier);
        player.RankStarUrl = AssetUrls.RankStar(detail.RankTier);
        player.RankLabel = detail.IsEstimated
            ? $"Estimated: {OpenDotaService.FormatRankTier(detail.RankTier)} (from 12 recent solo ranked lobbies)"
            : OpenDotaService.FormatRankTier(detail.RankTier);
        player.RankIsEstimated = detail.IsEstimated;
        player.ReportedRankTier = detail.IsEstimated ? null : detail.RankTier;
        player.MatchesLabel = detail.Matches;
        player.IsProfilePrivate = detail.IsPrivate;
        player.History = new PlayerMatchHistory
        {
            EncounterLabel = detail.Encounter,
            Kda = detail.Kda,
            Position = detail.IsPrivate ? null : player.Name switch
            {
                "Radiant Mid" or "Dire Mid" => 2,
                "Radiant Offlane" or "Dire Offlane" => 3,
                "Radiant Support" or "Dire Support" => 4,
                "Radiant Hard Support" or "Dire Hard Support" => 5,
                _ => 1,
            },
            Result = detail.IsPrivate ? string.Empty : detail.HeroMatch.Contains("Loss") ? "Loss" : "Win",
            PlayedAt = detail.IsPrivate ? null : DateTimeOffset.UtcNow.AddHours(-5),
            HeroMatchLabel = detail.HeroMatch,
            HeroMatchUrl = detail.IsPrivate ? string.Empty : "https://www.opendota.com/matches/8000000000",
            RecentMatchesLabel = detail.IsPrivate ? "Recent matches unavailable." : "Last 5 completed matches, newest first",
            RecentMatches = detail.IsPrivate ? [] : CreatePlayers().Take(5).Select((hero, index) => new RecentMatchSummary
            {
                HeroName = hero.DisplayHeroName,
                HeroImageUrl = AssetUrls.HeroIcon(hero.HeroIconKey),
                Result = index % 3 == 1 ? "Loss" : "Win",
            }).ToList(),
            Items = detail.Items
                .Select(static (item, index) => new MatchBuildItem
                {
                    Name = item.Replace('_', ' '),
                    ImageUrl = AssetUrls.ItemIcon(item),
                    Slot = "Item",
                    PurchaseTimeSeconds = 175 + index * 227,
                })
                .ToList(),
        };
    }

    public static IReadOnlyList<MatchPlayer> CreatePlayers() =>
    [
        new MatchPlayer
        {
            Name = "Mango Tester",
            SteamId = "76561198000000001",
            TeamName = "radiant",
            HeroName = "npc_dota_hero_juggernaut",
            HeroLevel = 18,
            Kills = 8,
            Deaths = 2,
            Assists = 6,
            LastHits = 214,
            Denies = 12,
            Gpm = 612,
            Xpm = 704,
            Gold = 2840,
        },
        new MatchPlayer
        {
            Name = "Radiant Mid",
            TeamName = "radiant",
            HeroName = "npc_dota_hero_storm_spirit",
            HeroLevel = 17,
            Kills = 6,
            Deaths = 4,
            Assists = 9,
            LastHits = 168,
            Denies = 8,
            Gpm = 548,
            Xpm = 692,
            Gold = 1960,
        },
        new MatchPlayer
        {
            Name = "Radiant Offlane",
            TeamName = "radiant",
            HeroName = "npc_dota_hero_tidehunter",
            HeroLevel = 16,
            Kills = 2,
            Deaths = 5,
            Assists = 14,
            LastHits = 92,
            Denies = 6,
            Gpm = 412,
            Xpm = 488,
            Gold = 1240,
        },
        new MatchPlayer
        {
            Name = "Radiant Support",
            TeamName = "radiant",
            HeroName = "npc_dota_hero_crystal_maiden",
            HeroLevel = 14,
            Kills = 1,
            Deaths = 7,
            Assists = 18,
            LastHits = 28,
            Denies = 2,
            Gpm = 286,
            Xpm = 392,
            Gold = 620,
        },
        new MatchPlayer
        {
            Name = "Radiant Hard Support",
            TeamName = "radiant",
            HeroName = "npc_dota_hero_lich",
            HeroLevel = 13,
            Kills = 0,
            Deaths = 6,
            Assists = 16,
            LastHits = 18,
            Denies = 1,
            Gpm = 248,
            Xpm = 356,
            Gold = 480,
        },
        new MatchPlayer
        {
            Name = "Dire Carry",
            TeamName = "dire",
            HeroName = "npc_dota_hero_phantom_assassin",
            HeroLevel = 17,
            Kills = 7,
            Deaths = 3,
            Assists = 4,
            LastHits = 198,
            Denies = 10,
            Gpm = 584,
            Xpm = 668,
            Gold = 2520,
        },
        new MatchPlayer
        {
            Name = "Dire Mid",
            TeamName = "dire",
            HeroName = "npc_dota_hero_invoker",
            HeroLevel = 16,
            Kills = 5,
            Deaths = 4,
            Assists = 7,
            LastHits = 142,
            Denies = 14,
            Gpm = 512,
            Xpm = 644,
            Gold = 1780,
        },
        new MatchPlayer
        {
            Name = "Dire Offlane",
            TeamName = "dire",
            HeroName = "npc_dota_hero_axe",
            HeroLevel = 15,
            Kills = 3,
            Deaths = 6,
            Assists = 11,
            LastHits = 84,
            Denies = 4,
            Gpm = 396,
            Xpm = 452,
            Gold = 980,
        },
        new MatchPlayer
        {
            Name = "Dire Support",
            TeamName = "dire",
            HeroName = "npc_dota_hero_shadow_shaman",
            HeroLevel = 13,
            Kills = 2,
            Deaths = 8,
            Assists = 12,
            LastHits = 22,
            Denies = 0,
            Gpm = 268,
            Xpm = 368,
            Gold = 540,
        },
        new MatchPlayer
        {
            Name = "Dire Hard Support",
            TeamName = "dire",
            HeroName = "npc_dota_hero_warlock",
            HeroLevel = 12,
            Kills = 1,
            Deaths = 9,
            Assists = 13,
            LastHits = 14,
            Denies = 1,
            Gpm = 224,
            Xpm = 332,
            Gold = 420,
        },
    ];
}
