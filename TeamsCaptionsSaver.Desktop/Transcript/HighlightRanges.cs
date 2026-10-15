using System.Text;

namespace TeamsCaptionsSaver.Desktop.Transcript;

/// <summary>Finds where highlighted phrases occur in a text, for drawing and exporting them.</summary>
public static class HighlightRanges
{
    /// <summary>Every occurrence of every phrase (ignoring case), sorted, with overlaps merged.</summary>
    public static List<(int Start, int Length)> Find(string text, IEnumerable<string> phrases)
    {
        var ranges = new List<(int Start, int End)>();
        foreach (var phrase in phrases)
        {
            if (phrase.Length == 0) continue;
            for (int i = text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase); i >= 0;
                 i = text.IndexOf(phrase, i + phrase.Length, StringComparison.OrdinalIgnoreCase))
                ranges.Add((i, i + phrase.Length));
        }

        ranges.Sort();
        var merged = new List<(int Start, int Length)>();
        int curStart = -1, curEnd = -1;
        foreach (var (start, end) in ranges)
        {
            if (start <= curEnd)
            {
                curEnd = Math.Max(curEnd, end);
                continue;
            }
            if (curStart >= 0) merged.Add((curStart, curEnd - curStart));
            (curStart, curEnd) = (start, end);
        }
        if (curStart >= 0) merged.Add((curStart, curEnd - curStart));
        return merged;
    }

    /// <summary>Wraps each highlighted part of <paramref name="text"/> in <paramref name="open"/>/<paramref name="close"/>.</summary>
    public static string Mark(string text, IEnumerable<string> phrases, string open, string close)
    {
        var sb = new StringBuilder();
        int pos = 0;
        foreach (var (start, length) in Find(text, phrases))
        {
            sb.Append(text, pos, start - pos).Append(open).Append(text, start, length).Append(close);
            pos = start + length;
        }
        return sb.Append(text, pos, text.Length - pos).ToString();
    }
}
