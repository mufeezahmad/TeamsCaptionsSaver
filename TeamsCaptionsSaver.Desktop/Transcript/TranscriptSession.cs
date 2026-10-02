using System.Collections.ObjectModel;
using TeamsCaptionsSaver.Desktop.Models;

namespace TeamsCaptionsSaver.Desktop.Transcript;

/// <summary>
/// The captions captured for one meeting. Teams edits a caption in place while the speaker talks,
/// so each scan updates existing entries (matched by UIA element identity) and appends new ones.
/// Must be used from the UI thread (it owns an ObservableCollection bound to the grid).
/// </summary>
public sealed class TranscriptSession
{
    private const int DuplicateLookback = 30;
    private const int MinDuplicateLength = 25;

    private readonly Dictionary<string, CaptionEntry> _byKey = new();
    private readonly Func<string, string> _resolveAlias;

    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; set; } = "Teams Meeting";
    public DateTime StartedAt { get; } = DateTime.Now;
    public DateTime? EndedAt { get; set; }
    public ObservableCollection<CaptionEntry> Entries { get; } = [];

    /// <summary>True when entries changed since the last backup.</summary>
    public bool IsDirty { get; set; }

    /// <summary>True when there are entries that have not been exported yet.</summary>
    public bool HasUnsavedChanges { get; set; }

    public TranscriptSession(Func<string, string> resolveAlias)
    {
        _resolveAlias = resolveAlias;
    }

    public IEnumerable<string> Speakers => Entries.Select(e => e.Speaker).Distinct();

    public void Merge(IReadOnlyList<ParsedCaption> captions)
    {
        var now = DateTime.Now;
        var visibleKeys = captions.Select(c => c.Key).ToHashSet();

        foreach (var caption in captions)
        {
            if (_byKey.TryGetValue(caption.Key, out var existing))
            {
                if (existing.Text != caption.Text)
                {
                    existing.Text = caption.Text;
                    existing.LastUpdated = now;
                    MarkChanged();
                }
                continue;
            }

            // Teams sometimes re-renders the caption list, giving an old caption a new element.
            // Re-attach it to the existing entry instead of duplicating it. Short captions ("Okay.")
            // are legitimately repeated, so only longer ones are treated as re-renders.
            var match = caption.Text.Length < MinDuplicateLength ? null : Entries.Reverse().Take(DuplicateLookback).FirstOrDefault(e =>
                e.RawSpeaker == caption.Speaker && e.Text.Length >= MinDuplicateLength && !visibleKeys.Contains(e.Key) &&
                (e.Text == caption.Text || caption.Text.StartsWith(e.Text, StringComparison.Ordinal) ||
                 e.Text.StartsWith(caption.Text, StringComparison.Ordinal)));
            if (match != null)
            {
                _byKey[caption.Key] = match;
                if (caption.Text.Length > match.Text.Length)
                {
                    match.Text = caption.Text;
                    match.LastUpdated = now;
                    MarkChanged();
                }
                continue;
            }

            var entry = new CaptionEntry
            {
                Key = caption.Key,
                FirstSeen = now,
                RawSpeaker = caption.Speaker,
                Speaker = _resolveAlias(caption.Speaker),
                Text = caption.Text,
                LastUpdated = now,
            };
            _byKey[caption.Key] = entry;
            Entries.Add(entry);
            MarkChanged();
        }
    }

    public void ApplyAliases()
    {
        foreach (var e in Entries) e.Speaker = _resolveAlias(e.RawSpeaker);
        MarkChanged();
    }

    private void MarkChanged()
    {
        IsDirty = true;
        HasUnsavedChanges = true;
    }
}
