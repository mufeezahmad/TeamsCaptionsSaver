using TeamsCaptionsSaver.Desktop.Models;
using TeamsCaptionsSaver.Desktop.Transcript;
using Xunit;

namespace TeamsCaptionsSaver.Tests;

public class MarksTests
{
    [Fact]
    public void Finds_every_occurrence_ignoring_case_and_merges_overlaps()
    {
        var ranges = HighlightRanges.Find("Friday works. friday it is.", ["FRIDAY", "day works", ""]);
        // "Friday" (0-6) and "day works" (3-12) overlap and merge; the second "friday" stays separate.
        Assert.Equal([(0, 12), (14, 6)], ranges);
    }

    [Fact]
    public void Mark_wraps_highlighted_parts()
    {
        Assert.Equal("The [[deadline]] is [[Friday]].",
            HighlightRanges.Mark("The deadline is Friday.", ["friday", "deadline"], "[[", "]]"));
        Assert.Equal("Nothing here.", HighlightRanges.Mark("Nothing here.", [], "[[", "]]"));
    }

    private static TranscriptSession Meeting(Dictionary<string, string>? aliases = null)
    {
        aliases ??= [];
        var s = new TranscriptSession(n => aliases.TryGetValue(n, out var a) ? a : n);
        s.Merge([
            new ParsedCaption("c1", "Ali", "We need to ship by"),
            new ParsedCaption("c2", "Ali", "Friday at the latest."),
            new ParsedCaption("c3", "Sara", "Agreed."),
        ]);
        return s;
    }

    [Fact]
    public void Bookmarking_a_turn_and_removing_it()
    {
        var s = Meeting();
        var turn = s.Turns[0];
        var changed = new List<string?>();
        turn.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        turn.IsBookmarked = true;
        Assert.True(turn.IsBookmarked);
        Assert.True(turn.IsMarked);
        Assert.Contains(nameof(SpeakerTurn.IsBookmarked), changed);
        Assert.Contains(nameof(SpeakerTurn.IsMarked), changed);

        turn.IsBookmarked = false;
        Assert.All(turn.Entries, e => Assert.False(e.IsBookmarked));
        Assert.False(turn.IsMarked);
    }

    [Fact]
    public void Highlight_is_stored_on_the_caption_where_it_starts()
    {
        var turn = Meeting().Turns[0];
        turn.AddHighlight("  Friday  ");

        Assert.Empty(turn.Entries[0].Highlights);
        Assert.Equal(["Friday"], turn.Entries[1].Highlights);
        Assert.Equal(["Friday"], turn.Highlights);
    }

    [Fact]
    public void Highlights_stay_attached_while_the_caption_is_still_growing()
    {
        var s = Meeting();
        s.Turns[0].AddHighlight("ship");

        s.Merge([new ParsedCaption("c1", "Ali", "We really need to ship this by")]);

        var text = s.Turns[0].Text;
        var range = Assert.Single(HighlightRanges.Find(text, s.Turns[0].Highlights));
        Assert.Equal("ship", text.Substring(range.Start, range.Length));
    }

    [Fact]
    public void Marks_survive_renaming_and_regrouping_speakers()
    {
        var aliases = new Dictionary<string, string>();
        var s = Meeting(aliases);
        s.Turns[1].IsBookmarked = true;      // Sara's turn
        s.Turns[0].AddHighlight("Friday");   // Ali's turn

        aliases["Sara"] = "Ali";             // now all three captions are one speaker
        s.ApplyAliases();

        var turn = Assert.Single(s.Turns);
        Assert.True(turn.IsBookmarked);
        Assert.Equal(["Friday"], turn.Highlights);
    }

    [Fact]
    public void Removing_highlights_by_selection_or_all()
    {
        var turn = Meeting().Turns[0];
        turn.AddHighlight("ship");
        turn.AddHighlight("Friday");
        var text = turn.Text;

        // Selection covering only "ship" removes just that one.
        turn.RemoveHighlights(text.IndexOf("ship"), 4);
        Assert.Equal(["Friday"], turn.Highlights);

        // Empty selection removes everything in the turn.
        turn.AddHighlight("ship");
        turn.RemoveHighlights(0, 0);
        Assert.Empty(turn.Highlights);
        Assert.False(turn.IsMarked);
    }

    [Fact]
    public void Marked_only_filter_shows_starred_and_highlighted_turns()
    {
        var s = Meeting();
        s.Merge([new ParsedCaption("c4", "Bob", "One more thing.")]);
        s.Turns[0].AddHighlight("Friday");
        s.Turns[2].IsBookmarked = true;

        var f = new TranscriptFilter { MarkedOnly = true };
        Assert.True(f.IsActive);
        Assert.Equal(["Ali", "Bob"], s.Turns.Where(f.Matches).Select(t => t.Speaker));
    }

    [Fact]
    public void Exports_include_a_highlights_section_and_marked_text()
    {
        var s = Meeting();
        s.Title = "Release planning";
        s.Turns[0].AddHighlight("Friday");
        s.Turns[1].IsBookmarked = true;

        var text = TranscriptExporter.ToText(s);
        Assert.Contains("Highlights:", text);
        Assert.Contains("🖍 [", text);
        Assert.Contains("Ali: “Friday”", text);
        Assert.Contains("⭐ [", text);
        Assert.Contains("Sara: Agreed.", text);
        Assert.Contains("We need to ship by [[Friday]] at the latest.", text);

        var md = TranscriptExporter.ToMarkdown(s);
        Assert.Contains("## Highlights", md);
        Assert.Contains("ship by **Friday** at", md);

        var json = TranscriptExporter.ToJson(s);
        Assert.Contains("\"bookmarked\": true", json);
        Assert.Contains("\"highlights\": [\n        \"Friday\"", json.Replace("\r\n", "\n"));

        var csv = TranscriptExporter.ToCsv(s);
        Assert.StartsWith("Start,End,Speaker,Text,Bookmarked,Highlights", csv);
        Assert.Contains(",yes,", csv);

        // Copy selected uses FormatTurn, which shows the same marks.
        Assert.StartsWith("⭐ [", TranscriptExporter.FormatTurn(s.Turns[1]));
    }

    [Fact]
    public void Exports_without_marks_have_no_highlights_section()
    {
        var s = Meeting();
        Assert.DoesNotContain("Highlights:", TranscriptExporter.ToText(s));
        Assert.DoesNotContain("## Highlights", TranscriptExporter.ToMarkdown(s));
    }
}
