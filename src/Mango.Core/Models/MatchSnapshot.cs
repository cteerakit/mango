namespace Mango.Models;

public sealed class MatchSnapshot
{
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public bool IsConnected { get; set; }
    public string GameState { get; set; } = string.Empty;
    public int ClockTimeSeconds { get; set; }
    public int GameTimeSeconds { get; set; }
    public bool IsDaytime { get; set; }
    public string MatchId { get; set; } = string.Empty;
    public string ServerSteamId { get; set; } = string.Empty;
    public int RadiantScore { get; set; }
    public int DireScore { get; set; }
    public int NetWorthLead { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public string SteamId { get; set; } = string.Empty;
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int LastHits { get; set; }
    public int Denies { get; set; }
    public int Gpm { get; set; }
    public int Xpm { get; set; }
    public int Gold { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string HeroName { get; set; } = string.Empty;
    public int HeroLevel { get; set; }
    public int Health { get; set; }
    public int MaxHealth { get; set; }
    public int Mana { get; set; }
    public int MaxMana { get; set; }
    public bool HeroAlive { get; set; }
    public IReadOnlyList<SlotSnapshot> Items { get; set; } = Array.Empty<SlotSnapshot>();
    public IReadOnlyList<SlotSnapshot> Abilities { get; set; } = Array.Empty<SlotSnapshot>();
    public IReadOnlyList<MatchPlayer> Players { get; set; } = Array.Empty<MatchPlayer>();
    public DraftSnapshot Draft { get; set; } = new();

    public string FormattedClock => Gsi.GsiNormalizer.FormatClock(ClockTimeSeconds);
    public string Kda => $"{Kills}/{Deaths}/{Assists}";
    public double HealthPercent => MaxHealth > 0 ? (double)Health / MaxHealth : 0;
    public double ManaPercent => MaxMana > 0 ? (double)Mana / MaxMana : 0;
    public bool IsInDraft =>
        GameState.Contains("HERO_SELECTION", StringComparison.OrdinalIgnoreCase) ||
        GameState.Contains("STRATEGY", StringComparison.OrdinalIgnoreCase);
    public bool IsInGame =>
        GameState.Contains("GAME_IN_PROGRESS", StringComparison.OrdinalIgnoreCase);
}

public sealed class SlotSnapshot
{
    public string Slot { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
    public double Cooldown { get; set; }
    public bool CanCast { get; set; }
}

public sealed class DraftSnapshot
{
    public bool IsActive { get; set; }
    public int ActiveTeam { get; set; }
    public bool IsPickPhase { get; set; }
    public int TimeRemaining { get; set; }
    public bool PlayerIsRadiant { get; set; }
    public IReadOnlyList<int> RadiantHeroIds { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> DireHeroIds { get; set; } = Array.Empty<int>();

    public IReadOnlyList<int> EnemyHeroIds =>
        PlayerIsRadiant ? DireHeroIds : RadiantHeroIds;

    public IReadOnlyList<int> AllyHeroIds =>
        PlayerIsRadiant ? RadiantHeroIds : DireHeroIds;
}
