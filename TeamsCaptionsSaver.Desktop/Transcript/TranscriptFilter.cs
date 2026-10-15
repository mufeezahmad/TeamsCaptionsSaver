using TeamsCaptionsSaver.Desktop.Models;

namespace TeamsCaptionsSaver.Desktop.Transcript;

/// <summary>
/// Which speaker turns the window shows: chosen speakers, a text search, and optionally only
/// starred or highlighted turns. Speakers are tracked as <em>hidden</em> rather than shown, so
/// anyone who starts talking mid-meeting appears automatically.
/// </summary>
public sealed class TranscriptFilter
{
    public HashSet<string> HiddenSpeakers { get; } = new(StringComparer.Ordinal);

    public string SearchText { get; set; } = "";

    /// <summary>Show only turns that are starred or have highlighted words.</summary>
    public bool MarkedOnly { get; set; }

    public bool IsActive => HiddenSpeakers.Count > 0 || SearchText.Trim().Length > 0 || MarkedOnly;

    public bool Matches(SpeakerTurn turn)
    {
        if (HiddenSpeakers.Contains(turn.Speaker)) return false;
        if (MarkedOnly && !turn.IsMarked) return false;
        var search = SearchText.Trim();
        return search.Length == 0 ||
               turn.Text.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
               turn.Speaker.Contains(search, StringComparison.CurrentCultureIgnoreCase);
    }

    public void ShowOnly(string speaker, IEnumerable<string> allSpeakers)
    {
        HiddenSpeakers.Clear();
        foreach (var s in allSpeakers)
            if (s != speaker) HiddenSpeakers.Add(s);
    }

    /// <summary>Keeps a hidden speaker hidden after they've been renamed.</summary>
    public void RenameSpeaker(string oldName, string newName)
    {
        if (oldName != newName && HiddenSpeakers.Remove(oldName)) HiddenSpeakers.Add(newName);
    }

    public void Clear()
    {
        HiddenSpeakers.Clear();
        SearchText = "";
        MarkedOnly = false;
    }
}
