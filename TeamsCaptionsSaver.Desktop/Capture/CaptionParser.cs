using TeamsCaptionsSaver.Desktop.Models;

namespace TeamsCaptionsSaver.Desktop.Capture;

/// <summary>
/// Turns a snapshot of the Teams live-captions region into (speaker, text) pairs.
/// In the browser each caption is a <c>.fui-ChatMessageCompact</c> element. The Teams desktop client's
/// accessibility tree drops that wrapper and only exposes its <c>fui-ChatMessageCompact__body</c>,
/// which holds two text elements: the speaker's name, then the caption text.
/// </summary>
public static class CaptionParser
{
    public const string CaptionItemClass = "fui-ChatMessageCompact";
    public const string CaptionBodyClass = "fui-ChatMessageCompact__body";

    public static bool IsCaptionItem(UiNode node) =>
        node.HasClass(CaptionItemClass) || node.HasClass(CaptionBodyClass);

    public static List<ParsedCaption> Parse(UiNode captionsRoot)
    {
        var results = new List<ParsedCaption>();
        foreach (var item in FindItems(captionsRoot))
        {
            var parsed = ParseItem(item);
            if (parsed != null) results.Add(parsed);
        }
        return results;
    }

    /// <summary>Caption item nodes, outermost first; does not descend into an item.</summary>
    public static IEnumerable<UiNode> FindItems(UiNode root)
    {
        if (IsCaptionItem(root))
        {
            yield return root;
            yield break;
        }
        foreach (var child in root.Children)
            foreach (var item in FindItems(child))
                yield return item;
    }

    public static ParsedCaption? ParseItem(UiNode item)
    {
        var authorNode = item.Descendants().FirstOrDefault(n => n.IdOrClassContains("author"));
        var textNode = item.Descendants().FirstOrDefault(n =>
            n.IdOrClassContains("caption-text") || n.IdOrClassContains("captiontext"));

        string speaker = authorNode?.InnerText() ?? "";
        string text = textNode?.InnerText() ?? "";

        if (speaker.Length == 0 || text.Length == 0)
        {
            // Fallback: the first visible text is the speaker, the rest is what they said.
            var leaves = item.TextLeaves().Where(t => t.Length > 0).ToList();
            if (speaker.Length == 0 && leaves.Count > 0)
            {
                speaker = leaves[0];
            }
            if (text.Length == 0)
            {
                // Skip only the speaker's own leaf, so a caption that repeats the name isn't emptied.
                var remaining = leaves.Count > 0 && leaves[0] == speaker ? leaves.Skip(1) : leaves;
                text = string.Join(" ", remaining);
            }
        }

        speaker = Normalize(speaker);
        text = Normalize(text);
        if (text.Length == 0) return null;
        if (speaker.Length == 0) speaker = "Unknown";

        return new ParsedCaption(item.Key, speaker, text);
    }

    private static string Normalize(string s) =>
        string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
