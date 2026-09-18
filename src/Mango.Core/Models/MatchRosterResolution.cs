namespace Mango.Models;

public sealed class MatchRosterResolution
{
    public IReadOnlyList<MatchPlayer>? Players { get; init; }
    public string? ServerSteamId { get; init; }
    public string? Source { get; init; }

    public int IdentifiedCount =>
        Players is null ? 0 : MatchRosterMerger.CountIdentified(Players);
}
