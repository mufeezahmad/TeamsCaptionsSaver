using System.ComponentModel;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Add(CaptionEntry entry)
    {
        _entries.Add(entry);
        entry.PropertyChanged += OnEntryChanged;
        Raise(nameof(Text));
    }

    /// <summary>Stops listening to the entries, before the turn is thrown away.</summary>
    internal void Detach()
    {
        foreach (var e in _entries) e.PropertyChanged -= OnEntryChanged;
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CaptionEntry.Text)) Raise(nameof(Text));
        else if (e.PropertyName == nameof(CaptionEntry.Speaker)) Raise(nameof(Speaker));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
