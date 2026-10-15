using System.ComponentModel;
using TeamsCaptionsSaver.Desktop.Transcript;

namespace TeamsCaptionsSaver.Desktop.Models;

/// <summary>
/// Consecutive captions from the same speaker, shown and exported as one paragraph.
/// Teams splits a person's speech into many short caption blocks; a turn joins them back
/// together until someone else speaks.
/// </summary>
public sealed class SpeakerTurn : INotifyPropertyChanged
{
    private readonly List<CaptionEntry> _entries = [];

    public SpeakerTurn(CaptionEntry first) => Add(first);

    public IReadOnlyList<CaptionEntry> Entries => _entries;

    public string Speaker => _entries[0].Speaker;
    public string RawSpeaker => _entries[0].RawSpeaker;
    public DateTime StartTime => _entries[0].FirstSeen;
    public DateTime EndTime => _entries.Max(e => e.LastUpdated);
    public string TimeDisplay => StartTime.ToString("HH:mm:ss");
    public string Text => string.Join(" ", _entries.Select(e => e.Text));

    /// <summary>Starred by the user. Setting it to false clears the star from every caption in the turn.</summary>
    public bool IsBookmarked
    {
        get => _entries.Any(e => e.IsBookmarked);
        set
        {
            if (value == IsBookmarked) return;
            if (value) _entries[0].IsBookmarked = true;
            else foreach (var e in _entries) e.IsBookmarked = false;
        }
    }

    /// <summary>All highlighted phrases in this turn.</summary>
    public IReadOnlyList<string> Highlights =>
        _entries.SelectMany(e => e.Highlights).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>True when the turn is starred or has highlighted words.</summary>
    public bool IsMarked => IsBookmarked || _entries.Any(e => e.Highlights.Count > 0);

    /// <summary>Highlights <paramref name="phrase"/>, stored on the caption where it starts.</summary>
    public void AddHighlight(string phrase)
    {
        phrase = phrase.Trim();
        if (phrase.Length == 0) return;
        int start = Text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
        EntryAt(Math.Max(0, start)).AddHighlight(phrase);
    }

    /// <summary>
    /// Removes the highlights that overlap the given part of the text (by default <see cref="Text"/>;
    /// pass the text the selection was made in if it may have changed since). With an empty selection,
    /// removes every highlight in the turn.
    /// </summary>
    public void RemoveHighlights(int selectionStart, int selectionLength, string? selectedIn = null)
    {
        var text = selectedIn ?? Text;
        var overlapping = Highlights.Where(phrase =>
            selectionLength == 0 ||
            HighlightRanges.Find(text, [phrase]).Any(r =>
                r.Start < selectionStart + selectionLength && selectionStart < r.Start + r.Length))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _entries) e.RemoveHighlights(overlapping.Contains);
    }

    private CaptionEntry EntryAt(int charIndex)
    {
        int offset = 0;
        foreach (var e in _entries)
        {
            offset += e.Text.Length + 1; // + the joining space
            if (charIndex < offset) return e;
        }
        return _entries[^1];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Add(CaptionEntry entry)
    {
        _entries.Add(entry);
        entry.PropertyChanged += OnEntryChanged;
        Raise(nameof(Text));
        if (entry.IsBookmarked || entry.Highlights.Count > 0) RaiseMarks();
    }

    /// <summary>Stops listening to the entries, before the turn is thrown away.</summary>
    internal void Detach()
    {
        foreach (var e in _entries) e.PropertyChanged -= OnEntryChanged;
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CaptionEntry.Text): Raise(nameof(Text)); break;
            case nameof(CaptionEntry.Speaker): Raise(nameof(Speaker)); break;
            case nameof(CaptionEntry.IsBookmarked) or nameof(CaptionEntry.Highlights): RaiseMarks(); break;
        }
    }

    private void RaiseMarks()
    {
        Raise(nameof(IsBookmarked));
        Raise(nameof(Highlights));
        Raise(nameof(IsMarked));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
