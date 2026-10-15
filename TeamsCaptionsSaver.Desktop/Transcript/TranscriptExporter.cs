using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TeamsCaptionsSaver.Desktop.Models;

namespace TeamsCaptionsSaver.Desktop.Transcript;

/// <summary>
/// Writes a transcript in each supported format. Every method takes an optional list of turns, so the
/// window can export just what its filters show; by default the whole session is exported.
/// </summary>
public static class TranscriptExporter
{
    public static readonly (string Ext, string Label)[] Formats =
    [
        ("txt", "Plain text"),
        ("md", "Markdown"),
        ("json", "JSON"),
        ("csv", "CSV (Excel)"),
        ("vtt", "WebVTT subtitles"),
    ];

    public static string FileDialogFilter =>
        string.Join("|", Formats.Select(f => $"{f.Label} (*.{f.Ext})|*.{f.Ext}"));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string BuildFileName(string pattern, TranscriptSession session)
    {
        var name = pattern
            .Replace("{date}", session.StartedAt.ToString("yyyy-MM-dd"))
            .Replace("{time}", session.StartedAt.ToString("HH-mm"))
            .Replace("{title}", session.Title);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim(' ', '.', '_');
        return name.Length == 0 ? "Teams Transcript" : name.Length > 150 ? name[..150] : name;
    }

    /// <summary>True when <paramref name="turns"/> leaves out some of the session's turns.</summary>
    public static bool IsFiltered(TranscriptSession s, IReadOnlyList<SpeakerTurn>? turns) =>
        turns != null && turns.Count != s.Turns.Count;

    /// <summary>
    /// Writes the transcript in each format to <paramref name="folder"/>; returns the paths.
    /// A filtered subset gets a "_filtered" suffix so it never overwrites the full transcript.
    /// </summary>
    public static List<string> SaveAll(TranscriptSession session, string folder, string pattern, IEnumerable<string> formats,
        IReadOnlyList<SpeakerTurn>? turns = null)
    {
        Directory.CreateDirectory(folder);
        var baseName = BuildFileName(pattern, session) + (IsFiltered(session, turns) ? "_filtered" : "");
        var paths = new List<string>();
        foreach (var ext in formats.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(folder, $"{baseName}.{ext}");
            Save(session, path, ext, turns);
            paths.Add(path);
        }
        return paths;
    }

    public static void Save(TranscriptSession session, string path, string? format = null,
        IReadOnlyList<SpeakerTurn>? turns = null)
    {
        format ??= Path.GetExtension(path).TrimStart('.');
        var content = format.ToLowerInvariant() switch
        {
            "md" => ToMarkdown(session, turns),
            "json" => ToJson(session, turns),
            "csv" => ToCsv(session, turns),
            "vtt" => ToVtt(session, turns),
            _ => ToText(session, turns),
        };
        // UTF-8 with BOM so Excel and Notepad pick the right encoding for non-English names.
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static IEnumerable<string> SpeakersOf(IEnumerable<SpeakerTurn> turns) =>
        turns.Select(t => t.Speaker).Distinct();

    private static string Header(TranscriptSession s, IReadOnlyList<SpeakerTurn> turns)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Meeting: {s.Title}");
        sb.AppendLine($"Date: {s.StartedAt:dddd, MMMM d, yyyy}");
        sb.AppendLine($"Time: {s.StartedAt:HH:mm}{(s.EndedAt is { } end ? $" - {end:HH:mm}" : "")}");
        sb.AppendLine($"Speakers: {string.Join(", ", SpeakersOf(turns))}");
        if (IsFiltered(s, turns)) sb.AppendLine($"Filtered: {turns.Count} of {s.Turns.Count} turns");
        return sb.ToString();
    }

    public static string ToText(TranscriptSession s, IReadOnlyList<SpeakerTurn>? turns = null)
    {
        turns ??= s.Turns;
        var sb = new StringBuilder(Header(s, turns));

        var marked = turns.Where(t => t.IsMarked).ToList();
        if (marked.Count > 0)
        {
            sb.AppendLine().AppendLine("Highlights:");
            foreach (var t in marked)
                sb.AppendLine(t.IsBookmarked
                    ? $"⭐ [{t.TimeDisplay}] {t.Speaker}: {MarkText(t)}"
                    : $"🖍 [{t.TimeDisplay}] {t.Speaker}: {string.Join(" · ", t.Highlights.Select(Quote))}");
            sb.AppendLine().Append("Transcript:").AppendLine();
        }

        foreach (var t in turns)
            sb.AppendLine().AppendLine(FormatTurn(t));
        return sb.ToString();
    }

    /// <summary>
    /// One speaker turn as a plain-text line: "[17:50:50] Jonas: what they said", with a ⭐ in front
    /// when starred and [[highlighted words]] marked.
    /// </summary>
    public static string FormatTurn(SpeakerTurn t) =>
        $"{(t.IsBookmarked ? "⭐ " : "")}[{t.TimeDisplay}] {t.Speaker}: {MarkText(t)}";

    private static string MarkText(SpeakerTurn t) => HighlightRanges.Mark(t.Text, t.Highlights, "[[", "]]");

    private static string Quote(string phrase) => $"“{phrase}”";

    public static string ToMarkdown(TranscriptSession s, IReadOnlyList<SpeakerTurn>? turns = null)
    {
        turns ??= s.Turns;
        var sb = new StringBuilder();
        sb.AppendLine($"# {s.Title}");
        sb.AppendLine();
        sb.AppendLine($"- **Date:** {s.StartedAt:dddd, MMMM d, yyyy}");
        sb.AppendLine($"- **Time:** {s.StartedAt:HH:mm}{(s.EndedAt is { } end ? $" – {end:HH:mm}" : "")}");
        sb.AppendLine($"- **Speakers:** {string.Join(", ", SpeakersOf(turns))}");
        if (IsFiltered(s, turns)) sb.AppendLine($"- **Filtered:** {turns.Count} of {s.Turns.Count} turns");
        sb.AppendLine();

        var marked = turns.Where(t => t.IsMarked).ToList();
        if (marked.Count > 0)
        {
            sb.AppendLine("## Highlights");
            sb.AppendLine();
            foreach (var t in marked)
                sb.AppendLine(t.IsBookmarked
                    ? $"- ⭐ **{t.Speaker}** _{t.TimeDisplay}_: {MarkMarkdown(t)}"
                    : $"- 🖍 **{t.Speaker}** _{t.TimeDisplay}_: {string.Join(" · ", t.Highlights.Select(Quote))}");
            sb.AppendLine();
        }

        sb.AppendLine("## Transcript");
        sb.AppendLine();

        foreach (var t in turns)
        {
            sb.AppendLine($"**{t.Speaker}** _{t.TimeDisplay}_{(t.IsBookmarked ? " ⭐" : "")}  ");
            sb.AppendLine(MarkMarkdown(t));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string MarkMarkdown(SpeakerTurn t) => HighlightRanges.Mark(t.Text, t.Highlights, "**", "**");

    public static string ToJson(TranscriptSession s, IReadOnlyList<SpeakerTurn>? turns = null)
    {
        var shown = turns ?? s.Turns;
        return JsonSerializer.Serialize(new
        {
            title = s.Title,
            startedAt = s.StartedAt,
            endedAt = s.EndedAt,
            speakers = SpeakersOf(shown).ToList(),
            filtered = IsFiltered(s, turns) ? new { shownTurns = shown.Count, totalTurns = s.Turns.Count } : null,
            turns = shown.Select(t => new
            {
                speaker = t.Speaker,
                originalSpeaker = t.RawSpeaker != t.Speaker ? t.RawSpeaker : null,
                start = t.StartTime,
                end = t.EndTime,
                offsetSeconds = Math.Round((t.StartTime - s.StartedAt).TotalSeconds, 1),
                text = t.Text,
                bookmarked = t.IsBookmarked,
                highlights = t.Highlights,
                // The individual caption blocks, for anyone who needs finer timing.
                captions = t.Entries.Select(e => new { time = e.FirstSeen, text = e.Text }),
            }),
        }, JsonOptions);
    }

    public static string ToCsv(TranscriptSession s, IReadOnlyList<SpeakerTurn>? turns = null)
    {
        static string Q(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("Start,End,Speaker,Text,Bookmarked,Highlights\r\n");
        foreach (var t in turns ?? s.Turns)
            sb.Append(Q(t.StartTime.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
              .Append(Q(t.EndTime.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
              .Append(Q(t.Speaker)).Append(',').Append(Q(t.Text)).Append(',')
              .Append(t.IsBookmarked ? "yes" : "").Append(',')
              .Append(Q(string.Join("; ", t.Highlights))).Append("\r\n");
        return sb.ToString();
    }

    public static string ToVtt(TranscriptSession s, IReadOnlyList<SpeakerTurn>? turns = null)
    {
        static string Ts(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
        var sb = new StringBuilder("WEBVTT\n\n");
        var entries = turns == null ? s.Entries.ToList() : turns.SelectMany(t => t.Entries).ToList();
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var start = e.FirstSeen - s.StartedAt;
            var end = i + 1 < entries.Count ? entries[i + 1].FirstSeen - s.StartedAt : e.LastUpdated - s.StartedAt;
            if (end <= start) end = start + TimeSpan.FromSeconds(3);
            sb.Append(i + 1).Append('\n')
              .Append(Ts(start)).Append(" --> ").Append(Ts(end)).Append('\n')
              .Append("<v ").Append(e.Speaker).Append('>').Append(e.Text).Append("\n\n");
        }
        return sb.ToString();
    }
}
