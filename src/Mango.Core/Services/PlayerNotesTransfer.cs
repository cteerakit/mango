using System.Text.Json;
using System.Text.Json.Serialization;
using Mango.Models;

namespace Mango.Services;

public static class PlayerNotesTransfer
{
    public const string FormatId = "mango-player-notes";
    public const string LegacyFormatId = "apem-player-notes";
    public const int CurrentVersion = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(IReadOnlyDictionary<string, PlayerNote> notes)
    {
        var document = new PlayerNotesDocument
        {
            Format = FormatId,
            Version = CurrentVersion,
            Notes = Normalize(notes).Values.ToList(),
        };

        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public static bool TryParse(string json, out Dictionary<string, PlayerNote> notes, out string error)
    {
        notes = new Dictionary<string, PlayerNote>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The file is empty.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Player notes must be a JSON object.";
                return false;
            }

            if (document.RootElement.TryGetProperty("notes", out var notesElement))
            {
                if (document.RootElement.TryGetProperty("format", out var formatElement)
                    && formatElement.ValueKind == JsonValueKind.String
                    && !IsSupportedFormat(formatElement.GetString()))
                {
                    error = "That JSON file is not a Mango player notes export.";
                    return false;
                }

                if (!TryReadNotes(notesElement, notes, out error))
                {
                    return false;
                }

                return true;
            }

            if (!TryReadNotes(document.RootElement, notes, out error))
            {
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = "That file is not valid JSON.";
            return false;
        }
    }

    public static PlayerNotesMergeResult Apply(
        Dictionary<string, PlayerNote> target,
        IReadOnlyDictionary<string, PlayerNote> imported,
        bool replace)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (replace)
        {
            target.Clear();
        }

        var added = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var (key, incoming) in Normalize(imported))
        {
            if (!incoming.HasSavedData)
            {
                skipped++;
                continue;
            }

            if (target.TryGetValue(key, out var existing))
            {
                if (NotesEqual(existing, incoming))
                {
                    skipped++;
                    continue;
                }

                target[key] = MergeNote(existing, incoming);
                updated++;
                continue;
            }

            target[key] = incoming;
            added++;
        }

        return new PlayerNotesMergeResult(added, updated, skipped, target.Count);
    }

    public static bool TryGetByPlayerId(
        IReadOnlyDictionary<string, PlayerNote> notes,
        string? playerId,
        out PlayerNote? note)
    {
        ArgumentNullException.ThrowIfNull(notes);
        note = null;

        var id = playerId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        if (notes.TryGetValue(id, out var byKey)
            && (HasPlayerId(byKey, id) || IsLegacyIdKey(byKey, id)))
        {
            note = byKey;
            return true;
        }

        foreach (var candidate in notes.Values)
        {
            if (HasPlayerId(candidate, id))
            {
                note = candidate;
                return true;
            }
        }

        return false;
    }

    public static string FindContentByPlayerId(IReadOnlyDictionary<string, PlayerNote> notes, string? playerId) =>
        TryGetByPlayerId(notes, playerId, out var note) ? note!.Content : string.Empty;

    public static void Upsert(
        Dictionary<string, PlayerNote> notes,
        string playerName,
        string playerId,
        string content)
    {
        var key = PlayerNote.LookupKey(playerId, playerName);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        notes.TryGetValue(key, out var existing);
        var trimmed = content.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)
            && (existing is null || (existing.LikeCount <= 0 && existing.DislikeCount <= 0 && !existing.IsToxic)))
        {
            notes.Remove(key);
            return;
        }

        notes[key] = WithIdentity(existing, playerName, playerId, trimmed);
    }

    public static bool TryToggleToxic(
        Dictionary<string, PlayerNote> notes,
        string playerName,
        string playerId)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return false;
        }

        var id = playerId.Trim();
        TryGetByPlayerId(notes, id, out var existing);
        var note = WithIdentity(existing, playerName, id, existing?.Content ?? string.Empty);
        note.IsToxic = !note.IsToxic;
        if (note.HasSavedData)
        {
            notes[note.StorageKey] = note;
        }
        else
        {
            notes.Remove(note.StorageKey);
        }

        return true;
    }

    public static bool TryVote(
        Dictionary<string, PlayerNote> notes,
        string playerName,
        string playerId,
        string matchId,
        PlayerNoteVote vote)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (string.IsNullOrWhiteSpace(playerId) || !PlayerNotesMatch.IsUsableMatchId(matchId))
        {
            return false;
        }

        var id = playerId.Trim();
        var match = matchId.Trim();
        TryGetByPlayerId(notes, id, out var existing);

        var note = WithIdentity(existing, playerName, id, existing?.Content ?? string.Empty);
        note.UpdatedAt = DateTimeOffset.Now;

        if (note.HasVotedInMatch(match))
        {
            var current = note.LastVote.Trim();
            if (string.Equals(current, VoteToken(vote), StringComparison.OrdinalIgnoreCase))
            {
                DecrementVote(note, vote);
                note.LastVoteMatchId = string.Empty;
                note.LastVote = string.Empty;
            }
            else
            {
                if (string.Equals(current, "like", StringComparison.OrdinalIgnoreCase))
                {
                    note.LikeCount = Math.Max(0, note.LikeCount - 1);
                }
                else if (string.Equals(current, "dislike", StringComparison.OrdinalIgnoreCase))
                {
                    note.DislikeCount = Math.Max(0, note.DislikeCount - 1);
                }

                ApplyVote(note, vote, match);
            }
        }
        else
        {
            ApplyVote(note, vote, match);
        }

        if (!note.HasSavedData)
        {
            notes.Remove(note.StorageKey);
            return true;
        }

        notes[note.StorageKey] = note;
        return true;
    }

    private static void ApplyVote(PlayerNote note, PlayerNoteVote vote, string matchId)
    {
        note.LastVoteMatchId = matchId;
        if (vote == PlayerNoteVote.Like)
        {
            note.LikeCount++;
            note.LastVote = "like";
        }
        else
        {
            note.DislikeCount++;
            note.LastVote = "dislike";
        }
    }

    private static void DecrementVote(PlayerNote note, PlayerNoteVote vote)
    {
        if (vote == PlayerNoteVote.Like)
        {
            note.LikeCount = Math.Max(0, note.LikeCount - 1);
        }
        else
        {
            note.DislikeCount = Math.Max(0, note.DislikeCount - 1);
        }
    }

    private static string VoteToken(PlayerNoteVote vote) =>
        vote == PlayerNoteVote.Like ? "like" : "dislike";

    private static PlayerNote WithIdentity(PlayerNote? existing, string playerName, string playerId, string content)
    {
        var note = existing is null ? new PlayerNote() : Clone(existing);
        note.PlayerName = FirstNonEmpty(playerName, note.PlayerName);
        note.PlayerId = FirstNonEmpty(playerId, note.PlayerId);
        note.Content = content;
        note.UpdatedAt = DateTimeOffset.Now;
        return note;
    }

    private static Dictionary<string, PlayerNote> Normalize(IReadOnlyDictionary<string, PlayerNote>? notes)
    {
        var normalized = new Dictionary<string, PlayerNote>(StringComparer.OrdinalIgnoreCase);
        if (notes is null)
        {
            return normalized;
        }

        foreach (var (key, value) in notes)
        {
            var note = Clone(value);
            note.FillIdentityFromKey(key);
            if (!note.HasSavedData)
            {
                continue;
            }

            var storageKey = string.IsNullOrWhiteSpace(note.StorageKey) ? key.Trim() : note.StorageKey;
            if (string.IsNullOrWhiteSpace(storageKey))
            {
                continue;
            }

            normalized[storageKey] = note;
        }

        return normalized;
    }

    private static bool TryReadNotes(JsonElement element, Dictionary<string, PlayerNote> notes, out string error)
    {
        error = string.Empty;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (!TryReadNote(item, keyHint: string.Empty, out var note, out error))
                {
                    notes.Clear();
                    return false;
                }

                AddNote(notes, note);
            }

            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "The notes field must be an object or an array.";
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!TryReadNote(property.Value, property.Name, out var note, out error))
            {
                notes.Clear();
                return false;
            }

            AddNote(notes, note);
        }

        return true;
    }

    private static bool TryReadNote(JsonElement element, string keyHint, out PlayerNote note, out string error)
    {
        note = new PlayerNote();
        error = string.Empty;

        if (element.ValueKind is JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            note.Content = element.GetString()?.Trim() ?? string.Empty;
            note.FillIdentityFromKey(keyHint);
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "Each note must be a string or an object.";
            return false;
        }

        note = JsonSerializer.Deserialize<PlayerNote>(element.GetRawText(), JsonOptions) ?? new PlayerNote();
        note.Content = note.Content.Trim();
        note.PlayerName = note.PlayerName.Trim();
        note.PlayerId = note.PlayerId.Trim();
        note.LastVoteMatchId = note.LastVoteMatchId.Trim();
        note.LastVote = note.LastVote.Trim();
        note.LikeCount = Math.Max(0, note.LikeCount);
        note.DislikeCount = Math.Max(0, note.DislikeCount);
        note.FillIdentityFromKey(keyHint);
        return true;
    }

    private static void AddNote(Dictionary<string, PlayerNote> notes, PlayerNote note)
    {
        if (!note.HasSavedData)
        {
            return;
        }

        var key = note.StorageKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        notes[key] = note;
    }

    private static PlayerNote MergeNote(PlayerNote existing, PlayerNote incoming) =>
        new()
        {
            PlayerName = FirstNonEmpty(incoming.PlayerName, existing.PlayerName),
            PlayerId = FirstNonEmpty(incoming.PlayerId, existing.PlayerId),
            Content = incoming.Content.Trim(),
            LikeCount = incoming.LikeCount,
            DislikeCount = incoming.DislikeCount,
            IsToxic = incoming.IsToxic,
            LastVoteMatchId = FirstNonEmpty(incoming.LastVoteMatchId, existing.LastVoteMatchId),
            LastVote = FirstNonEmpty(incoming.LastVote, existing.LastVote),
            UpdatedAt = incoming.UpdatedAt ?? existing.UpdatedAt ?? DateTimeOffset.Now,
        };

    private static bool NotesEqual(PlayerNote left, PlayerNote right) =>
        string.Equals(left.Content, right.Content, StringComparison.Ordinal)
        && string.Equals(left.PlayerName, right.PlayerName, StringComparison.Ordinal)
        && string.Equals(left.PlayerId, right.PlayerId, StringComparison.Ordinal)
        && left.LikeCount == right.LikeCount
        && left.DislikeCount == right.DislikeCount
        && left.IsToxic == right.IsToxic
        && string.Equals(left.LastVoteMatchId, right.LastVoteMatchId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.LastVote, right.LastVote, StringComparison.OrdinalIgnoreCase);

    private static PlayerNote Clone(PlayerNote note) =>
        new()
        {
            PlayerName = note.PlayerName?.Trim() ?? string.Empty,
            PlayerId = note.PlayerId?.Trim() ?? string.Empty,
            Content = note.Content?.Trim() ?? string.Empty,
            LikeCount = note.LikeCount,
            DislikeCount = note.DislikeCount,
            IsToxic = note.IsToxic,
            LastVoteMatchId = note.LastVoteMatchId?.Trim() ?? string.Empty,
            LastVote = note.LastVote?.Trim() ?? string.Empty,
            UpdatedAt = note.UpdatedAt,
        };

    private static bool HasPlayerId(PlayerNote note, string id) =>
        string.Equals(note.PlayerId?.Trim(), id, StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyIdKey(PlayerNote note, string id) =>
        string.IsNullOrWhiteSpace(note.PlayerId) && PlayerNote.LooksLikeSteamId(id);

    private static bool IsSupportedFormat(string? format) =>
        string.Equals(format, FormatId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(format, LegacyFormatId, StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(string? preferred, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred.Trim();
        }

        return fallback?.Trim() ?? string.Empty;
    }

    private sealed class PlayerNotesDocument
    {
        public string Format { get; set; } = FormatId;
        public int Version { get; set; } = CurrentVersion;
        public List<PlayerNote> Notes { get; set; } = [];
    }
}

public readonly record struct PlayerNotesMergeResult(int Added, int Updated, int Skipped, int Total);
