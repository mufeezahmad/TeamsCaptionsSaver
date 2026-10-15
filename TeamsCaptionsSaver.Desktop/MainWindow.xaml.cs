using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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

    private TranscriptSession _session;
    private bool _sessionEnded;
    private bool _paused;
    private bool _meetingActive;
    private int _notInMeetingCount;

    public MainWindow()
    {
        InitializeComponent();
        _session = NewSession();
        LoadSettingsIntoUi();

        Log.MessageLogged += line => Dispatcher.BeginInvoke(() => LogLine.Text = line);
        _reader.AutoEnableCaptions = _settings.AutoEnableCaptions;
        _reader.SnapshotTaken += snapshot => Dispatcher.BeginInvoke(() => OnSnapshot(snapshot));
        _reader.Start();

        _backupTimer.Tick += (_, _) => WriteBackup();
        _backupTimer.Start();
    }

    private TranscriptSession NewSession()
    {
        var session = new TranscriptSession(ResolveAlias);
        TranscriptGrid.ItemsSource = session.Turns;
        _sessionEnded = false;
        return session;
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

        if (_settings.AutoSaveOnMeetingEnd && _session.Entries.Count > 0 && _session.HasUnsavedChanges)
        {
            var paths = SaveAll();
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
    }

    // --- Saving -------------------------------------------------------------------------------

    private List<string>? SaveAll()
    {
        try
        {
            var paths = TranscriptExporter.SaveAll(_session, _settings.OutputFolder, _settings.FilenamePattern,
                _settings.AutoSaveFormats.Count > 0 ? _settings.AutoSaveFormats : ["txt"]);
            _session.HasUnsavedChanges = false;
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
        if (!_session.IsDirty || _session.Entries.Count == 0) return;
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
        Directory.CreateDirectory(_settings.OutputFolder);
        var dialog = new SaveFileDialog
        {
            Filter = TranscriptExporter.FileDialogFilter,
            InitialDirectory = _settings.OutputFolder,
            FileName = TranscriptExporter.BuildFileName(_settings.FilenamePattern, _session),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var ext = TranscriptExporter.Formats[Math.Max(0, dialog.FilterIndex - 1)].Ext;
            var path = Path.HasExtension(dialog.FileName) ? dialog.FileName : $"{dialog.FileName}.{ext}";
            TranscriptExporter.Save(_session, path);
            _session.HasUnsavedChanges = false;
            Log.Write($"Saved: {path}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save the transcript:\n{ex.Message}", Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSaveAllClick(object sender, RoutedEventArgs e)
    {
        if (EnsureHasEntries()) SaveAll();
    }

    private bool EnsureHasEntries()
    {
        if (_session.Entries.Count > 0) return true;
        MessageBox.Show(this, "No captions have been captured yet.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
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

        if (newName.Length == 0 || newName == turn.RawSpeaker)
            _settings.SpeakerAliases.Remove(turn.RawSpeaker);
        else
            _settings.SpeakerAliases[turn.RawSpeaker] = newName;
        _settings.Save();
        _session.ApplyAliases();
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
        if (CopyToClipboard(TranscriptExporter.ToText(_session)))
            Log.Write($"Copied the whole transcript ({_session.Turns.Count} turns) to the clipboard.");
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
        if (!_session.HasUnsavedChanges || _session.Entries.Count == 0) return true;
        var answer = MessageBox.Show(this,
            $"The current transcript ({_session.Entries.Count} captions) hasn't been saved. Save it now?",
            Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return SaveAll() != null;
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
