using TeamsCaptionsSaver.Desktop.Capture;
using TeamsCaptionsSaver.Desktop.Models;
using TeamsCaptionsSaver.Desktop.Transcript;
using Xunit;
using Node = TeamsCaptionsSaver.Desktop.Capture.TeamsCaptionReader.SourceNode;

namespace TeamsCaptionsSaver.Tests;

public class CaptureTests
{
    private static int _nextKey;

    private static Node N(string type, string name = "", string cls = "", string id = "", params UiNode[] children) =>
        new() { Key = $"k{_nextKey++}", ControlType = type, Name = name, ClassName = cls, AutomationId = id, Children = children.ToList() };

    private static Node Text(string text) => N("Text", text);

    private static Node Caption(string speaker, string text, string key) => new()
    {
        Key = key,
        ControlType = "Group",
        ClassName = "fui-ChatMessageCompact ___1abc f22iagw",
        Children =
        [
            N("Image", speaker, "fui-Avatar"),
            N("Group", "", "fui-ChatMessageCompact__body", "",
                Text(speaker),
                N("Group", "", "", "", Text(text))),
        ],
    };

    private static Node MeetingDocument(string regionName, params UiNode[] captions) =>
        N("Document", "Weekly sync | Microsoft Teams", "", "RootWebArea",
            N("Group", "", "fui-AppLayout", "app",
                N("Button", "Leave (Ctrl+Shift+H)", "fui-Button", "hangup-button"),
                N("Group", regionName, "stage-layout", "",
                    N("List", "", "", "", captions))));

    [Fact]
    public void Parses_speaker_and_text_from_caption_item()
    {
        var parsed = CaptionParser.Parse(N("Group", "", "", "", Caption("Jane Doe", "Hello everyone, let's start.", "c1")));

        var caption = Assert.Single(parsed);
        Assert.Equal("Jane Doe", caption.Speaker);
        Assert.Equal("Hello everyone, let's start.", caption.Text);
        Assert.Equal("c1", caption.Key);
    }

    [Fact]
    public void Uses_author_and_caption_text_elements_when_present()
    {
        var item = new Node
        {
            Key = "c1",
            ControlType = "Group",
            ClassName = "fui-ChatMessageCompact",
            Children =
            [
                N("Text", "", "ui-author", "", Text("John Smith")),
                N("Text", "", "closed-caption-text", "", Text("We shipped it.")),
            ],
        };
        var caption = Assert.Single(CaptionParser.Parse(item));
        Assert.Equal("John Smith", caption.Speaker);
        Assert.Equal("We shipped it.", caption.Text);
    }

    /// <summary>
    /// Structure observed in the new Teams desktop client (Oct 2026): the fui-ChatMessageCompact wrapper
    /// is not in the accessibility tree, only its __body with [speaker, text] Text children.
    /// </summary>
    [Fact]
    public void Parses_real_desktop_client_structure()
    {
        Node Body(string key, string speaker, string text) => new()
        {
            Key = key, ControlType = "Group", ClassName = "fui-ChatMessageCompact__body ___abc f1x2y3z",
            Children = [Text(speaker), Text(text)],
        };
        var doc = N("Document", "Weekly sync | Microsoft Teams", "", "RootWebArea",
            N("Button", "Leave", "fui-Button", "hangup-button"),
            N("Group", "Live Captions", "fui-Flex", "",
                N("Group", "", "fui-Flex", "",
                    N("Group", "", "ui-box", "",
                        N("Group", "", "fui-Flex", "",
                            N("Group"),
                            N("Group", "", "fui-Primitive"),
                            N("Group", "", "lpcCommonWeb-hoverTarget cw-container-103"),
                            Body("b1", "Alex Johnson", "Let's review the release checklist."),
                            N("Group", "", "lpcCommonWeb-hoverTarget cw-container-103"),
                            Body("b2", "Sam Lee", "Sounds good to me")))),
                N("Group", "", "fui-Flex", "",
                    N("Button", "Hide live captions (Alt+Shift+C)", "fui-Button", "captions-panel-dismiss-button"))));

        var container = TeamsCaptionReader.FindCaptionsContainer(doc, inMeeting: true);
        Assert.NotNull(container);
        Assert.Equal("Live Captions", container!.Name);

        var captions = CaptionParser.Parse(container);
        Assert.Equal(2, captions.Count);
        Assert.Equal(("Alex Johnson", "Let's review the release checklist.", "b1"),
            (captions[0].Speaker, captions[0].Text, captions[0].Key));
        Assert.Equal(("Sam Lee", "Sounds good to me"), (captions[1].Speaker, captions[1].Text));
    }

    [Fact]
    public void Skips_items_without_text()
    {
        var item = new Node { Key = "c1", ControlType = "Group", ClassName = "fui-ChatMessageCompact", Children = [Text("Jane Doe")] };
        Assert.Empty(CaptionParser.Parse(item));
    }

    [Fact]
    public void Finds_container_by_caption_name()
    {
        var doc = MeetingDocument("Live Captions", Caption("Jane", "Hi", "c1"), Caption("Bob", "Hey", "c2"));
        var container = TeamsCaptionReader.FindCaptionsContainer(doc, inMeeting: false);

        Assert.NotNull(container);
        Assert.Equal("Live Captions", container!.Name);
        Assert.Equal(2, CaptionParser.Parse(container).Count);
    }

    [Fact]
    public void Falls_back_to_common_ancestor_in_meeting()
    {
        var doc = MeetingDocument("", Caption("Jane", "Hi", "c1"), Caption("Bob", "Hey", "c2"));

        Assert.Null(TeamsCaptionReader.FindCaptionsContainer(doc, inMeeting: false));
        var container = TeamsCaptionReader.FindCaptionsContainer(doc, inMeeting: true);
        Assert.NotNull(container);
        Assert.Equal("List", container!.ControlType);
    }

    [Fact]
    public void Ignores_compact_chat_messages()
    {
        var doc = N("Document", "Chat | Microsoft Teams", "", "RootWebArea",
            N("List", "Chat messages", "", "", Caption("Jane", "Lunch?", "c1"), Caption("Bob", "Sure", "c2")));
        Assert.Null(TeamsCaptionReader.FindCaptionsContainer(doc, inMeeting: true));
    }

    private static TranscriptSession Session() => new(s => s == "Jane Doe" ? "Jane" : s);

    [Fact]
    public void Merge_updates_captions_in_place_and_appends_new_ones()
    {
        var s = Session();
        s.Merge([new ParsedCaption("c1", "Jane Doe", "Hello")]);
        s.Merge([new ParsedCaption("c1", "Jane Doe", "Hello everyone"), new ParsedCaption("c2", "Bob", "Hi")]);

        Assert.Equal(2, s.Entries.Count);
        Assert.Equal("Hello everyone", s.Entries[0].Text);
        Assert.Equal("Jane", s.Entries[0].Speaker);
        Assert.Equal("Jane Doe", s.Entries[0].RawSpeaker);
        Assert.True(s.HasUnsavedChanges);
    }

    [Fact]
    public void Merge_reattaches_rerendered_caption_instead_of_duplicating()
    {
        var s = Session();
        const string text = "Let's go over the release plan for next week.";
        s.Merge([new ParsedCaption("c1", "Bob", text)]);
        s.Merge([new ParsedCaption("c9", "Bob", text)]);

        Assert.Single(s.Entries);
    }

    [Fact]
    public void Merge_keeps_repeated_short_captions()
    {
        var s = Session();
        s.Merge([new ParsedCaption("c1", "Bob", "Okay.")]);
        s.Merge([new ParsedCaption("c2", "Bob", "Okay.")]);

        Assert.Equal(2, s.Entries.Count);
    }

    [Fact]
    public void Exports_contain_speakers_and_text()
    {
        var s = Session();
        s.Title = "Weekly sync";
        s.Merge([new ParsedCaption("c1", "Jane Doe", "Hello, \"team\""), new ParsedCaption("c2", "Bob", "Hi")]);

        Assert.Contains("Jane: Hello, \"team\"", TranscriptExporter.ToText(s));
        Assert.Contains("\"Jane\",\"Hello, \"\"team\"\"\"", TranscriptExporter.ToCsv(s));
        Assert.Contains("<v Bob>Hi", TranscriptExporter.ToVtt(s));
        Assert.Contains("\"originalSpeaker\": \"Jane Doe\"", TranscriptExporter.ToJson(s));
        Assert.Contains("**Jane**", TranscriptExporter.ToMarkdown(s));
        Assert.Equal("Weekly sync", TranscriptExporter.BuildFileName("{title}", s));
        Assert.Equal("a_b", TranscriptExporter.BuildFileName("a/b", s));
    }
}
