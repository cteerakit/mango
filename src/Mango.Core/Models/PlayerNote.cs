using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mango.Models;

[JsonConverter(typeof(PlayerNoteJsonConverter))]
public sealed class PlayerNote
{
    public string PlayerName { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int LikeCount { get; set; }
    public int DislikeCount { get; set; }
    public bool IsToxic { get; set; }
    public string LastVoteMatchId { get; set; } = string.Empty;
    public string LastVote { get; set; } = string.Empty;
    public DateTimeOffset? UpdatedAt { get; set; }

    public string StorageKey => LookupKey(PlayerId, PlayerName);

    public string DisplayName => string.IsNullOrWhiteSpace(PlayerName) ? "—" : PlayerName;
    public string DisplayId => string.IsNullOrWhiteSpace(PlayerId) ? "—" : PlayerId;
    public string DisplayUpdatedAt =>
        UpdatedAt is { } updated ? updated.ToLocalTime().ToString("g") : "—";
    public bool HasSavedData =>
        !string.IsNullOrWhiteSpace(Content) || LikeCount > 0 || DislikeCount > 0 || IsToxic;

    public bool HasVotedInMatch(string? matchId)
    {
        var id = matchId?.Trim() ?? string.Empty;
        return PlayerNotesMatch.IsUsableMatchId(id)
            && string.Equals(LastVoteMatchId, id, StringComparison.OrdinalIgnoreCase);
    }

    public static string LookupKey(string? playerId, string? playerName)
    {
        var id = playerId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(id))
        {
            return id;
        }

        return playerName?.Trim() ?? string.Empty;
    }

    public void FillIdentityFromKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (LooksLikeSteamId(key))
        {
            if (string.IsNullOrWhiteSpace(PlayerId))
            {
                PlayerId = key.Trim();
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(PlayerName))
        {
            PlayerName = key.Trim();
        }
    }

    public static bool LooksLikeSteamId(string value) =>
        value.Length >= 15 && value.All(char.IsDigit);
}

internal sealed class PlayerNoteJsonConverter : JsonConverter<PlayerNote>
{
    public override PlayerNote Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new PlayerNote { Content = reader.GetString() ?? string.Empty };
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            return new PlayerNote();
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Each note must be a string or an object.");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        return new PlayerNote
        {
            PlayerName = ReadString(root, "playerName", "PlayerName"),
            PlayerId = ReadString(root, "playerId", "PlayerId"),
            Content = ReadString(root, "content", "Content"),
            LikeCount = ReadInt(root, "likeCount", "LikeCount"),
            DislikeCount = ReadInt(root, "dislikeCount", "DislikeCount"),
            IsToxic = root.EnumerateObject().Any(property =>
                property.Name.Equals("isToxic", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.True),
            LastVoteMatchId = ReadString(root, "lastVoteMatchId", "LastVoteMatchId"),
            LastVote = ReadString(root, "lastVote", "LastVote"),
            UpdatedAt = ReadTime(root, "updatedAt", "UpdatedAt"),
        };
    }

    public override void Write(Utf8JsonWriter writer, PlayerNote value, JsonSerializerOptions options)
    {
        string Name(string property) =>
            options.PropertyNamingPolicy?.ConvertName(property) ?? property;

        writer.WriteStartObject();
        writer.WriteString(Name(nameof(PlayerNote.PlayerName)), value.PlayerName);
        writer.WriteString(Name(nameof(PlayerNote.PlayerId)), value.PlayerId);
        writer.WriteString(Name(nameof(PlayerNote.Content)), value.Content);
        writer.WriteNumber(Name(nameof(PlayerNote.LikeCount)), value.LikeCount);
        writer.WriteNumber(Name(nameof(PlayerNote.DislikeCount)), value.DislikeCount);
        writer.WriteBoolean(Name(nameof(PlayerNote.IsToxic)), value.IsToxic);
        if (!string.IsNullOrWhiteSpace(value.LastVoteMatchId))
        {
            writer.WriteString(Name(nameof(PlayerNote.LastVoteMatchId)), value.LastVoteMatchId);
        }

        if (!string.IsNullOrWhiteSpace(value.LastVote))
        {
            writer.WriteString(Name(nameof(PlayerNote.LastVote)), value.LastVote);
        }

        if (value.UpdatedAt is { } updated)
        {
            writer.WriteString(Name(nameof(PlayerNote.UpdatedAt)), updated);
        }

        writer.WriteEndObject();
    }

    private static string ReadString(JsonElement root, params string[] names)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (names.Any(name => name.Equals(property.Name, StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static int ReadInt(JsonElement root, params string[] names)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Any(name => name.Equals(property.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var number))
            {
                return Math.Max(0, number);
            }

            if (property.Value.ValueKind == JsonValueKind.String
                && int.TryParse(property.Value.GetString(), out number))
            {
                return Math.Max(0, number);
            }
        }

        return 0;
    }

    private static DateTimeOffset? ReadTime(JsonElement root, params string[] names)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Any(name => name.Equals(property.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(property.Value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}
