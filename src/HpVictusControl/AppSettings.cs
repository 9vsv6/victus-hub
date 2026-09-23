using System.IO;
using System.Text.Json;

namespace HpVictusControl;

/// <summary>One game: which performance mode to switch to while it's running.</summary>
public sealed class GameProfile {
    public const string NoSwitch = "None";

    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string Mode { get; set; } = "Performance";
    public int RefreshRateHz { get; set; }
    public bool MaxFan { get; set; }
    public bool Boost { get; set; }
    // 0 × 0 = leave the resolution alone.
    public int ResolutionWidth { get; set; }
    public int ResolutionHeight { get; set; }
    public bool IsScanned { get; set; }
}

/// <summary>Small local settings file so the app remembers user preferences across restarts.</summary>
public sealed class AppSettings {

    public bool DarkTheme { get; set; }
    public bool MatchWindowsTheme { get; set; }
    // Interface language: "en" or "ar".
    public string Language { get; set; } = "en";
    // 0 = scale the interface with the window; otherwise a fixed factor (1.15 = 115%).
    public double InterfaceScale { get; set; }
    public bool TuneWifiForGames { get; set; }
    public bool AutoAddGames { get; set; } = true;
    public bool AutoCleanDriverDownloads { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool AutoPowerSwitch { get; set; }
    public string PerformanceMode { get; set; } = "Balanced";
    public bool StartWithWindows { get; set; }
    public bool TempAlertsEnabled { get; set; }
    public double TempAlertThreshold { get; set; } = 85;
    public bool AutoFanByTemp { get; set; }
    public bool ExitOnClose { get; set; }
    // GPU power as last chosen in the app; null = never changed here, so the BIOS's own setting stands.
    public bool? GpuCustomTgp { get; set; }
    public bool? GpuDynamicBoost { get; set; }
    public bool IdleCoolEnabled { get; set; }
    public int IdleCoolMinutes { get; set; } = 5;
    // 0 = leave the keyboard backlight on however long the laptop sits idle.
    public int KeyboardBacklightTimeoutSeconds { get; set; }
    // Set once, the first time the tray icon is pulled out of Windows' hidden-icons overflow.
    public bool TrayIconPromoted { get; set; }
    public int? SpeakerIdleSecondsBeforeKeepAwake { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
    public DateTime? LastAppUpdateCheckUtc { get; set; }
    public bool WeeklyDriverCheck { get; set; } = true;
    // Asked once whether to install into Program Files; after that it's in Settings → About.
    public bool InstallOffered { get; set; }
    // The version whose "What's new" was last dismissed; null = never shown.
    public string? LastSeenVersion { get; set; }
    public int PerformanceRefreshRateHz { get; set; }
    public int BalancedRefreshRateHz { get; set; } = 60;
    public int CoolRefreshRateHz { get; set; } = 60;
    // Global shortcuts as text ("Ctrl+Alt+F12"); an empty string turns that shortcut off.
    public string CycleModeHotkey { get; set; } = "Ctrl+Alt+F12";
    public string MaxFanHotkey { get; set; } = "Ctrl+Alt+F11";
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public List<GameProfile> GameProfiles { get; set; } = new();
    // Typical battery draw per performance mode ("Balanced" → average mW), learned on battery.
    public Dictionary<string, ModeDrain> BatteryDrainByMode { get; set; } = new();

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
