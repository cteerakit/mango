namespace Mango.Views.Shell;

public sealed class PlayerPageNavigationArgs
{
    public bool IsSelf { get; init; }
    public string SteamId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}
