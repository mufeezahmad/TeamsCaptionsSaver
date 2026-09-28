using System.IO;
using System.Text.Json;

namespace TeamsCaptionsSaver.Desktop.Models;

public sealed class AppSettings
{
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Teams Transcripts");

    /// <summary>Variables: {date} {time} {title}</summary>
    public string FilenamePattern { get; set; } = "{date}_{time}_{title}";

    public bool AutoSaveOnMeetingEnd { get; set; } = true;
    public List<string> AutoSaveFormats { get; set; } = ["txt", "md", "json"];

    /// <summary>Open More &gt; Language and speech &gt; Turn on live captions when a meeting starts.</summary>
    public bool AutoEnableCaptions { get; set; } = true;

    public bool AutoScroll { get; set; } = true;

    public Dictionary<string, string> SpeakerAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string AppDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeamsCaptionsSaver");

    private static string SettingsPath => Path.Combine(AppDataFolder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
                if (loaded != null)
                {
                    // Deserialization replaces the dictionary, losing the case-insensitive comparer.
                    loaded.SpeakerAliases = new(loaded.SpeakerAliases, StringComparer.OrdinalIgnoreCase);
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to load settings: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to save settings: {ex.Message}");
        }
    }
}
