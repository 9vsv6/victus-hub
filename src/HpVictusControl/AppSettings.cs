using System.IO;
using System.Text.Json;

namespace HpVictusControl;

/// <summary>One game: which performance mode to switch to while it's running.</summary>
public sealed class GameProfile {
    public const string NoSwitch = "None";

    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string Mode { get; set; } = "Performance";
    public bool IsScanned { get; set; }
}

/// <summary>Small local settings file so the app remembers user preferences across restarts.</summary>
public sealed class AppSettings {

    public bool DarkTheme { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool AutoPowerSwitch { get; set; }
    public string PerformanceMode { get; set; } = "Balanced";
    public bool StartWithWindows { get; set; }
    public bool TempAlertsEnabled { get; set; }
    public double TempAlertThreshold { get; set; } = 85;
    public bool AutoFanByTemp { get; set; }
    public bool ExitOnClose { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
    public int PerformanceRefreshRateHz { get; set; }
    public int BalancedRefreshRateHz { get; set; } = 60;
    public int CoolRefreshRateHz { get; set; } = 60;
    public string LastTab { get; set; } = "Performance";
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public List<GameProfile> GameProfiles { get; set; } = new();

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
