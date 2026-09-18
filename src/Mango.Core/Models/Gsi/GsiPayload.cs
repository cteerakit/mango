using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mango.Models.Gsi;

public sealed class GsiPayload
{
    [JsonPropertyName("provider")]
    public GsiProvider? Provider { get; set; }

    [JsonPropertyName("map")]
    public GsiMap? Map { get; set; }

    /// <summary>
    /// Playing: a single player object. Spectating: team2/team3 maps of player0–player9.
    /// </summary>
    [JsonPropertyName("player")]
    [JsonConverter(typeof(GsiPlayerNodeJsonConverter))]
    public GsiPlayerNode? Player { get; set; }

    [JsonPropertyName("allplayers")]
    [JsonConverter(typeof(GsiAllPlayersJsonConverter))]
    public Dictionary<string, GsiPlayer>? AllPlayers { get; set; }

    /// <summary>
    /// Playing: a single hero object. Spectating: team2/team3 maps of player0–player9.
    /// </summary>
    [JsonPropertyName("hero")]
    [JsonConverter(typeof(GsiHeroNodeJsonConverter))]
    public GsiHeroNode? Hero { get; set; }

    [JsonPropertyName("items")]
    [JsonConverter(typeof(GsiLocalDictionaryJsonConverter<GsiItemSlot>))]
    public Dictionary<string, GsiItemSlot>? Items { get; set; }

    [JsonPropertyName("abilities")]
    [JsonConverter(typeof(GsiLocalDictionaryJsonConverter<GsiAbilitySlot>))]
    public Dictionary<string, GsiAbilitySlot>? Abilities { get; set; }

    [JsonPropertyName("draft")]
    public GsiDraft? Draft { get; set; }

    [JsonPropertyName("auth")]
    public GsiAuth? Auth { get; set; }
}

/// <summary>
/// GSI <c>player</c> is either the local player (playing) or team→slot maps (spectating).
/// </summary>
public sealed class GsiPlayerNode
{
    public GsiPlayer? Local { get; set; }

    /// <summary>Keys like <c>team2</c>/<c>team3</c>, then <c>player0</c>–<c>player9</c>.</summary>
    public Dictionary<string, Dictionary<string, GsiPlayer>>? Teams { get; set; }

    public bool IsSpectating => Teams is { Count: > 0 };
}

/// <summary>
/// GSI <c>hero</c> is either the local hero (playing) or team→slot maps (spectating).
/// </summary>
public sealed class GsiHeroNode
{
    public GsiHero? Local { get; set; }

    /// <summary>Keys like <c>team2</c>/<c>team3</c>, then <c>player0</c>–<c>player9</c>.</summary>
    public Dictionary<string, Dictionary<string, GsiHero>>? Teams { get; set; }

    public bool IsSpectating => Teams is { Count: > 0 };
}

public sealed class GsiProvider
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("appid")]
    public int AppId { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

public sealed class GsiMap
{
    [JsonPropertyName("game_state")]
    public string? GameState { get; set; }

    [JsonPropertyName("clock_time")]
    public int ClockTime { get; set; }

    [JsonPropertyName("game_time")]
    public int GameTime { get; set; }

    [JsonPropertyName("daytime")]
    public bool Daytime { get; set; }

    [JsonPropertyName("matchid")]
    [JsonConverter(typeof(GsiFlexibleStringJsonConverter))]
    public string? MatchId { get; set; }

    [JsonPropertyName("server_steam_id")]
    [JsonConverter(typeof(GsiFlexibleStringJsonConverter))]
    public string? ServerSteamId { get; set; }

    [JsonPropertyName("radiant_score")]
    public int RadiantScore { get; set; }

    [JsonPropertyName("dire_score")]
    public int DireScore { get; set; }

    [JsonPropertyName("radiant_gold_adv")]
    public int[]? RadiantGoldAdv { get; set; }

    [JsonPropertyName("win_team")]
    public string? WinTeam { get; set; }
}

public sealed class GsiPlayer
{
    [JsonPropertyName("steamid")]
    public string? SteamId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("kills")]
    public int Kills { get; set; }

    [JsonPropertyName("deaths")]
    public int Deaths { get; set; }

    [JsonPropertyName("assists")]
    public int Assists { get; set; }

    [JsonPropertyName("last_hits")]
    public int LastHits { get; set; }

    [JsonPropertyName("denies")]
    public int Denies { get; set; }

    [JsonPropertyName("gpm")]
    public int Gpm { get; set; }

    [JsonPropertyName("xpm")]
    public int Xpm { get; set; }

    [JsonPropertyName("gold")]
    public int Gold { get; set; }

    [JsonPropertyName("gold_reliable")]
    public int GoldReliable { get; set; }

    [JsonPropertyName("gold_unreliable")]
    public int GoldUnreliable { get; set; }

    [JsonPropertyName("team_name")]
    public string? TeamName { get; set; }

    [JsonPropertyName("team")]
    [JsonConverter(typeof(GsiTeamJsonConverter))]
    public int Team { get; set; }

    [JsonPropertyName("team_slot")]
    public int TeamSlot { get; set; }

    /// <summary>0–4 radiant / 128–132 dire in many payloads; sometimes 0–9 lobby index.</summary>
    [JsonPropertyName("player_slot")]
    public int? PlayerSlot { get; set; }

    [JsonPropertyName("hero")]
    public GsiHero? Hero { get; set; }
}

internal sealed class GsiTeamJsonConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var teamNumber))
        {
            return teamNumber;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var team = reader.GetString();
            if (int.TryParse(team, out teamNumber))
            {
                return teamNumber;
            }

            if (team?.Contains("radiant", StringComparison.OrdinalIgnoreCase) == true)
            {
                return 2;
            }

            if (team?.Contains("dire", StringComparison.OrdinalIgnoreCase) == true)
            {
                return 3;
            }
        }

        return 0;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

public sealed class GsiFlexibleStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when reader.TryGetInt64(out var number) => number.ToString(),
            JsonTokenType.Number when reader.TryGetUInt64(out var unsigned) => unsigned.ToString(),
            JsonTokenType.Null => null,
            _ => null,
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value);
    }
}

public sealed class GsiHero
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("health")]
    public int Health { get; set; }

    [JsonPropertyName("max_health")]
    public int MaxHealth { get; set; }

    [JsonPropertyName("mana")]
    public int Mana { get; set; }

    [JsonPropertyName("max_mana")]
    public int MaxMana { get; set; }

    [JsonPropertyName("alive")]
    public bool Alive { get; set; }
}

public sealed class GsiItemSlot
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("cooldown")]
    public double Cooldown { get; set; }

    [JsonPropertyName("can_cast")]
    public bool CanCast { get; set; }
}

public sealed class GsiAbilitySlot
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("cooldown")]
    public double Cooldown { get; set; }

    [JsonPropertyName("can_cast")]
    public bool CanCast { get; set; }
}

public sealed class GsiDraft
{
    [JsonPropertyName("activeteam")]
    public int ActiveTeam { get; set; }

    [JsonPropertyName("pick")]
    public bool Pick { get; set; }

    [JsonPropertyName("activeteam_time_remaining")]
    public int ActiveTeamTimeRemaining { get; set; }

    [JsonPropertyName("radiant_bonus_time")]
    public int RadiantBonusTime { get; set; }

    [JsonPropertyName("dire_bonus_time")]
    public int DireBonusTime { get; set; }

    [JsonPropertyName("team2")]
    public GsiDraftTeam? Team2 { get; set; }

    [JsonPropertyName("team3")]
    public GsiDraftTeam? Team3 { get; set; }
}

public sealed class GsiDraftTeam
{
    [JsonPropertyName("home_team")]
    public bool HomeTeam { get; set; }

    [JsonPropertyName("pick0_id")]
    public int Pick0Id { get; set; }

    [JsonPropertyName("pick1_id")]
    public int Pick1Id { get; set; }

    [JsonPropertyName("pick2_id")]
    public int Pick2Id { get; set; }

    [JsonPropertyName("pick3_id")]
    public int Pick3Id { get; set; }

    [JsonPropertyName("pick4_id")]
    public int Pick4Id { get; set; }

    [JsonPropertyName("ban0_id")]
    public int Ban0Id { get; set; }

    [JsonPropertyName("ban1_id")]
    public int Ban1Id { get; set; }

    [JsonPropertyName("ban2_id")]
    public int Ban2Id { get; set; }

    [JsonPropertyName("ban3_id")]
    public int Ban3Id { get; set; }

    [JsonPropertyName("ban4_id")]
    public int Ban4Id { get; set; }

    [JsonPropertyName("ban5_id")]
    public int Ban5Id { get; set; }

    [JsonPropertyName("ban6_id")]
    public int Ban6Id { get; set; }
}

public sealed class GsiAuth
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

internal sealed class GsiPlayerNodeJsonConverter : JsonConverter<GsiPlayerNode?>
{
    public override GsiPlayerNode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (GsiSpectatingJson.IsSpectatingTeamMap(root))
        {
            return new GsiPlayerNode
            {
                Teams = GsiSpectatingJson.DeserializeTeamMap<GsiPlayer>(root, options),
            };
        }

        return new GsiPlayerNode
        {
            Local = root.Deserialize<GsiPlayer>(options),
        };
    }

    public override void Write(Utf8JsonWriter writer, GsiPlayerNode? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.IsSpectating)
        {
            JsonSerializer.Serialize(writer, value.Teams, options);
            return;
        }

        JsonSerializer.Serialize(writer, value.Local, options);
    }
}

internal sealed class GsiHeroNodeJsonConverter : JsonConverter<GsiHeroNode?>
{
    public override GsiHeroNode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (GsiSpectatingJson.IsSpectatingTeamMap(root))
        {
            return new GsiHeroNode
            {
                Teams = GsiSpectatingJson.DeserializeTeamMap<GsiHero>(root, options),
            };
        }

        return new GsiHeroNode
        {
            Local = root.Deserialize<GsiHero>(options),
        };
    }

    public override void Write(Utf8JsonWriter writer, GsiHeroNode? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.IsSpectating)
        {
            JsonSerializer.Serialize(writer, value.Teams, options);
            return;
        }

        JsonSerializer.Serialize(writer, value.Local, options);
    }
}

/// <summary>
/// Playing clients send flat slot dictionaries; spectators send team→player→slots. Only the flat shape is kept.
/// </summary>
internal sealed class GsiLocalDictionaryJsonConverter<TValue> : JsonConverter<Dictionary<string, TValue>?>
{
    public override Dictionary<string, TValue>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || GsiSpectatingJson.IsSpectatingTeamMap(root))
        {
            return null;
        }

        var result = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            var value = property.Value.Deserialize<TValue>(options);
            if (value is not null)
            {
                result[property.Name] = value;
            }
        }

        return result.Count > 0 ? result : null;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, TValue>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        foreach (var pair in value)
        {
            writer.WritePropertyName(pair.Key);
            JsonSerializer.Serialize(writer, pair.Value, options);
        }

        writer.WriteEndObject();
    }
}

internal sealed class GsiAllPlayersJsonConverter : JsonConverter<Dictionary<string, GsiPlayer>?>
{
    public override Dictionary<string, GsiPlayer>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, GsiPlayer>(StringComparer.OrdinalIgnoreCase);

        if (GsiSpectatingJson.IsSpectatingTeamMap(root))
        {
            var teams = GsiSpectatingJson.DeserializeTeamMap<GsiPlayer>(root, options);
            if (teams is null)
            {
                return null;
            }

            foreach (var (teamKey, players) in teams)
            {
                foreach (var (playerKey, player) in players)
                {
                    result[$"{teamKey}.{playerKey}"] = player;
                }
            }

            return result.Count > 0 ? result : null;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var player = property.Value.Deserialize<GsiPlayer>(options);
            if (player is null || !LooksLikePlayer(player))
            {
                continue;
            }

            result[property.Name] = player;
        }

        return result.Count > 0 ? result : null;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, GsiPlayer>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        JsonSerializer.Serialize(writer, value, options);
    }

    private static bool LooksLikePlayer(GsiPlayer player) =>
        !string.IsNullOrWhiteSpace(player.SteamId)
        || !string.IsNullOrWhiteSpace(player.Name)
        || !string.IsNullOrWhiteSpace(player.TeamName)
        || player.Team is 2 or 3
        || player.PlayerSlot is not null;
}

internal static class GsiSpectatingJson
{
    public static bool IsSpectatingTeamMap(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            // Spectating uses team2/team3 buckets — not local fields like team_name / team_slot.
            if (IsTeamBucketKey(property.Name) && property.Value.ValueKind == JsonValueKind.Object)
            {
                return true;
            }
        }

        return false;
    }

    public static Dictionary<string, Dictionary<string, T>>? DeserializeTeamMap<T>(
        JsonElement root,
        JsonSerializerOptions options)
    {
        var teams = new Dictionary<string, Dictionary<string, T>>(StringComparer.OrdinalIgnoreCase);
        foreach (var teamProperty in root.EnumerateObject())
        {
            if (!IsTeamBucketKey(teamProperty.Name)
                || teamProperty.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var players = teamProperty.Value.Deserialize<Dictionary<string, T>>(options);
            if (players is { Count: > 0 })
            {
                teams[teamProperty.Name] = players;
            }
        }

        return teams.Count > 0 ? teams : null;
    }

    private static bool IsTeamBucketKey(string name)
    {
        if (!name.StartsWith("team", StringComparison.OrdinalIgnoreCase) || name.Length <= 4)
        {
            return false;
        }

        for (var i = 4; i < name.Length; i++)
        {
            if (!char.IsDigit(name[i]))
            {
                return false;
            }
        }

        return true;
    }
}
