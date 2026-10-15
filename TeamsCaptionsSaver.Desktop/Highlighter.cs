using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TeamsCaptionsSaver.Desktop.Transcript;

namespace TeamsCaptionsSaver.Desktop;

/// <summary>
/// Attached properties that draw a TextBlock's text with the highlighted phrases on a yellow background:
/// <c>local:Highlighter.Text="{Binding Text}" local:Highlighter.Phrases="{Binding Highlights}"</c>.
/// </summary>
public static class Highlighter
{
    private static readonly Brush HighlightBackground = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xE8, 0x6B)));
    private static readonly Brush HighlightForeground = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)));

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Highlighter), new PropertyMetadata("", OnChanged));

    public static readonly DependencyProperty PhrasesProperty = DependencyProperty.RegisterAttached(
        "Phrases", typeof(IReadOnlyList<string>), typeof(Highlighter), new PropertyMetadata(null, OnChanged));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);

    public static IReadOnlyList<string>? GetPhrases(DependencyObject d) => (IReadOnlyList<string>?)d.GetValue(PhrasesProperty);
    public static void SetPhrases(DependencyObject d, IReadOnlyList<string>? value) => d.SetValue(PhrasesProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        var text = GetText(block) ?? "";
        var phrases = GetPhrases(block) ?? [];

        block.Inlines.Clear();
        int pos = 0;
        foreach (var (start, length) in HighlightRanges.Find(text, phrases))
        {
            if (start > pos) block.Inlines.Add(new Run(text[pos..start]));
            block.Inlines.Add(new Run(text.Substring(start, length))
            {
                Background = HighlightBackground,
                Foreground = HighlightForeground, // stays readable on a selected (blue) row
            });
            pos = start + length;
        }
        if (pos < text.Length) block.Inlines.Add(new Run(text[pos..]));
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
