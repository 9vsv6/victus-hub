using System.IO;
using System.Text.Json;

namespace HpVictusControl.Updates;

/// <summary>What the last "Check for updates" found, and when.</summary>
public sealed class SavedUpdateScan {
    public DateTime CheckedUtc { get; set; }
    public List<HpDriverUpdate> Updates { get; set; } = new();
    /// <summary>The "you're up to date" explanation when nothing was found, e.g. Intel's own confirmation.</summary>
    public string? UpToDateNote { get; set; }
}

/// <summary>
/// Keeps the last update check on disk, so the Drivers page opens on what was found rather than an
/// empty page — checking HP, NVIDIA and Intel takes a while, and the list rarely changes day to day.
/// </summary>
public static class LastUpdateScan {

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HP Victus Control", "last_update_scan.json");

    public static void Save(List<HpDriverUpdate> updates, string? upToDateNote) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var scan = new SavedUpdateScan { CheckedUtc = DateTime.UtcNow, Updates = updates, UpToDateNote = upToDateNote };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(scan));
        } catch {
            // Only a convenience: without it the page just starts empty next time.
        }
    }

    /// <summary>
    /// The saved scan, minus anything installed through the app since — those would otherwise sit
    /// in the list until the next check.
    /// </summary>
    public static SavedUpdateScan? Load() {
        try {
            if (!File.Exists(FilePath)) return null;
            SavedUpdateScan? scan = JsonSerializer.Deserialize<SavedUpdateScan>(File.ReadAllText(FilePath));
            if (scan == null) return null;
            scan.Updates = scan.Updates.Where(update => !InstalledUpdateHistory.IsMarkedInstalled(update)).ToList();
            return scan;
        } catch {
            return null; // An unreadable file is the same as never having checked.
        }
    }
}
