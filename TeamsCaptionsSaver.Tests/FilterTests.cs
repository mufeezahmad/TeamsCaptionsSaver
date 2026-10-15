using System.IO;
using TeamsCaptionsSaver.Desktop.Models;
using TeamsCaptionsSaver.Desktop.Transcript;
using Xunit;

namespace TeamsCaptionsSaver.Tests;

public class FilterTests
{
    private static TranscriptSession Meeting()
    {
        var s = new TranscriptSession(n => n) { Title = "Weekly sync" };
        s.Merge([
            new ParsedCaption("c1", "Ali Khan", "Let's discuss the deadline."),
            new ParsedCaption("c2", "Sara Ahmed", "I think Friday works."),
            new ParsedCaption("c3", "Bob", "Friday is fine for me."),
            new ParsedCaption("c4", "Ali Khan", "Great, Friday it is."),
        ]);
        return s;
    }

    [Fact]
    public void No_filter_shows_everything()
    {
        var f = new TranscriptFilter();
        Assert.False(f.IsActive);
        Assert.All(Meeting().Turns, t => Assert.True(f.Matches(t)));
    }

    [Fact]
    public void Hidden_speakers_are_left_out()
    {
        var s = Meeting();
        var f = new TranscriptFilter();
        f.HiddenSpeakers.Add("Bob");

        var shown = s.Turns.Where(f.Matches).Select(t => t.Speaker).ToList();
        Assert.True(f.IsActive);
        Assert.Equal(["Ali Khan", "Sara Ahmed", "Ali Khan"], shown);
    }

    [Fact]
    public void Show_only_one_speaker()
    {
        var s = Meeting();
        var f = new TranscriptFilter();
        f.ShowOnly("Ali Khan", s.Speakers);

        Assert.All(s.Turns.Where(f.Matches), t => Assert.Equal("Ali Khan", t.Speaker));
        Assert.Equal(2, s.Turns.Count(f.Matches));
    }

    [Fact]
    public void Search_ignores_case_and_also_matches_speaker_names()
    {
        var s = Meeting();
        var f = new TranscriptFilter { SearchText = "  FRIDAY " };
        Assert.Equal(3, s.Turns.Count(f.Matches));

        f.SearchText = "sara";
        Assert.Equal("Sara Ahmed", Assert.Single(s.Turns, f.Matches).Speaker);
    }

    [Fact]
    public void Search_and_speakers_combine()
    {
        var s = Meeting();
        var f = new TranscriptFilter { SearchText = "friday" };
        f.HiddenSpeakers.Add("Bob");

        var shown = s.Turns.Where(f.Matches).Select(t => t.Speaker).ToList();
        Assert.Equal(["Sara Ahmed", "Ali Khan"], shown);
    }

    [Fact]
    public void A_new_speaker_is_shown_automatically()
    {
        var s = Meeting();
        var f = new TranscriptFilter();
        f.HiddenSpeakers.Add("Bob");

        s.Merge([new ParsedCaption("c5", "Zara", "Sorry I'm late.")]);
        Assert.True(f.Matches(s.Turns[^1]));
    }

    [Fact]
    public void Renamed_hidden_speaker_stays_hidden()
    {
        var f = new TranscriptFilter();
        f.HiddenSpeakers.Add("Conference Room");
        f.RenameSpeaker("Conference Room", "Board room");

        Assert.Contains("Board room", f.HiddenSpeakers);
        Assert.DoesNotContain("Conference Room", f.HiddenSpeakers);
    }

    [Fact]
    public void Exports_of_a_filtered_list_contain_only_those_turns_and_say_so()
    {
        var s = Meeting();
        var f = new TranscriptFilter();
        f.ShowOnly("Ali Khan", s.Speakers);
        var shown = s.Turns.Where(f.Matches).ToList();

        Assert.True(TranscriptExporter.IsFiltered(s, shown));
        Assert.False(TranscriptExporter.IsFiltered(s, null));

        var text = TranscriptExporter.ToText(s, shown);
        Assert.Contains("Speakers: Ali Khan", text);
        Assert.Contains("Filtered: 2 of 4 turns", text);
        Assert.DoesNotContain("Sara Ahmed", text);

        Assert.DoesNotContain("Bob", TranscriptExporter.ToCsv(s, shown));
        Assert.DoesNotContain("Bob", TranscriptExporter.ToVtt(s, shown));
        Assert.Contains("\"totalTurns\": 4", TranscriptExporter.ToJson(s, shown));
        Assert.Contains("**Filtered:** 2 of 4 turns", TranscriptExporter.ToMarkdown(s, shown));

        // Unfiltered exports are unchanged.
        Assert.DoesNotContain("Filtered:", TranscriptExporter.ToText(s));
        Assert.Contains("\"filtered\": null", TranscriptExporter.ToJson(s));
    }

    [Fact]
    public void Filtered_save_all_never_overwrites_the_full_transcript()
    {
        var s = Meeting();
        var folder = Path.Combine(Path.GetTempPath(), "tcs-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var full = TranscriptExporter.SaveAll(s, folder, "{title}", ["txt"]);
            var filtered = TranscriptExporter.SaveAll(s, folder, "{title}", ["txt"], [s.Turns[0]]);

            Assert.Equal("Weekly sync.txt", Path.GetFileName(full[0]));
            Assert.Equal("Weekly sync_filtered.txt", Path.GetFileName(filtered[0]));
            Assert.Contains("Bob", File.ReadAllText(full[0]));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
