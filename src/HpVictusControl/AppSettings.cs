using System.IO;
using System.Text.Json;

namespace HpVictusControl;

/// <summary>Small local settings file so the app remembers user preferences across restarts.</summary>
public sealed class AppSettings {

    public bool DarkTheme { get; set; }
    public bool AutoPowerSwitch { get; set; }
    public string PerformanceMode { get; set; } = "Balanced";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HP Victus Control", "settings.json");

    public static AppSettings Load() {
        try {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        } catch {
            // Fall through to defaults below.
        }
        return new AppSettings();
    }

    public void Save() {
        try {
            string? dir = Path.GetDirectoryName(FilePath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        } catch {
            // Best-effort — not persisting isn't fatal, settings just won't stick this time.
        }
    }
}
