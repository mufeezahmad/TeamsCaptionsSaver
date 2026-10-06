using System.Windows;
using System.Windows.Threading;

namespace TeamsCaptionsSaver.Desktop;

public partial class App : Application
{
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write($"Unhandled error: {e.Exception}");
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to {Log.LogPath}",
            "Teams Captions Saver", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
