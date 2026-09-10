using System.IO;
using System.Text.Json;

namespace HpVictusControl.Updates;

/// <summary>
/// Remembers which HP catalog updates this app has already successfully run an installer for,
/// so a later "Check for updates" doesn't show them again — some HP driver packages don't
/// actually bump the version Windows reports for the device, so a live version comparison
/// alone can't always tell the difference between "not installed" and "installed, but the
/// vendor's own installer doesn't update the driver store".
/// </summary>
public static class InstalledUpdateHistory {

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HP Victus Control", "installed_updates.json");

    private static HashSet<string>? _cache;

    public static bool IsMarkedInstalled(HpDriverUpdate update) => Load().Contains(Key(update));

    public static void MarkInstalled(HpDriverUpdate update) {
        HashSet<string> set = Load();
        if (set.Add(Key(update))) Save(set);
    }

    private static string Key(HpDriverUpdate update) => $"{update.Title}|{update.Version}".ToLowerInvariant();

    private static HashSet<string> Load() {
        if (_cache != null) return _cache;

        try {
            _cache = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(FilePath)) ?? new HashSet<string>()
                : new HashSet<string>();
        } catch {
            _cache = new HashSet<string>();
        }
        return _cache;
    }

    private static void Save(HashSet<string> set) {
        try {
            string? dir = Path.GetDirectoryName(FilePath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(set));
        } catch {
            // Best-effort persistence — not marking it isn't fatal, just means it may reappear.
        }
    }
}
