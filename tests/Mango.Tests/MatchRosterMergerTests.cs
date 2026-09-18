using Mango.Models;
using Xunit;

namespace Mango.Tests;

public class MatchRosterMergerTests
{
    [Fact]
    public void Merge_PrefersExternalWhenGsiRosterIsSparse()
    {
        var gsi = new[]
        {
            new MatchPlayer { SteamId = "1", Name = "Me", TeamName = "radiant", HeroName = "npc_dota_hero_pudge" },
        };

        var external = Enumerable.Range(1, 10)
            .Select(i => new MatchPlayer
            {
                SteamId = i.ToString(),
                Name = $"Player {i}",
                TeamName = i <= 5 ? "radiant" : "dire",
                TeamSlot = i <= 5 ? i - 1 : i - 6,
                HeroName = "npc_dota_hero_axe",
            })
            .ToList();

        var merged = MatchRosterMerger.Merge(gsi, external);

        Assert.Equal(10, merged.Count);
        Assert.Equal("Me", merged.Single(player => player.SteamId == "1").Name);
        Assert.Equal("npc_dota_hero_pudge", merged.Single(player => player.SteamId == "1").HeroName);
    }

    [Fact]
    public void Merge_OverlaysGsiPlayerWhenSteamIdFormatsDiffer()
    {
        var gsi = new[]
        {
            new MatchPlayer
            {
                SteamId = "76561197960265729",
                Name = "Me",
                TeamName = "radiant",
                HeroName = "npc_dota_hero_pudge",
            },
        };

        var external = new[]
        {
            new MatchPlayer
            {
                SteamId = "1",
                Name = "Account 1",
                TeamName = "radiant",
                HeroName = "npc_dota_hero_axe",
            },
            new MatchPlayer { SteamId = "2", Name = "Ally", TeamName = "radiant" },
        };

        var merged = MatchRosterMerger.Merge(gsi, external);

        Assert.Equal(2, merged.Count);
        var self = merged.Single(player => SteamIdConverter.ToAccountId(player.SteamId) == 1);
        Assert.Equal("Me", self.Name);
        Assert.Equal("npc_dota_hero_pudge", self.HeroName);
    }

    [Fact]
    public void Merge_KeepsRicherGsiRoster()
    {
        var gsi = Enumerable.Range(1, 10)
            .Select(i => new MatchPlayer
            {
                SteamId = i.ToString(),
                Name = $"Gsi {i}",
                TeamName = i <= 5 ? "radiant" : "dire",
            })
            .ToList();

        var external = new[]
        {
            new MatchPlayer { SteamId = "1", Name = "External", TeamName = "radiant" },
        };

        var merged = MatchRosterMerger.Merge(gsi, external);

        Assert.Equal(10, merged.Count);
        Assert.All(merged, player => Assert.StartsWith("Gsi", player.Name));
    }

    [Fact]
    public void RetainIfRicher_KeepsFetchedLobbyAcrossSparseGsiUpdates()
    {
        var incoming = new[]
        {
            new MatchPlayer { SteamId = "1", Name = "Me", TeamName = "radiant", HeroName = "npc_dota_hero_pudge" },
        };
        var current = Enumerable.Range(1, 10)
            .Select(i => new MatchPlayer
            {
                SteamId = i.ToString(),
                Name = $"Player {i}",
                TeamName = i <= 5 ? "radiant" : "dire",
            })
            .ToList();

        var retained = MatchRosterMerger.RetainIfRicher(incoming, current, "100", "100");

        Assert.Equal(10, retained.Count);
        Assert.Equal("Me", retained.Single(player => player.SteamId == "1").Name);
        Assert.Equal("npc_dota_hero_pudge", retained.Single(player => player.SteamId == "1").HeroName);
    }

    [Fact]
    public void RetainIfRicher_DropsPreviousLobbyWhenMatchIdChanges()
    {
        var incoming = new[]
        {
            new MatchPlayer { SteamId = "99", Name = "Me", TeamName = "radiant" },
        };
        var current = Enumerable.Range(1, 10)
            .Select(i => new MatchPlayer { SteamId = i.ToString(), Name = $"Old {i}", TeamName = "radiant" })
            .ToList();

        var retained = MatchRosterMerger.RetainIfRicher(incoming, current, "200", "100");

        Assert.Single(retained);
        Assert.Equal("99", retained[0].SteamId);
    }
}
