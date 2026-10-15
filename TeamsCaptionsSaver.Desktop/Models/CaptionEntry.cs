using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TeamsCaptionsSaver.Desktop.Models;

/// <summary>One caption block as shown by Teams: a speaker and what they said.</summary>
public sealed class CaptionEntry : INotifyPropertyChanged
{
    private string _speaker = "";
    private string _text = "";
    private DateTime _lastUpdated;

    public required string Key { get; init; }
    public DateTime FirstSeen { get; init; }

    /// <summary>Speaker name exactly as Teams displayed it (aliases are applied on top).</summary>
    public string RawSpeaker { get; init; } = "";

    public string Speaker
    {
        get => _speaker;
        set => Set(ref _speaker, value);
    }

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    public DateTime LastUpdated
    {
        get => _lastUpdated;
        set => Set(ref _lastUpdated, value);
    }

    public string TimeDisplay => FirstSeen.ToString("HH:mm:ss");

    // Bookmarks and highlights live on the caption, not on the speaker turn: turns are rebuilt
    // when speakers are renamed, captions keep their identity.
    private bool _isBookmarked;
    private readonly List<string> _highlights = [];

    public bool IsBookmarked
    {
        get => _isBookmarked;
        set => Set(ref _isBookmarked, value);
    }

    /// <summary>
    /// Highlighted phrases. Stored as text rather than character positions, so they stay attached
    /// while Teams is still rewriting the caption.
    /// </summary>
    public IReadOnlyList<string> Highlights => _highlights;

    public void AddHighlight(string phrase)
    {
        if (phrase.Length == 0 || _highlights.Contains(phrase, StringComparer.OrdinalIgnoreCase)) return;
        _highlights.Add(phrase);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Highlights)));
    }

    public bool RemoveHighlights(Func<string, bool> predicate)
    {
        if (_highlights.RemoveAll(new Predicate<string>(predicate)) == 0) return false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Highlights)));
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A caption as parsed from the Teams UI in a single scan.</summary>
public sealed record ParsedCaption(string Key, string Speaker, string Text);
