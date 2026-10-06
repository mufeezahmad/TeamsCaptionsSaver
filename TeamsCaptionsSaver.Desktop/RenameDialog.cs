using System.Windows;
using System.Windows.Controls;

namespace TeamsCaptionsSaver.Desktop;

/// <summary>Small prompt for renaming (aliasing) a speaker.</summary>
public static class RenameDialog
{
    /// <returns>The new name, "" to clear the alias, or null if cancelled.</returns>
    public static string? Ask(Window owner, string originalName, string currentName)
    {
        var box = new TextBox { Text = currentName, Margin = new Thickness(0, 6, 0, 12), Padding = new Thickness(3) };
        var ok = new Button { Content = "OK", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Show \"{originalName}\" as (leave empty to reset):",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = "Rename speaker",
            Owner = owner,
            Content = panel,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        return window.ShowDialog() == true ? box.Text.Trim() : null;
    }
}
