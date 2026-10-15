using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using TeamsCaptionsSaver.Desktop.Models;

namespace TeamsCaptionsSaver.Desktop.Capture;

public sealed record CaptureSnapshot(
    bool TeamsRunning,
    bool InMeeting,
    bool CaptionsVisible,
    string MeetingTitle,
    IReadOnlyList<ParsedCaption> Captions);

/// <summary>
/// Reads live captions from the Microsoft Teams desktop client (new Teams, ms-teams.exe) through
/// Windows UI Automation. Teams renders its UI in WebView2, which exposes the page's accessibility
/// tree to UIA - so we can see the caption elements of the Teams page,
/// including the real speaker names Teams attaches to each caption.
/// </summary>
public sealed class TeamsCaptionReader : IDisposable
{
    private const string HangupButtonId = "hangup-button";
    private const string MoreButtonId = "callingButtons-showMoreBtn";
    private const string LanguageSpeechMenuId = "LanguageSpeechMenuControl-id";
    private const string CaptionsMenuItemId = "closed-captions-button";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan WindowRefreshInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FullScanInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleFullScanInterval = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan AutoEnableDelay = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan AutoEnableRetry = TimeSpan.FromSeconds(20);
    private const int MaxAutoEnableAttempts = 2;

    private readonly CacheRequest _subtreeRequest;
    private CancellationTokenSource? _cts;
    private Thread? _thread;

    private List<(IntPtr Hwnd, AutomationElement Root, string WindowTitle)> _documents = [];
    private DateTime _lastWindowRefresh = DateTime.MinValue;
    private DateTime _lastFullScan = DateTime.MinValue;

    private AutomationElement? _hangupButton;
    private AutomationElement? _captionsContainer;
    private AutomationElement? _meetingDocument;
    private string _meetingTitle = "";

    private bool _wasInMeeting;
    private DateTime _meetingDetectedAt;
    private int _autoEnableAttempts;
    private DateTime _lastAutoEnableAttempt = DateTime.MinValue;

    public bool AutoEnableCaptions { get; set; }

    /// <summary>Raised on the background capture thread roughly once per second.</summary>
    public event Action<CaptureSnapshot>? SnapshotTaken;

    public TeamsCaptionReader()
    {
        _subtreeRequest = new CacheRequest
        {
            TreeScope = TreeScope.Subtree,
            TreeFilter = Automation.RawViewCondition,
            AutomationElementMode = AutomationElementMode.Full,
        };
        _subtreeRequest.Add(AutomationElement.NameProperty);
        _subtreeRequest.Add(AutomationElement.ClassNameProperty);
        _subtreeRequest.Add(AutomationElement.AutomationIdProperty);
        _subtreeRequest.Add(AutomationElement.ControlTypeProperty);
        _subtreeRequest.Add(AutomationElement.RuntimeIdProperty);
    }

    public void Start()
    {
        if (_thread != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Run(token)) { IsBackground = true, Name = "Teams caption reader" };
        // UIA clients should run on an MTA thread.
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _thread = null;
        _cts = null;
    }

    public void Dispose() => Stop();

    private void Run(CancellationToken token)
    {
        Log.Write("Caption reader started.");
        while (!token.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Log.Write($"Capture tick failed: {ex.GetType().Name}: {ex.Message}");
                ResetMeetingElements();
            }
            var elapsed = DateTime.UtcNow - started;
            var wait = PollInterval - elapsed;
            if (wait > TimeSpan.Zero) token.WaitHandle.WaitOne(wait);
        }
        Log.Write("Caption reader stopped.");
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        if (now - _lastWindowRefresh > WindowRefreshInterval)
        {
            _documents = FindTeamsDocuments();
            _lastWindowRefresh = now;
        }

        if (_documents.Count == 0)
        {
            ResetMeetingElements();
            UpdateMeetingState(false, now);
            SnapshotTaken?.Invoke(new CaptureSnapshot(IsTeamsRunning(), false, false, "", []));
            return;
        }

        if (_hangupButton != null && !IsAlive(_hangupButton)) _hangupButton = null;

        // A full scan is only needed while we're still looking for the meeting controls or the captions.
        var scanInterval = _wasInMeeting ? FullScanInterval : IdleFullScanInterval;
        if ((_hangupButton == null || _captionsContainer == null) && now - _lastFullScan > scanInterval)
        {
            FullScan();
            _lastFullScan = now;
        }

        IReadOnlyList<ParsedCaption> captions = [];
        if (_captionsContainer != null)
        {
            try
            {
                var cached = _captionsContainer.GetUpdatedCache(_subtreeRequest);
                captions = CaptionParser.Parse(ToNode(cached));
            }
            catch (ElementNotAvailableException)
            {
                Log.Write("Captions region disappeared (captions turned off or meeting ended).");
                _captionsContainer = null;
            }
        }

        bool inMeeting = _hangupButton != null || _captionsContainer != null;
        UpdateMeetingState(inMeeting, now);

        if (inMeeting && _captionsContainer == null && AutoEnableCaptions)
            MaybeAutoEnableCaptions(now);

        SnapshotTaken?.Invoke(new CaptureSnapshot(true, inMeeting, _captionsContainer != null,
            inMeeting ? _meetingTitle : "", captions));
    }

    private void UpdateMeetingState(bool inMeeting, DateTime now)
    {
        if (inMeeting && !_wasInMeeting)
        {
            _meetingDetectedAt = now;
            _autoEnableAttempts = 0;
            Log.Write($"Meeting detected: '{_meetingTitle}'.");
        }
        else if (!inMeeting && _wasInMeeting)
        {
            Log.Write("Meeting ended.");
        }
        _wasInMeeting = inMeeting;
    }

    private void ResetMeetingElements()
    {
        _hangupButton = null;
        _captionsContainer = null;
        _meetingDocument = null;
    }

    // --- Locating Teams ----------------------------------------------------------------------

    private static bool IsTeamsRunning() => Process.GetProcessesByName("ms-teams").Length > 0;

    /// <summary>Finds the WebView2 render hosts inside every top-level Teams window.</summary>
    private static List<(IntPtr, AutomationElement, string)> FindTeamsDocuments()
    {
        var teamsPids = Process.GetProcessesByName("ms-teams").Select(p => (uint)p.Id).ToHashSet();
        var results = new List<(IntPtr, AutomationElement, string)>();
        if (teamsPids.Count == 0) return results;

        foreach (var top in Native.TopLevelWindows())
        {
            Native.GetWindowThreadProcessId(top, out var pid);
            if (!teamsPids.Contains(pid) || !Native.IsWindowVisible(top)) continue;
            var title = Native.GetTitle(top);

            foreach (var child in Native.ChildWindows(top))
            {
                if (Native.GetClass(child) != "Chrome_RenderWidgetHostHWND") continue;
                try
                {
                    results.Add((child, AutomationElement.FromHandle(child), title));
                }
                catch (Exception ex)
                {
                    Log.Write($"Could not attach to Teams window '{title}': {ex.Message}");
                }
            }
        }
        return results;
    }

    /// <summary>Scans every Teams document for the Leave button and the live-captions region.</summary>
    private void FullScan()
    {
        foreach (var (_, root, windowTitle) in _documents)
        {
            UiNode tree;
            try
            {
                tree = ToNode(root.GetUpdatedCache(_subtreeRequest));
            }
            catch (ElementNotAvailableException)
            {
                _lastWindowRefresh = DateTime.MinValue;
                continue;
            }

            var hangup = FindHangup(tree);
            var container = FindCaptionsContainer(tree, inMeeting: hangup != null || _hangupButton != null);

            if (hangup != null)
            {
                _hangupButton = (AutomationElement?)hangup.Source;
                _meetingDocument = root;
                _meetingTitle = CleanTitle(windowTitle);
            }
            if (container != null && _captionsContainer == null)
            {
                _captionsContainer = (AutomationElement?)container.Source;
                _meetingDocument ??= root;
                if (_meetingTitle.Length == 0) _meetingTitle = CleanTitle(windowTitle);
                Log.Write($"Captions region found (name='{container.Name}', id='{container.AutomationId}').");
            }
        }
    }

    private static SourceNode? FindHangup(UiNode tree) =>
        tree.Descendants().OfType<SourceNode>().FirstOrDefault(n =>
            n.AutomationId == HangupButtonId ||
            (n.ControlType == "Button" &&
             (n.Name.Equals("Leave", StringComparison.OrdinalIgnoreCase) ||
              n.Name.StartsWith("Leave (", StringComparison.OrdinalIgnoreCase))));

    /// <summary>
    /// Finds the element that holds the caption items. Prefers an ancestor whose name/id/class
    /// mentions "caption" (in current Teams, the "Live Captions" panel); otherwise, while
    /// in a meeting, falls back to the lowest common ancestor of the caption items.
    /// </summary>
    internal static SourceNode? FindCaptionsContainer(UiNode tree, bool inMeeting)
    {
        var itemPaths = new List<List<UiNode>>();
        CollectItemPaths(tree, [], itemPaths);
        if (itemPaths.Count == 0)
        {
            // Captions are on but nobody has spoken yet: the panel exists with no caption items.
            return tree.Descendants().OfType<SourceNode>().FirstOrDefault(n =>
                n.Name.Equals("Live Captions", StringComparison.OrdinalIgnoreCase));
        }

        foreach (var path in itemPaths)
        {
            // Walk upward from the item (skipping the document root, whose name is the window title).
            for (int i = path.Count - 2; i >= 1; i--)
            {
                var n = path[i];
                if (n.Name.Contains("caption", StringComparison.OrdinalIgnoreCase) ||
                    n.IdOrClassContains("caption"))
                    return n as SourceNode;
            }
        }

        if (!inMeeting) return null;

        // Lowest common ancestor of all items.
        int depth = 0;
        while (itemPaths.All(p => p.Count > depth + 1 && p[depth] == itemPaths[0][depth])) depth++;
        var lca = depth > 0 ? itemPaths[0][depth - 1] : tree;

        // Don't mistake a (compact-density) chat message list for captions.
        bool looksLikeChat = itemPaths[0].Take(depth).Skip(1)
            .Any(n => n.Name.Contains("chat", StringComparison.OrdinalIgnoreCase) ||
                      n.Name.Contains("message", StringComparison.OrdinalIgnoreCase));
        return looksLikeChat ? null : lca as SourceNode;
    }

    private static void CollectItemPaths(UiNode node, List<UiNode> ancestors, List<List<UiNode>> results)
    {
        var path = new List<UiNode>(ancestors) { node };
        if (CaptionParser.IsCaptionItem(node))
        {
            results.Add(path);
            return;
        }
        foreach (var child in node.Children) CollectItemPaths(child, path, results);
    }

    private static string CleanTitle(string windowTitle)
    {
        var first = windowTitle.Split(" | ")[0].Trim();
        if (first.Length == 0 || first.Equals("Microsoft Teams", StringComparison.OrdinalIgnoreCase))
            return "Teams Meeting";
        return first;
    }

    // --- Auto-enable captions -----------------------------------------------------------------

    private void MaybeAutoEnableCaptions(DateTime now)
    {
        if (_meetingDocument == null || _autoEnableAttempts >= MaxAutoEnableAttempts) return;
        if (now - _meetingDetectedAt < AutoEnableDelay || now - _lastAutoEnableAttempt < AutoEnableRetry) return;

        _autoEnableAttempts++;
        _lastAutoEnableAttempt = now;
        Log.Write($"Trying to turn on live captions (attempt {_autoEnableAttempts}).");
        try
        {
            var doc = _meetingDocument;
            var more = FindById(doc, MoreButtonId);
            if (more == null) { Log.Write("Auto-enable: 'More' button not found."); return; }
            Activate(more);

            var language = FindById(doc, LanguageSpeechMenuId, waitForIt: true);
            if (language == null) { Log.Write("Auto-enable: 'Language and speech' not found."); CloseMenu(more); return; }
            Activate(language);

            // "Show live captions" is a checkbox in current Teams (older builds: "Turn on/off live captions").
            // Never toggle it blindly - a second attempt would turn the captions back off.
            var captions = FindById(doc, CaptionsMenuItemId, waitForIt: true);
            if (captions == null) { Log.Write("Auto-enable: 'Show live captions' not found."); CloseMenu(more); return; }
            if (IsCaptionsOn(captions))
            {
                Log.Write($"Auto-enable: live captions are already on ('{captions.Current.Name}').");
                _autoEnableAttempts = MaxAutoEnableAttempts;
                CloseMenu(more);
                return;
            }
            Activate(captions);
            Thread.Sleep(500);
            CloseMenu(more);
            Log.Write($"Auto-enable: turned on '{captions.Current.Name}'.");
            _lastFullScan = DateTime.MinValue; // look for the captions region right away
        }
        catch (Exception ex)
        {
            Log.Write($"Auto-enable failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <param name="waitForIt">Retry for up to ~2 s, for menu items that are still opening.</param>
    private static AutomationElement? FindById(AutomationElement root, string id, bool waitForIt = false)
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, id);
        for (int attempt = 0; ; attempt++)
        {
            var found = root.FindFirst(TreeScope.Descendants, condition);
            if (found != null || !waitForIt || attempt >= 10) return found;
            Thread.Sleep(200);
        }
    }

    private static bool IsCaptionsOn(AutomationElement captionsItem)
    {
        if (captionsItem.TryGetCurrentPattern(TogglePattern.Pattern, out var tg))
            return ((TogglePattern)tg).Current.ToggleState == ToggleState.On;
        return captionsItem.Current.Name.Contains("off", StringComparison.OrdinalIgnoreCase);
    }

    private static void Activate(AutomationElement e)
    {
        if (e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec) &&
            ((ExpandCollapsePattern)ec).Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
        {
            ((ExpandCollapsePattern)ec).Expand();
            return;
        }
        if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
        {
            ((InvokePattern)inv).Invoke();
            return;
        }
        if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var tg))
        {
            ((TogglePattern)tg).Toggle();
            return;
        }
        throw new InvalidOperationException($"Element '{e.Current.Name}' can't be clicked via UI Automation.");
    }

    private static void CloseMenu(AutomationElement more)
    {
        try
        {
            if (more.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec) &&
                ((ExpandCollapsePattern)ec).Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                ((ExpandCollapsePattern)ec).Collapse();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not close the More menu: {ex.Message}");
        }
    }

    // --- Diagnostics --------------------------------------------------------------------------

    /// <summary>Text dump of every Teams UI tree, used to adapt the parser if Teams changes its markup.</summary>
    public string DumpTrees()
    {
        var sb = new StringBuilder();
        var docs = FindTeamsDocuments();
        sb.AppendLine($"Teams Captions Saver UI dump - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Teams running: {IsTeamsRunning()}, documents: {docs.Count}");
        foreach (var (hwnd, root, title) in docs)
        {
            sb.AppendLine();
            sb.AppendLine($"=== Window '{title}' (hwnd {hwnd}) ===");
            try
            {
                var tree = ToNode(root.GetUpdatedCache(_subtreeRequest));
                var hangupFound = FindHangup(tree) != null;
                var container = FindCaptionsContainer(tree, hangupFound);
                sb.AppendLine($"Leave button found: {hangupFound}; captions region: " +
                              (container == null ? "not found" : $"'{container.Name}' id='{container.AutomationId}'"));
                if (container != null)
                    foreach (var c in CaptionParser.Parse(container))
                        sb.AppendLine($"  parsed -> {c.Speaker}: {c.Text}");
                DumpNode(sb, tree, 0);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(failed: {ex.Message})");
            }
        }
        return sb.ToString();
    }

    private static void DumpNode(StringBuilder sb, UiNode n, int depth)
    {
        var name = n.Name.Length > 120 ? n.Name[..120] + "…" : n.Name;
        var cls = n.ClassName.Length > 80 ? n.ClassName[..80] + "…" : n.ClassName;
        sb.Append(' ', depth * 2)
          .Append('[').Append(n.ControlType).Append(']')
          .Append(name.Length > 0 ? $" name=\"{name.ReplaceLineEndings(" ")}\"" : "")
          .Append(n.AutomationId.Length > 0 ? $" id=\"{n.AutomationId}\"" : "")
          .Append(cls.Length > 0 ? $" class=\"{cls}\"" : "")
          .AppendLine();
        foreach (var c in n.Children) DumpNode(sb, c, depth + 1);
    }

    // --- UIA -> UiNode ------------------------------------------------------------------------

    /// <summary>UiNode that remembers the live UIA element it was built from.</summary>
    internal sealed class SourceNode : UiNode
    {
        public object? Source { get; init; }
    }

    private static UiNode ToNode(AutomationElement e)
    {
        var children = new List<UiNode>();
        AutomationElementCollection? cachedChildren = null;
        try { cachedChildren = e.CachedChildren; } catch (InvalidOperationException) { }
        if (cachedChildren != null)
            foreach (AutomationElement child in cachedChildren)
                children.Add(ToNode(child));

        var runtimeId = e.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty) as int[];
        var controlType = e.GetCachedPropertyValue(AutomationElement.ControlTypeProperty) as ControlType;
        return new SourceNode
        {
            Source = e,
            Key = runtimeId == null ? Guid.NewGuid().ToString("N") : string.Join(".", runtimeId),
            Name = e.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? "",
            ClassName = e.GetCachedPropertyValue(AutomationElement.ClassNameProperty) as string ?? "",
            AutomationId = e.GetCachedPropertyValue(AutomationElement.AutomationIdProperty) as string ?? "",
            ControlType = controlType?.ProgrammaticName.Replace("ControlType.", "") ?? "",
            Children = children,
        };
    }

    private static bool IsAlive(AutomationElement e)
    {
        try
        {
            _ = e.Current.ControlType;
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }
}
