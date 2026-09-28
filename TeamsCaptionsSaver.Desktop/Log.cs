using System.IO;
using TeamsCaptionsSaver.Desktop.Models;

namespace TeamsCaptionsSaver.Desktop;

/// <summary>Minimal file logger at %LOCALAPPDATA%\TeamsCaptionsSaver\log.txt.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string LogPath { get; } = Path.Combine(AppSettings.AppDataFolder, "log.txt");

    public static event Action<string>? MessageLogged;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.AppDataFolder);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 2_000_000) info.Delete();
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
        MessageLogged?.Invoke(line);
    }
}
