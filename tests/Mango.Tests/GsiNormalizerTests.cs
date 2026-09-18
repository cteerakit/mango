using System.Text.Json;
using Mango.Models.Gsi;
using Mango.Services;
using Xunit;

namespace Mango.Tests;

public class GsiNormalizerTests
{
    [Fact]
    public void Normalize_FormatsClockAndKda()
    {
        var payload = new GsiPayload
        {
            Map = new GsiMap { ClockTime = 754, GameState = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", MatchId = "8345123456" },
            Player = new GsiPlayerNode
            {
                Local = new GsiPlayer
                {
                    Name = "Invoker",
                    SteamId = "76561198000000000",
                    Kills = 3,
                    Deaths = 1,
                    Assists = 5,
                    Gpm = 420,
                    Xpm = 510,
                    Gold = 1250,
                },
            },
            Hero = new GsiHeroNode
            {
                Local = new GsiHero
                {
                    Name = "npc_dota_hero_invoker",
                    Level = 12,
                    Health = 800,
                    MaxHealth = 1000,
                    Mana = 300,
                    MaxMana = 400,
                },
            },
        };

        var snapshot = GsiNormalizer.Normalize(payload);

        Assert.Equal("12:34", snapshot.FormattedClock);
        Assert.Equal("3/1/5", snapshot.Kda);
        Assert.Equal("Invoker", snapshot.PlayerName);
        Assert.Equal("76561198000000000", snapshot.SteamId);
        Assert.Equal("8345123456", snapshot.MatchId);
        Assert.Equal("invoker", snapshot.HeroName);
        Assert.Equal(0.8, snapshot.HealthPercent, 3);
        Assert.Single(snapshot.Players);
        Assert.Equal("invoker", snapshot.Players[0].DisplayHeroName);
    }

    [Fact]
    public void Normalize_IgnoresZeroMatchId()
    {
        var payload = new GsiPayload
        {
            Map = new GsiMap { MatchId = "0" },
        };

        var snapshot = GsiNormalizer.Normalize(payload);
        Assert.Equal(string.Empty, snapshot.MatchId);
    }

    [Fact]
    public void Normalize_MapsAllPlayersRoster()
    {
        var payload = new GsiPayload
        {
            AllPlayers = new Dictionary<string, GsiPlayer>
            {
                ["player1"] = new GsiPlayer
                {
                    Name = "Dire Mid",
                    Team = 3,
                    TeamSlot = 1,
                    Kills = 5,
                    Deaths = 2,
                    Assists = 4,
                    LastHits = 120,
                    Denies = 8,
                    Gpm = 500,
                    Xpm = 600,
                    Gold = 1800,
                    Hero = new GsiHero { Name = "npc_dota_hero_invoker", Level = 16 },
                },
                ["player0"] = new GsiPlayer
                {
                    Name = "Radiant Carry",
                    TeamName = "radiant",
                    TeamSlot = 0,
                    Kills = 8,
                    Deaths = 1,
                    Assists = 3,
                    LastHits = 200,
                    Denies = 12,
                    Gpm = 620,
                    Xpm = 710,
                    Gold = 2400,
                    Hero = new GsiHero { Name = "npc_dota_hero_juggernaut", Level = 18 },
                },
            },
        };

        var snapshot = GsiNormalizer.Normalize(payload);

        Assert.Equal(2, snapshot.Players.Count);
        Assert.Equal("Radiant Carry", snapshot.Players[0].Name);
        Assert.Equal("radiant", snapshot.Players[0].TeamName);
        Assert.Equal("juggernaut", snapshot.Players[0].DisplayHeroName);
        Assert.Equal("Dire Mid", snapshot.Players[1].Name);
        Assert.Equal("dire", snapshot.Players[1].TeamName);
        Assert.Equal("5/2/4", snapshot.Players[1].Kda);
    }

    [Fact]
    public void Normalize_MapsSpectatedTeamPlayersFromJson()
    {
        const string json = """
            {
              "map": { "matchid": "9001", "game_state": "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", "clock_time": 120 },
              "player": {
                "team2": {
                  "player0": {
                    "steamid": "76561198000000001",
                    "name": "Radiant Carry",
                    "team_slot": 0,
                    "kills": 8,
                    "deaths": 1,
                    "assists": 3,
                    "last_hits": 200,
                    "denies": 12,
                    "gpm": 620,
                    "xpm": 710,
                    "gold": 2400
                  }
                },
                "team3": {
                  "player5": {
                    "steamid": "76561198000000002",
                    "name": "Dire Mid",
                    "team_slot": 1,
                    "kills": 5,
                    "deaths": 2,
                    "assists": 4,
                    "last_hits": 120,
                    "denies": 8,
                    "gpm": 500,
                    "xpm": 600,
                    "gold": 1800
                  }
                }
              },
              "hero": {
                "team2": {
                  "player0": { "name": "npc_dota_hero_juggernaut", "level": 18 }
                },
                "team3": {
                  "player5": { "name": "npc_dota_hero_invoker", "level": 16 }
                }
              },
              "items": {
                "team2": {
                  "player0": {
                    "slot0": { "name": "item_blink" }
                  }
                }
              },
              "abilities": {
                "team2": {
                  "player0": {
                    "ability0": { "name": "juggernaut_blade_fury", "level": 4 }
                  }
                }
              }
            }
            """;

        var payload = JsonSerializer.Deserialize<GsiPayload>(json);
        Assert.NotNull(payload);
        Assert.True(payload.Player?.IsSpectating);
        Assert.Null(payload.Items);
        Assert.Null(payload.Abilities);

        var snapshot = GsiNormalizer.Normalize(payload);

        Assert.Equal("9001", snapshot.MatchId);
        Assert.Equal(2, snapshot.Players.Count);
        Assert.Equal("Radiant Carry", snapshot.Players[0].Name);
        Assert.Equal("radiant", snapshot.Players[0].TeamName);
        Assert.Equal("76561198000000001", snapshot.Players[0].SteamId);
        Assert.Equal("juggernaut", snapshot.Players[0].DisplayHeroName);
        Assert.Equal(18, snapshot.Players[0].HeroLevel);
        Assert.Equal("Dire Mid", snapshot.Players[1].Name);
        Assert.Equal("dire", snapshot.Players[1].TeamName);
        Assert.Equal("invoker", snapshot.Players[1].DisplayHeroName);
        Assert.Equal("5/2/4", snapshot.Players[1].Kda);
        Assert.Empty(snapshot.Items);
        Assert.Empty(snapshot.Abilities);
    }

    [Fact]
    public void Normalize_MapsLocalPlayerDuringHeroSelectionFromJson()
    {
        // Playing-mode GSI includes team_name/team/team_slot on the flat player object.
        // Those must not be mistaken for spectator team2/team3 maps (common during pick).
        const string json = """
            {
              "map": {
                "matchid": "8345123456",
                "server_steam_id": "90123456789012345",
                "game_state": "DOTA_GAMERULES_STATE_HERO_SELECTION",
                "clock_time": -45
              },
              "player": {
                "steamid": "76561198000000001",
                "name": "Mango Tester",
                "activity": "playing",
                "team_name": "radiant",
                "team": 2,
                "team_slot": 0,
                "kills": 0,
                "deaths": 0,
                "assists": 0
              },
              "hero": {
                "id": 0,
                "name": ""
              }
            }
            """;

        var payload = JsonSerializer.Deserialize<GsiPayload>(json);
        Assert.NotNull(payload);
        Assert.False(payload.Player?.IsSpectating);
        Assert.NotNull(payload.Player?.Local);
        Assert.Equal("radiant", payload.Player!.Local!.TeamName);

        var snapshot = GsiNormalizer.Normalize(payload);

        Assert.Equal("DOTA_GAMERULES_STATE_HERO_SELECTION", snapshot.GameState);
        Assert.Equal("8345123456", snapshot.MatchId);
        Assert.Equal("90123456789012345", snapshot.ServerSteamId);
        Assert.Single(snapshot.Players);
        Assert.Equal("Mango Tester", snapshot.Players[0].Name);
        Assert.Equal("76561198000000001", snapshot.Players[0].SteamId);
        Assert.Equal("radiant", snapshot.Players[0].TeamName);
    }

    [Fact]
    public void Normalize_InfersTeamsFromAllPlayersKeysWhenTeamNameMissing()
    {
        const string json = """
            {
              "map": { "matchid": "9002", "game_state": "DOTA_GAMERULES_STATE_HERO_SELECTION" },
              "player": {
                "steamid": "76561198000000001",
                "name": "Me",
                "team_name": "radiant",
                "team": 2
              },
              "allplayers": {
                "player0": { "steamid": "76561198000000001", "name": "Me" },
                "player1": { "steamid": "76561198000000002", "name": "Ally" },
                "player5": { "steamid": "76561198000000006", "name": "Enemy" }
              }
            }
            """;

        var payload = JsonSerializer.Deserialize<GsiPayload>(json);
        Assert.NotNull(payload);
        Assert.Equal(3, payload.AllPlayers?.Count);

        var snapshot = GsiNormalizer.Normalize(payload);

        Assert.Equal(3, snapshot.Players.Count);
        Assert.Contains(snapshot.Players, p => p.Name == "Me" && p.TeamName == "radiant");
        Assert.Contains(snapshot.Players, p => p.Name == "Ally" && p.TeamName == "radiant");
        Assert.Contains(snapshot.Players, p => p.Name == "Enemy" && p.TeamName == "dire");
    }

    [Fact]
    public void Normalize_PrefersSpectatingTeamsOverSparseAllPlayers()
    {
        const string json = """
            {
              "map": { "matchid": "9003", "game_state": "DOTA_GAMERULES_STATE_HERO_SELECTION" },
              "allplayers": {
                "player0": { "steamid": "76561198000000001", "name": "OnlyMe", "team_name": "radiant" }
              },
              "player": {
                "team2": {
                  "player0": { "steamid": "76561198000000001", "name": "Radiant A", "team_slot": 0 },
                  "player1": { "steamid": "76561198000000002", "name": "Radiant B", "team_slot": 1 }
                },
                "team3": {
                  "player5": { "steamid": "76561198000000006", "name": "Dire A", "team_slot": 0 }
                }
              }
            }
            """;

        var payload = JsonSerializer.Deserialize<GsiPayload>(json);
        Assert.NotNull(payload);

        var snapshot = GsiNormalizer.Normalize(payload);

        Assert.Equal(3, snapshot.Players.Count);
        Assert.Contains(snapshot.Players, p => p.Name == "Radiant B");
        Assert.Contains(snapshot.Players, p => p.Name == "Dire A");
    }

    [Fact]
    public void ObjectiveTimerRules_BountyRuneSpawnsEveryFourMinutes()
    {
        Assert.Equal(0, ObjectiveTimerRules.NextBountyRune(-30));
        Assert.Equal(240, ObjectiveTimerRules.NextBountyRune(100));
        Assert.Equal(480, ObjectiveTimerRules.NextBountyRune(240));
    }

    [Fact]
    public void ObjectiveTimerRules_PowerRuneSpawnsEveryTwoMinutes()
    {
        Assert.Equal(120, ObjectiveTimerRules.NextPowerRune(0));
        Assert.Equal(240, ObjectiveTimerRules.NextPowerRune(120));
        Assert.Equal(360, ObjectiveTimerRules.NextPowerRune(300));
    }

    [Fact]
    public void ObjectiveTimerRules_LotusHalvesItsIntervalInTurbo()
    {
        Assert.Equal(180, ObjectiveTimerRules.NextLotus(100, turbo: false));
        Assert.Equal(360, ObjectiveTimerRules.NextLotus(180, turbo: false));
        Assert.Equal(90, ObjectiveTimerRules.NextLotus(0, turbo: true));
        Assert.Equal(180, ObjectiveTimerRules.NextLotus(90, turbo: true));
    }
}
