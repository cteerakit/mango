using Mango.Models;
using Xunit;

namespace Mango.Tests;

public sealed class SteamLocalRosterMapperTests
{
    [Fact]
    public void FindServer_UsesExactMatchAndLatestConnection()
    {
        const string log = """
            Received Steam datagram ticket for server steamid:901 vport 0. match_id=123 mm=1
            Received Steam datagram ticket for server steamid:902 vport 0. match_id=1234 mm=1
            Received Steam datagram ticket for server steamid:903 vport 0. match_id=123 mm=1
            """;
        Assert.Equal("903", SteamLocalRosterMapper.FindServer(log, "123"));
        Assert.Null(SteamLocalRosterMapper.FindServer(log, "456"));
        Assert.Null(SteamLocalRosterMapper.FindServer(log, "0"));
    }

    [Fact]
    public void ReadPlayers_ReturnsCurrentServerMembersWithoutInventingTeams()
    {
        var players = SteamLocalRosterMapper.ReadPlayers(Roster, "901", "1");
        Assert.Equal(2, players.Count);
        Assert.Equal("76561197960265730", players[1].SteamId);
        Assert.Equal("Other player", players[1].Name);
        Assert.All(players, player => Assert.Empty(player.TeamName));
        Assert.All(players, player => Assert.Empty(player.HeroName));
    }

    [Theory]
    [InlineData("902", "1")]
    [InlineData("901", "3")]
    [InlineData("901", "0")]
    public void ReadPlayers_RejectsDifferentServerOrMissingLocalPlayer(string server, string localPlayer) =>
        Assert.Empty(SteamLocalRosterMapper.ReadPlayers(Roster, server, localPlayer));

    private const string Roster = """
        {"serverSteamId":"901","players":[
          {"steamId":"76561197960265729","name":"Local player"},
          {"steamId":"76561197960265730","name":"Other player"}
        ]}
        """;
}
