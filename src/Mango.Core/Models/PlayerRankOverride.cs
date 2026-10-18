namespace Mango.Models;

public sealed class PlayerRankOverride
{
    public string PlayerId { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public int RankTier { get; set; }
}
