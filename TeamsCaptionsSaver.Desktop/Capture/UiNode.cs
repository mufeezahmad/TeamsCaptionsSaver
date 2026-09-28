namespace TeamsCaptionsSaver.Desktop.Capture;

/// <summary>
/// Immutable snapshot of one UI Automation element. Teams' WebView2 maps HTML to UIA as:
/// HTML id -> AutomationId, HTML class -> ClassName, text nodes -> Text elements whose Name is the text.
/// </summary>
public class UiNode
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string AutomationId { get; init; } = "";
    public string ControlType { get; init; } = "";
    public List<UiNode> Children { get; init; } = [];

    public bool HasClass(string cssClass) =>
        ClassName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(c => string.Equals(c, cssClass, StringComparison.Ordinal));

    public bool IdOrClassContains(string fragment) =>
        AutomationId.Contains(fragment, StringComparison.OrdinalIgnoreCase) ||
        ClassName.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    public IEnumerable<UiNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var d in child.Descendants()) yield return d;
        }
    }

    /// <summary>Visible text of this element: the Names of its text leaves, in order.</summary>
    public string InnerText()
    {
        var parts = TextLeaves().ToList();
        if (parts.Count == 0) return Name.Trim();
        return string.Join(" ", parts);
    }

    public IEnumerable<string> TextLeaves()
    {
        if (ControlType == "Text" && !string.IsNullOrWhiteSpace(Name))
        {
            // A Text element's Name already contains its children's text; don't descend.
            yield return Name.Trim();
            yield break;
        }
        foreach (var child in Children)
            foreach (var t in child.TextLeaves())
                yield return t;
    }
}
