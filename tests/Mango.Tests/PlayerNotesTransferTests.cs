using Mango.Models;
using Mango.Services;
using Xunit;

namespace Mango.Tests;

public class PlayerNotesTransferTests
{
    [Fact]
    public void Serialize_RoundTripsNotes()
    {
        var notes = new Dictionary<string, PlayerNote>
        {
            ["76561198000000000"] = new()
            {
                PlayerName = "Invoker",
                PlayerId = "76561198000000000",
                Content = "Always mid",
                UpdatedAt = DateTimeOffset.Parse("2026-08-24T01:00:00+07:00"),
            },
            ["Dire Carry"] = new() { PlayerName = "Dire Carry", Content = "Rushes BKB" },
        };

        var json = PlayerNotesTransfer.Serialize(notes);
        Assert.Contains("mango-player-notes", json);

        Assert.True(PlayerNotesTransfer.TryParse(json, out var parsed, out var error));
        Assert.Equal(string.Empty, error);
        Assert.Equal("Always mid", parsed["76561198000000000"].Content);
        Assert.Equal("Invoker", parsed["76561198000000000"].PlayerName);
        Assert.Equal("Rushes BKB", parsed["Dire Carry"].Content);
    }

    [Fact]
    public void TryParse_AcceptsRawStringMap()
    {
        const string json = """{ "Player One": "Ganks a lot" }""";

        Assert.True(PlayerNotesTransfer.TryParse(json, out var notes, out var error));
        Assert.Equal(string.Empty, error);
        Assert.Equal("Ganks a lot", notes["Player One"].Content);
        Assert.Equal("Player One", notes["Player One"].PlayerName);
    }

    [Fact]
    public void TryParse_AcceptsLegacyKeyedStrings()
    {
        const string json = """
            { "format": "apem-player-notes", "version": 1, "notes": { "76561198000000000": "Always mid" } }
            """;

        Assert.True(PlayerNotesTransfer.TryParse(json, out var notes, out var error));
        Assert.Equal(string.Empty, error);
        Assert.Equal("Always mid", notes["76561198000000000"].Content);
        Assert.Equal("76561198000000000", notes["76561198000000000"].PlayerId);
    }

    [Fact]
    public void TryParse_RejectsInvalidJson()
    {
        Assert.False(PlayerNotesTransfer.TryParse("{ not json", out _, out var error));
        Assert.Equal("That file is not valid JSON.", error);
    }

    [Fact]
    public void TryParse_RejectsUnknownFormat()
    {
        const string json = """{ "format": "other", "notes": { "a": "b" } }""";

        Assert.False(PlayerNotesTransfer.TryParse(json, out _, out var error));
        Assert.Equal("That JSON file is not a Mango player notes export.", error);
    }

    [Fact]
    public void Apply_MergesAndUpdates()
    {
        var target = new Dictionary<string, PlayerNote>(StringComparer.OrdinalIgnoreCase)
        {
            ["existing"] = new() { PlayerName = "existing", Content = "old" },
            ["keep"] = new() { PlayerName = "keep", Content = "same" },
        };
        var imported = new Dictionary<string, PlayerNote>
        {
            ["existing"] = new() { PlayerName = "existing", Content = "new" },
            ["keep"] = new() { PlayerName = "keep", Content = "same" },
            ["fresh"] = new() { PlayerName = "fresh", Content = "added" },
        };

        var result = PlayerNotesTransfer.Apply(target, imported, replace: false);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("new", target["existing"].Content);
        Assert.Equal("same", target["keep"].Content);
        Assert.Equal("added", target["fresh"].Content);
        Assert.Equal(3, result.Total);
    }

    [Fact]
    public void Apply_ReplaceClearsExistingNotes()
    {
        var target = new Dictionary<string, PlayerNote>(StringComparer.OrdinalIgnoreCase)
        {
            ["old"] = new() { PlayerName = "old", Content = "gone" },
        };

        var result = PlayerNotesTransfer.Apply(
            target,
            new Dictionary<string, PlayerNote> { ["new"] = new() { PlayerName = "new", Content = "kept" } },
            replace: true);

        Assert.False(target.ContainsKey("old"));
        Assert.Equal("kept", target["new"].Content);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Total);
    }

    [Fact]
    public void Upsert_SetsNameIdContentAndTimestamp()
    {
        var notes = new Dictionary<string, PlayerNote>(StringComparer.OrdinalIgnoreCase);
        PlayerNotesTransfer.Upsert(notes, "Sakamoto", "76561198000000000", "Test");

        var note = notes["76561198000000000"];
        Assert.Equal("Sakamoto", note.PlayerName);
        Assert.Equal("76561198000000000", note.PlayerId);
        Assert.Equal("Test", note.Content);
        Assert.NotNull(note.UpdatedAt);
    }

    [Fact]
    public void TryGetByPlayerId_MatchesIdAndIgnoresName()
    {
        var notes = new Dictionary<string, PlayerNote>(StringComparer.OrdinalIgnoreCase)
        {
            ["Invoker"] = new() { PlayerName = "Invoker", Content = "By name" },
            ["76561198000000000"] = new()
            {
                PlayerName = "Different nick",
                PlayerId = "76561198000000000",
                Content = "By id",
            },
        };

        Assert.True(PlayerNotesTransfer.TryGetByPlayerId(notes, "76561198000000000", out var note));
        Assert.Equal("By id", note!.Content);
        Assert.Equal(string.Empty, PlayerNotesTransfer.FindContentByPlayerId(notes, "Invoker"));
        Assert.False(PlayerNotesTransfer.TryGetByPlayerId(notes, "Invoker", out _));
        Assert.Equal(string.Empty, PlayerNotesTransfer.FindContentByPlayerId(notes, string.Empty));
    }

    [Fact]
    public void Serialize_RoundTripsLikeAndDislikeCounts()
    {
        var notes = new Dictionary<string, PlayerNote>
        {
            ["76561198000000000"] = new()
            {
                PlayerName = "Invoker",
                PlayerId = "76561198000000000",
                Content = "Always mid",
                LikeCount = 3,
                DislikeCount = 1,
                LastVoteMatchId = "8345",
                LastVote = "like",
            },
        };

        var json = PlayerNotesTransfer.Serialize(notes);
        Assert.Contains("likeCount", json, StringComparison.Ordinal);
        Assert.Contains("dislikeCount", json, StringComparison.Ordinal);

        Assert.True(PlayerNotesTransfer.TryParse(json, out var parsed, out var error));
        Assert.Equal(string.Empty, error);
        Assert.Equal(3, parsed["76561198000000000"].LikeCount);
        Assert.Equal(1, parsed["76561198000000000"].DislikeCount);
        Assert.Equal("8345", parsed["76561198000000000"].LastVoteMatchId);
        Assert.Equal("like", parsed["76561198000000000"].LastVote);
    }

    [Fact]
    public void TryVote_TogglesSameVoteAndSwitchesOppositeVote()
    {
        var notes = new Dictionary<string, PlayerNote>(StringComparer.OrdinalIgnoreCase);

        Assert.True(PlayerNotesTransfer.TryVote(notes, "Invoker", "76561198000000000", "match-1", PlayerNoteVote.Like));
        Assert.Equal(1, notes["76561198000000000"].LikeCount);
        Assert.Equal("like", notes["76561198000000000"].LastVote);

        Assert.True(PlayerNotesTransfer.TryVote(notes, "Invoker", "76561198000000000", "match-1", PlayerNoteVote.Like));
        Assert.False(notes.ContainsKey("76561198000000000"));

        Assert.True(PlayerNotesTransfer.TryVote(notes, "Invoker", "76561198000000000", "match-1", PlayerNoteVote.Like));
        Assert.True(PlayerNotesTransfer.TryVote(notes, "Invoker", "76561198000000000", "match-1", PlayerNoteVote.Dislike));

        var note = notes["76561198000000000"];
        Assert.Equal(0, note.LikeCount);
        Assert.Equal(1, note.DislikeCount);
        Assert.Equal("match-1", note.LastVoteMatchId);
        Assert.Equal("dislike", note.LastVote);

        Assert.True(PlayerNotesTransfer.TryVote(notes, "Invoker", "76561198000000000", "match-2", PlayerNoteVote.Like));
        note = notes["76561198000000000"];
        Assert.Equal(1, note.LikeCount);
        Assert.Equal(1, note.DislikeCount);
        Assert.Equal("match-2", note.LastVoteMatchId);
        Assert.Equal("like", note.LastVote);
    }

    [Fact]
    public void TryVote_KeepsVoteOnlyNotesWithoutContent()
    {
        var notes = new Dictionary<string, PlayerNote>(StringComparer.OrdinalIgnoreCase);
        Assert.True(PlayerNotesTransfer.TryVote(notes, "Invoker", "76561198000000000", "match-1", PlayerNoteVote.Like));

        var json = PlayerNotesTransfer.Serialize(notes);
        Assert.True(PlayerNotesTransfer.TryParse(json, out var parsed, out var error));
        Assert.Equal(string.Empty, error);
        Assert.True(parsed["76561198000000000"].HasSavedData);
        Assert.Equal(1, parsed["76561198000000000"].LikeCount);
        Assert.Equal(string.Empty, parsed["76561198000000000"].Content);
    }

    [Fact]
    public void ToxicMark_RoundTripsWithoutNoteAndTogglesOff()
    {
        var notes = new Dictionary<string, PlayerNote>();
        const string id = "76561198000000000";
        Assert.True(PlayerNotesTransfer.TryToggleToxic(notes, "Invoker", id));
        Assert.True(notes[id].IsToxic);

        PlayerNotesTransfer.Upsert(notes, "Invoker", id, string.Empty);
        Assert.True(PlayerNotesTransfer.TryParse(PlayerNotesTransfer.Serialize(notes), out var parsed, out var error));
        Assert.Equal(string.Empty, error);
        Assert.True(parsed[id].IsToxic);

        Assert.True(PlayerNotesTransfer.TryToggleToxic(parsed, "Invoker", id));
        Assert.False(parsed.ContainsKey(id));
    }

    [Fact]
    public void ToxicMark_IsIndependentOfVotesAndPreservedWhenEditingNote()
    {
        var notes = new Dictionary<string, PlayerNote>();
        const string id = "76561198000000000";
        PlayerNotesTransfer.TryToggleToxic(notes, "Invoker", id);
        PlayerNotesTransfer.TryVote(notes, "Invoker", id, "match-1", PlayerNoteVote.Dislike);
        PlayerNotesTransfer.Upsert(notes, "Invoker", id, "Always mid");
        Assert.True(notes[id].IsToxic);
        Assert.Equal("dislike", notes[id].LastVote);

        PlayerNotesTransfer.TryToggleToxic(notes, "Invoker", id);
        Assert.False(notes[id].IsToxic);
        Assert.Equal("dislike", notes[id].LastVote);
        Assert.Equal("Always mid", notes[id].Content);
        Assert.False(PlayerNotesTransfer.TryToggleToxic(notes, "Unknown", string.Empty));
    }

    [Fact]
    public void Upsert_PreservesLikeCountsWhenEditingContent()
    {
        var notes = new Dictionary<string, PlayerNote>(StringComparer.OrdinalIgnoreCase);
        PlayerNotesTransfer.TryVote(notes, "Invoker", "76561198000000000", "match-1", PlayerNoteVote.Like);
        PlayerNotesTransfer.Upsert(notes, "Invoker", "76561198000000000", "Always mid");

        var note = notes["76561198000000000"];
        Assert.Equal("Always mid", note.Content);
        Assert.Equal(1, note.LikeCount);
    }
}
