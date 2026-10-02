using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TeamsCaptionsSaver.Desktop.Models;

namespace TeamsCaptionsSaver.Desktop.Transcript;

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

    /// <summary>Writes the transcript in each format to <paramref name="folder"/>; returns the paths.</summary>
    public static List<string> SaveAll(TranscriptSession session, string folder, string pattern, IEnumerable<string> formats)
    {
        Directory.CreateDirectory(folder);
        var baseName = BuildFileName(pattern, session);
        var paths = new List<string>();
        foreach (var ext in formats.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(folder, $"{baseName}.{ext}");
            Save(session, path, ext);
            paths.Add(path);
        }
        return paths;
    }

    public static void Save(TranscriptSession session, string path, string? format = null)
    {
        format ??= Path.GetExtension(path).TrimStart('.');
        var content = format.ToLowerInvariant() switch
        {
            "md" => ToMarkdown(session),
            "json" => ToJson(session),
            "csv" => ToCsv(session),
            "vtt" => ToVtt(session),
            _ => ToText(session),
        };
        // UTF-8 with BOM so Excel and Notepad pick the right encoding for non-English names.
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Header(TranscriptSession s, string prefix = "")
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{prefix}Meeting: {s.Title}");
        sb.AppendLine($"{prefix}Date: {s.StartedAt:dddd, MMMM d, yyyy}");
        sb.AppendLine($"{prefix}Time: {s.StartedAt:HH:mm}{(s.EndedAt is { } end ? $" - {end:HH:mm}" : "")}");
        sb.AppendLine($"{prefix}Speakers: {string.Join(", ", s.Speakers)}");
        return sb.ToString();
    }

    public static string ToText(TranscriptSession s)
    {
        var sb = new StringBuilder(Header(s));
        sb.AppendLine();
        foreach (var e in s.Entries)
            sb.AppendLine($"[{e.TimeDisplay}] {e.Speaker}: {e.Text}");
        return sb.ToString();
    }

    public static string ToMarkdown(TranscriptSession s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {s.Title}");
        sb.AppendLine();
        sb.AppendLine($"- **Date:** {s.StartedAt:dddd, MMMM d, yyyy}");
        sb.AppendLine($"- **Time:** {s.StartedAt:HH:mm}{(s.EndedAt is { } end ? $" – {end:HH:mm}" : "")}");
        sb.AppendLine($"- **Speakers:** {string.Join(", ", s.Speakers)}");
        sb.AppendLine();
        sb.AppendLine("## Transcript");
        sb.AppendLine();

        // Group consecutive captions from the same speaker into one paragraph.
        string? lastSpeaker = null;
        foreach (var e in s.Entries)
        {
            if (e.Speaker != lastSpeaker)
            {
                if (lastSpeaker != null) sb.AppendLine().AppendLine();
                sb.Append($"**{e.Speaker}** _{e.TimeDisplay}_  ").AppendLine();
                sb.Append(e.Text);
                lastSpeaker = e.Speaker;
            }
            else
            {
                sb.Append(' ').Append(e.Text);
            }
        }
        sb.AppendLine();
        return sb.ToString();
    }

    public static string ToJson(TranscriptSession s) => JsonSerializer.Serialize(new
    {
        title = s.Title,
        startedAt = s.StartedAt,
        endedAt = s.EndedAt,
        speakers = s.Speakers.ToList(),
        captions = s.Entries.Select(e => new
        {
            speaker = e.Speaker,
            originalSpeaker = e.RawSpeaker != e.Speaker ? e.RawSpeaker : null,
            text = e.Text,
            time = e.FirstSeen,
            offsetSeconds = Math.Round((e.FirstSeen - s.StartedAt).TotalSeconds, 1),
        }),
    }, JsonOptions);

    public static string ToCsv(TranscriptSession s)
    {
        static string Q(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("Time,Speaker,Text\r\n");
        foreach (var e in s.Entries)
            sb.Append(Q(e.FirstSeen.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
              .Append(Q(e.Speaker)).Append(',').Append(Q(e.Text)).Append("\r\n");
        return sb.ToString();
    }

    public static string ToVtt(TranscriptSession s)
    {
        static string Ts(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
        var sb = new StringBuilder("WEBVTT\n\n");
        var entries = s.Entries.ToList();
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
