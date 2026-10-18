namespace Mango.Models;

public sealed class MatchPageState
{
    public MatchSnapshot Snapshot { get; private set; } = new();
    public bool IsPreviousMatch { get; private set; }

    public void Update(MatchSnapshot incoming)
    {
        var isActive = incoming.IsConnected && incoming.IsInMatch;
        var sameMatch = PlayerNotesMatch.IsUsableMatchId(incoming.MatchId)
            && incoming.MatchId == Snapshot.MatchId;
        if (isActive)
        {
            if (!sameMatch || incoming.Players.Count >= Snapshot.Players.Count)
            {
                Snapshot = incoming;
            }

            IsPreviousMatch = false;
            return;
        }

        var hasMatch = PlayerNotesMatch.IsUsableMatchId(Snapshot.MatchId) && Snapshot.Players.Count > 0;
        if (!hasMatch || (incoming.GameState == "DOTA_GAMERULES_STATE_POST_GAME"
            && PlayerNotesMatch.IsUsableMatchId(incoming.MatchId) && incoming.Players.Count > 0
            && (!sameMatch || incoming.Players.Count >= Snapshot.Players.Count)))
        {
            Snapshot = incoming;
        }

        IsPreviousMatch = PlayerNotesMatch.IsUsableMatchId(Snapshot.MatchId) && Snapshot.Players.Count > 0;
    }
}
