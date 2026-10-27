using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TeamsCaptionsSaver.Desktop.Capture;
using TeamsCaptionsSaver.Desktop.Models;
using TeamsCaptionsSaver.Desktop.Transcript;

namespace TeamsCaptionsSaver.Desktop;

public partial class MainWindow : Window
{
    /// <summary>Consecutive "not in meeting" snapshots (~1/s) before we treat the meeting as over.</summary>
    private const int MeetingEndDebounce = 5;

    private static readonly string BackupFolder = Path.Combine(AppSettings.AppDataFolder, "backups");

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly TeamsCaptionReader _reader = new();
    private readonly DispatcherTimer _backupTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly TranscriptFilter _filter = new();

    private TranscriptSession _session;
    private ListCollectionView? _view;
    private bool _sessionEnded;
    private bool _paused;
    private bool _meetingActive;
    private int _notInMeetingCount;
    private bool _demoMode;

    public MainWindow()
    {
        InitializeComponent();
        _session = NewSession();
        LoadSettingsIntoUi();

        Log.MessageLogged += line => Dispatcher.BeginInvoke(() => LogLine.Text = line);

        // "--demo" shows made-up captions without connecting to Teams (for trying the UI and screenshots).
        if (Environment.GetCommandLineArgs().Contains("--demo", StringComparer.OrdinalIgnoreCase))
        {
            _demoMode = true;
            LoadDemoTranscript();
        }
        else
        {
            _reader.AutoEnableCaptions = _settings.AutoEnableCaptions;
            _reader.SnapshotTaken += snapshot => Dispatcher.BeginInvoke(() => OnSnapshot(snapshot));
            _reader.Start();
        }

        _backupTimer.Tick += (_, _) => WriteBackup();
        _backupTimer.Start();
    }

    private TranscriptSession NewSession()
    {
        var session = new TranscriptSession(ResolveAlias);
        TranscriptGrid.ItemsSource = session.Turns;

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(session.Turns);
        _view.Filter = item => _filter.Matches((SpeakerTurn)item);
        // Re-check a turn when its text grows or its speaker is renamed, not only when it's added.
        _view.IsLiveFiltering = true;
        _view.LiveFilteringProperties.Add(nameof(SpeakerTurn.Text));
        _view.LiveFilteringProperties.Add(nameof(SpeakerTurn.Speaker));
        _view.LiveFilteringProperties.Add(nameof(SpeakerTurn.IsMarked));

        // A new meeting has different speakers, so start unfiltered.
        _filter.Clear();
        SearchBox.Text = "";
        MarkedOnlyBox.IsChecked = false;

        _sessionEnded = false;
        return session;
    }

    private void LoadDemoTranscript()
    {
        _session.Title = "Demo: Weekly sync";
        _session.Merge([
            new ParsedCaption("d1", "Alex Johnson", "Morning everyone, let's start with the release checklist."),
            new ParsedCaption("d2", "Alex Johnson", "We still have two open bugs from last week."),
            new ParsedCaption("d3", "Sam Lee", "The login bug is fixed, I merged it yesterday evening."),
            new ParsedCaption("d4", "Priya Patel", "I'll take the export bug. Should be done by Thursday."),
            new ParsedCaption("d5", "Alex Johnson", "Great. Then the deadline for the release stays Friday."),
            new ParsedCaption("d6", "Sam Lee", "Sounds good. I'll update the release notes today."),
        ]);
        _session.HasUnsavedChanges = false;
        _session.IsDirty = false; // don't write demo data to the backups
        StatusText.Text = "Demo mode";
        StatusDetail.Text = "Made-up captions to try the app. Not connected to Teams.";
        StatusDot.Fill = Brushes.SteelBlue;
        CountText.Text = $"{_session.Entries.Count} captions · {_session.Speakers.Count()} speakers";
    }

    private string ResolveAlias(string speaker) =>
        _settings.SpeakerAliases.TryGetValue(speaker, out var alias) && alias.Length > 0 ? alias : speaker;

    // --- Capture ------------------------------------------------------------------------------

    private void OnSnapshot(CaptureSnapshot snap)
    {
        if (snap.InMeeting)
        {
            _notInMeetingCount = 0;
            if (!_meetingActive) OnMeetingStarted(snap.MeetingTitle);
            if (snap.MeetingTitle.Length > 0) _session.Title = snap.MeetingTitle;
        }
        else if (_meetingActive && ++_notInMeetingCount >= MeetingEndDebounce)
        {
            OnMeetingEnded();
        }

        if (!_paused && snap.Captions.Count > 0)
        {
            var before = (_session.Turns.Count, _session.Turns.LastOrDefault()?.Text);
            _session.Merge(snap.Captions);
            // The last turn keeps growing while the same person talks, so follow the bottom of the
            // grid rather than just the last row.
            if (_settings.AutoScroll && before != (_session.Turns.Count, _session.Turns.LastOrDefault()?.Text))
                FindChild<ScrollViewer>(TranscriptGrid)?.ScrollToEnd();
        }

        UpdateStatus(snap);
    }

    private void OnMeetingStarted(string title)
    {
        _meetingActive = true;
        // A previous meeting's transcript stays on screen until the next meeting starts.
        if (_sessionEnded && _session.Entries.Count > 0)
            _session = NewSession();
        _sessionEnded = false;
        _session.Title = title.Length > 0 ? title : _session.Title;
    }

    private void OnMeetingEnded()
    {
        _meetingActive = false;
        _sessionEnded = true;
        _session.EndedAt = DateTime.Now;
        WriteBackup();

        // Auto-save always writes the full transcript, whatever the filters show.
        if (_settings.AutoSaveOnMeetingEnd && _session.Entries.Count > 0 && _session.HasUnsavedChanges)
        {
            var paths = SaveAll(useFilter: false);
            if (paths != null) Log.Write($"Meeting ended - transcript auto-saved ({paths.Count} files).");
        }
    }

    private void UpdateStatus(CaptureSnapshot snap)
    {
        Color color;
        if (!snap.TeamsRunning)
        {
            color = Colors.Gray;
            StatusText.Text = "Microsoft Teams isn't running";
            StatusDetail.Text = "Start the Teams desktop app (new Teams) and join a meeting.";
        }
        else if (!snap.InMeeting)
        {
            color = Colors.Goldenrod;
            StatusText.Text = "Waiting for a meeting";
            StatusDetail.Text = "Join a Teams meeting - capture starts automatically once live captions are on.";
        }
        else if (!snap.CaptionsVisible)
        {
            color = Colors.DarkOrange;
            StatusText.Text = $"In meeting: {snap.MeetingTitle}";
            StatusDetail.Text = "Live captions are off. In Teams: More (…) > Language and speech > Turn on live captions.";
        }
        else
        {
            color = _paused ? Colors.SteelBlue : Colors.ForestGreen;
            StatusText.Text = _paused ? $"Paused - {snap.MeetingTitle}" : $"Capturing - {snap.MeetingTitle}";
            StatusDetail.Text = "Captions with speaker names are being recorded. Keep the captions visible in Teams.";
        }
        StatusDot.Fill = new SolidColorBrush(color);

        var speakers = _session.Speakers.Count();
        CountText.Text = $"{_session.Entries.Count} captions · {speakers} speaker{(speakers == 1 ? "" : "s")}";
        UpdateFilterStatus();
    }

    // --- Saving -------------------------------------------------------------------------------

    /// <param name="useFilter">Save only the turns the filters show (manual saves), or everything (auto-save).</param>
    private List<string>? SaveAll(bool useFilter)
    {
        try
        {
            var turns = useFilter ? ShownTurns() : null;
            var paths = TranscriptExporter.SaveAll(_session, _settings.OutputFolder, _settings.FilenamePattern,
                _settings.AutoSaveFormats.Count > 0 ? _settings.AutoSaveFormats : ["txt"], turns);
            // A filtered save leaves the rest unsaved, so keep warning about it on close.
            if (!TranscriptExporter.IsFiltered(_session, turns)) _session.HasUnsavedChanges = false;
            Log.Write($"Saved: {string.Join(", ", paths.Select(Path.GetFileName))}");
            return paths;
        }
        catch (Exception ex)
        {
            Log.Write($"Save failed: {ex.Message}");
            MessageBox.Show(this, $"Couldn't save the transcript:\n{ex.Message}", Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    /// <summary>Crash protection: keeps a JSON copy of the live transcript in %LOCALAPPDATA%.</summary>
    private void WriteBackup()
    {
        if (_demoMode || !_session.IsDirty || _session.Entries.Count == 0) return;
        try
        {
            Directory.CreateDirectory(BackupFolder);
            var path = Path.Combine(BackupFolder, $"{_session.StartedAt:yyyyMMdd_HHmmss}_{_session.Id:N}.json");
            TranscriptExporter.Save(_session, path, "json");
            _session.IsDirty = false;
            PruneBackups();
        }
        catch (Exception ex)
        {
            Log.Write($"Backup failed: {ex.Message}");
        }
    }

    private static void PruneBackups()
    {
        foreach (var old in new DirectoryInfo(BackupFolder).GetFiles("*.json")
                     .Where(f => f.LastWriteTime < DateTime.Now.AddDays(-30)))
            old.Delete();
    }

    private void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureHasEntries()) return;
        var turns = ShownTurns();
        bool filtered = TranscriptExporter.IsFiltered(_session, turns);
        Directory.CreateDirectory(_settings.OutputFolder);
        var dialog = new SaveFileDialog
        {
            Filter = TranscriptExporter.FileDialogFilter,
            InitialDirectory = _settings.OutputFolder,
            FileName = TranscriptExporter.BuildFileName(_settings.FilenamePattern, _session) + (filtered ? "_filtered" : ""),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var ext = TranscriptExporter.Formats[Math.Max(0, dialog.FilterIndex - 1)].Ext;
            var path = Path.HasExtension(dialog.FileName) ? dialog.FileName : $"{dialog.FileName}.{ext}";
            TranscriptExporter.Save(_session, path, turns: turns);
            if (!filtered) _session.HasUnsavedChanges = false;
            Log.Write($"Saved{(filtered ? $" {turns!.Count} filtered turns" : "")}: {path}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save the transcript:\n{ex.Message}", Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSaveAllClick(object sender, RoutedEventArgs e)
    {
        if (EnsureHasEntries()) SaveAll(useFilter: true);
    }

    /// <summary>Checks there is something to copy or save, taking the filters into account.</summary>
    private bool EnsureHasEntries()
    {
        string? problem = _session.Entries.Count == 0 ? "No captions have been captured yet."
            : ShownTurns() is { Count: 0 } ? "Nothing matches the current filters. Clear them to copy or save the transcript."
            : null;
        if (problem == null) return true;
        MessageBox.Show(this, problem, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    // --- Other actions ------------------------------------------------------------------------

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        PauseButton.Content = _paused ? "Resume capture" : "Pause capture";
        Log.Write(_paused ? "Capture paused." : "Capture resumed.");
    }

    private void OnNewSessionClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardUnsaved()) return;
        var title = _session.Title;
        _session = NewSession();
        _session.Title = title;
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.OutputFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.OutputFolder}\"") { UseShellExecute = true });
    }

    private async void OnDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            var dump = await Task.Run(_reader.DumpTrees);
            var path = Path.Combine(AppSettings.AppDataFolder, $"teams-ui-dump-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(path, dump, Encoding.UTF8);
            Log.Write($"UI dump written to {path}");
            MessageBox.Show(this,
                "The Teams UI tree was written to:\n" + path +
                "\n\nIt contains what's on screen in Teams (names, chat text), so review it before sharing.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Diagnostics failed:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void OnRenameSpeakerClick(object sender, RoutedEventArgs e)
    {
        if (TranscriptGrid.SelectedItem is not SpeakerTurn turn) return;
        var newName = RenameDialog.Ask(this, turn.RawSpeaker, turn.Speaker);
        if (newName == null) return;

        var oldName = turn.Speaker;
        var rawName = turn.RawSpeaker;
        if (newName.Length == 0 || newName == rawName)
            _settings.SpeakerAliases.Remove(rawName);
        else
            _settings.SpeakerAliases[rawName] = newName;
        _settings.Save();
        _session.ApplyAliases();
        _filter.RenameSpeaker(oldName, ResolveAlias(rawName));
        RefreshFilter();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var lines = TranscriptGrid.SelectedItems.Cast<SpeakerTurn>()
            .OrderBy(t => _session.Turns.IndexOf(t))
            .Select(TranscriptExporter.FormatTurn);
        CopyToClipboard(string.Join(Environment.NewLine + Environment.NewLine, lines));
    }

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureHasEntries()) return;
        var turns = ShownTurns();
        if (CopyToClipboard(TranscriptExporter.ToText(_session, turns)))
            Log.Write(TranscriptExporter.IsFiltered(_session, turns)
                ? $"Copied {turns!.Count} of {_session.Turns.Count} turns (filtered) to the clipboard."
                : $"Copied the whole transcript ({_session.Turns.Count} turns) to the clipboard.");
    }

    private bool CopyToClipboard(string text)
    {
        if (text.Length == 0) return false;
        // Another app can hold the clipboard open for a moment; retry briefly before giving up.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't copy to the clipboard:\n{ex.Message}", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    // --- Filters ------------------------------------------------------------------------------

    /// <summary>The turns the filters currently show, or null when nothing is filtered (= all turns).</summary>
    private IReadOnlyList<SpeakerTurn>? ShownTurns() =>
        _filter.IsActive && _view != null ? _view.Cast<SpeakerTurn>().ToList() : null;

    private void RefreshFilter()
    {
        _view?.Refresh();
        UpdateFilterStatus();
    }

    private void UpdateFilterStatus()
    {
        if (_session is null || _view is null) return;

        var allSpeakers = _session.Speakers.ToList();
        int visibleSpeakers = allSpeakers.Count(s => !_filter.HiddenSpeakers.Contains(s));
        SpeakersButton.Content = _filter.HiddenSpeakers.Count > 0
            ? $"Speakers ({visibleSpeakers}/{allSpeakers.Count}) ▾"
            : "Speakers ▾";

        ClearFilterButton.Visibility = _filter.IsActive ? Visibility.Visible : Visibility.Collapsed;
        if (!_filter.IsActive)
        {
            FilterStatus.Text = "";
            return;
        }

        int shown = _view.Count;
        var parts = new List<string> { $"Showing {shown} of {_session.Turns.Count} turns" };
        if (_filter.SearchText.Trim().Length > 0) parts.Add($"{shown} match{(shown == 1 ? "" : "es")}");
        if (_filter.HiddenSpeakers.Count > 0) parts.Add($"{visibleSpeakers} of {allSpeakers.Count} speakers");
        if (_filter.MarkedOnly) parts.Add("★ / highlighted only");
        FilterStatus.Text = string.Join(" · ", parts) + " — Copy all and Save use what's shown";
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleBookmarks(TranscriptGrid.SelectedItems.Cast<SpeakerTurn>().ToList());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && DetailPane.IsVisible && !SearchBox.IsKeyboardFocused)
        {
            CloseDetail();
            e.Handled = true;
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _filter.SearchText = SearchBox.Text;
        RefreshFilter();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SearchBox.Text = "";
        e.Handled = true;
    }

    private void OnSpeakersPopupOpening(object sender, RoutedEventArgs e) => BuildSpeakerList();

    private void BuildSpeakerList()
    {
        SpeakerList.Children.Clear();
        var speakers = _session.Speakers.ToList();
        if (speakers.Count == 0)
        {
            SpeakerList.Children.Add(new TextBlock { Text = "No speakers yet.", Foreground = Brushes.Gray });
            return;
        }
        foreach (var speaker in speakers)
        {
            var box = new CheckBox
            {
                Content = speaker,
                IsChecked = !_filter.HiddenSpeakers.Contains(speaker),
                Margin = new Thickness(0, 3, 0, 3),
            };
            box.Click += (_, _) =>
            {
                if (box.IsChecked == true) _filter.HiddenSpeakers.Remove(speaker);
                else _filter.HiddenSpeakers.Add(speaker);
                RefreshFilter();
            };
            SpeakerList.Children.Add(box);
        }
    }

    private void OnShowAllSpeakersClick(object sender, RoutedEventArgs e)
    {
        _filter.HiddenSpeakers.Clear();
        BuildSpeakerList();
        RefreshFilter();
    }

    private void OnHideAllSpeakersClick(object sender, RoutedEventArgs e)
    {
        foreach (var s in _session.Speakers) _filter.HiddenSpeakers.Add(s);
        BuildSpeakerList();
        RefreshFilter();
    }

    private void OnShowOnlySpeakerClick(object sender, RoutedEventArgs e)
    {
        if (TranscriptGrid.SelectedItem is not SpeakerTurn turn) return;
        _filter.ShowOnly(turn.Speaker, _session.Speakers);
        RefreshFilter();
    }

    private void OnClearFiltersClick(object sender, RoutedEventArgs e)
    {
        _filter.Clear();
        SearchBox.Text = ""; // also refreshes
        MarkedOnlyBox.IsChecked = false;
        RefreshFilter();
    }

    private void OnMarkedOnlyChanged(object sender, RoutedEventArgs e)
    {
        _filter.MarkedOnly = MarkedOnlyBox.IsChecked == true;
        RefreshFilter();
    }

    // --- Bookmarks and highlights -------------------------------------------------------------

    private SpeakerTurn? _detailTurn;

    /// <summary>Stars the given turns, or un-stars them if they're all starred already.</summary>
    private void ToggleBookmarks(IReadOnlyList<SpeakerTurn> turns)
    {
        if (turns.Count == 0) return;
        bool star = turns.Any(t => !t.IsBookmarked);
        foreach (var t in turns) t.IsBookmarked = star;
        _session.MarkChanged();
        Log.Write(star ? $"Bookmarked {turns.Count} line(s)." : $"Removed {turns.Count} bookmark(s).");
    }

    private void OnStarClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SpeakerTurn turn) ToggleBookmarks([turn]);
    }

    private void OnToggleBookmarkClick(object sender, RoutedEventArgs e) =>
        ToggleBookmarks(TranscriptGrid.SelectedItems.Cast<SpeakerTurn>().ToList());

    private void OnDetailStarClick(object sender, RoutedEventArgs e)
    {
        if (_detailTurn != null) ToggleBookmarks([_detailTurn]);
    }

    /// <summary>Shows the detail pane for a single selected row, where words can be highlighted.</summary>
    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var turn = TranscriptGrid.SelectedItems.Count == 1 ? TranscriptGrid.SelectedItem as SpeakerTurn : null;
        if (turn == _detailTurn) return;

        if (_detailTurn != null) _detailTurn.PropertyChanged -= OnDetailTurnChanged;
        _detailTurn = turn;
        if (turn != null) turn.PropertyChanged += OnDetailTurnChanged;

        DetailPane.Visibility = turn == null ? Visibility.Collapsed : Visibility.Visible;
        RefreshDetail(updateText: true);
    }

    private void OnCloseDetailClick(object sender, RoutedEventArgs e) => CloseDetail();
    private void CloseDetail() => TranscriptGrid.UnselectAll();

    private void OnDetailTurnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // While the speaker is still talking the text keeps growing; don't wipe out words the
        // user is in the middle of selecting.
        bool textChanged = e.PropertyName == nameof(SpeakerTurn.Text);
        RefreshDetail(updateText: textChanged && DetailText.SelectionLength == 0);
    }

    private void RefreshDetail(bool updateText)
    {
        if (_detailTurn == null) return;
        DetailHeader.Text = $"{_detailTurn.Speaker} · {_detailTurn.TimeDisplay}";
        DetailStarButton.Content = _detailTurn.IsBookmarked ? "★ Bookmarked" : "☆ Bookmark";
        if (updateText) DetailText.Text = _detailTurn.Text;
    }

    private void OnHighlightClick(object sender, RoutedEventArgs e)
    {
        if (_detailTurn == null) return;
        var phrase = DetailText.SelectedText.Trim();
        if (phrase.Length == 0)
        {
            MessageBox.Show(this, "Select some words in the box first, then click Highlight.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _detailTurn.AddHighlight(phrase);
        _session.MarkChanged();
        DetailText.Select(DetailText.SelectionStart + DetailText.SelectionLength, 0);
        RefreshDetail(updateText: true);
        Log.Write($"Highlighted “{phrase}”.");
    }

    private void OnRemoveHighlightClick(object sender, RoutedEventArgs e)
    {
        if (_detailTurn == null) return;
        _detailTurn.RemoveHighlights(DetailText.SelectionStart, DetailText.SelectionLength, DetailText.Text);
        _session.MarkChanged();
        DetailText.Select(DetailText.SelectionStart, 0);
        RefreshDetail(updateText: true);
    }

    // --- Settings -----------------------------------------------------------------------------

    private void LoadSettingsIntoUi()
    {
        FolderBox.Text = _settings.OutputFolder;
        PatternBox.Text = _settings.FilenamePattern;
        AutoSaveBox.IsChecked = _settings.AutoSaveOnMeetingEnd;
        AutoEnableBox.IsChecked = _settings.AutoEnableCaptions;
        AutoScrollBox.IsChecked = _settings.AutoScroll;

        foreach (var (ext, label) in TranscriptExporter.Formats)
        {
            var box = new CheckBox
            {
                Content = ext.ToUpperInvariant(),
                ToolTip = label,
                Tag = ext,
                IsChecked = _settings.AutoSaveFormats.Contains(ext, StringComparer.OrdinalIgnoreCase),
            };
            box.Click += OnSettingChanged;
            FormatsPanel.Children.Add(box);
        }
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(FolderBox.Text)) _settings.OutputFolder = FolderBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(PatternBox.Text)) _settings.FilenamePattern = PatternBox.Text.Trim();
        _settings.AutoSaveOnMeetingEnd = AutoSaveBox.IsChecked == true;
        _settings.AutoEnableCaptions = AutoEnableBox.IsChecked == true;
        _settings.AutoScroll = AutoScrollBox.IsChecked == true;
        _settings.AutoSaveFormats = FormatsPanel.Children.OfType<CheckBox>()
            .Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
        _reader.AutoEnableCaptions = _settings.AutoEnableCaptions;
        _settings.Save();
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = _settings.OutputFolder };
        if (dialog.ShowDialog(this) != true) return;
        FolderBox.Text = dialog.FolderName;
        OnSettingChanged(sender, e);
    }

    // --- Shutdown -----------------------------------------------------------------------------

    private bool ConfirmDiscardUnsaved()
    {
        if (_demoMode || !_session.HasUnsavedChanges || _session.Entries.Count == 0) return true;
        var answer = MessageBox.Show(this,
            $"The current transcript ({_session.Entries.Count} captions) hasn't been saved. Save it now?",
            Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return SaveAll(useFilter: false) != null;
        return true;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        OnSettingChanged(this, new RoutedEventArgs());
        if (!ConfirmDiscardUnsaved())
        {
            e.Cancel = true;
            return;
        }
        WriteBackup();
        _backupTimer.Stop();
        _reader.Dispose();
    }
}
