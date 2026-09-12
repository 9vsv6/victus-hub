using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HpVictusControl.Games;

/// <summary>
/// Finds installed games from the two most common PC launchers so the user can pick from a
/// list instead of hunting down .exe paths by hand. Steam has to guess the main executable
/// (its manifests don't record one) via a "largest non-utility .exe" heuristic; Epic's
/// manifests record the real launch executable directly, so that one is exact.
/// </summary>
public static class GameDetector {

    public static List<DetectedGame> DetectInstalledGames() {
        var games = new List<DetectedGame>();
        try { games.AddRange(DetectSteamGames()); } catch { /* best-effort */ }
        try { games.AddRange(DetectEpicGames()); } catch { /* best-effort */ }

        return games
            .GroupBy(g => g.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<DetectedGame> DetectEpicGames() {
        var results = new List<DetectedGame>();
        const string manifestDir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        if (!Directory.Exists(manifestDir)) return results;

        foreach (string file in Directory.GetFiles(manifestDir, "*.item")) {
            try {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement root = doc.RootElement;

                string name = root.TryGetProperty("DisplayName", out JsonElement n) ? n.GetString() ?? "" : "";
                string installLocation = root.TryGetProperty("InstallLocation", out JsonElement il) ? il.GetString() ?? "" : "";
                string launchExe = root.TryGetProperty("LaunchExecutable", out JsonElement le) ? le.GetString() ?? "" : "";
                if (name.Length == 0 || installLocation.Length == 0 || launchExe.Length == 0) continue;

                string fullPath = PathNormalizer.Normalize(Path.Combine(installLocation, launchExe));
                if (File.Exists(fullPath)) results.Add(new DetectedGame(name, fullPath));
            } catch {
                // Skip a malformed/unreadable manifest.
            }
        }
        return results;
    }

    private static readonly string[] NonGameExeHints = {
        "unins", "setup", "redist", "vcredist", "directx", "crashreport", "crashpad",
        "battleye", "easyanticheat", "eossdk", "vc_redist", "dxsetup", "helper", "service", "updater",
        "launcher"
    };

    // Bundled installers (e.g. GTA V's Redistributables\Rockstar-Games-Launcher.exe) are often
    // bigger than the game itself, so exes under these folders are never picked.
    private static readonly string[] NonGameFolderHints = {
        "redist", "installer", "prerequisite", "directx", "easyanticheat", "battleye"
    };

    private static List<DetectedGame> DetectSteamGames() {
        var results = new List<DetectedGame>();
        string? steamPath = GetSteamInstallPath();
        if (steamPath == null || !Directory.Exists(steamPath)) return results;

        var libraryFolders = new List<string>();
        string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath)) {
            string content = File.ReadAllText(vdfPath);
            foreach (Match m in Regex.Matches(content, "\"path\"\\s*\"([^\"]+)\"")) {
                string path = m.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(path)) libraryFolders.Add(PathNormalizer.Normalize(path));
            }
        }
        // Steam stores its own path as "c:/program files (x86)/steam". Normalized and added last, it
        // dedupes against the same folder from libraryfolders.vdf instead of being scanned twice.
        libraryFolders.Add(PathNormalizer.Normalize(steamPath));

        foreach (string library in libraryFolders.Distinct(StringComparer.OrdinalIgnoreCase)) {
            string steamappsDir = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamappsDir)) continue;

            foreach (string acfFile in Directory.GetFiles(steamappsDir, "appmanifest_*.acf")) {
                try {
                    string content = File.ReadAllText(acfFile);
                    Match nameMatch = Regex.Match(content, "\"name\"\\s*\"([^\"]+)\"");
                    Match dirMatch = Regex.Match(content, "\"installdir\"\\s*\"([^\"]+)\"");
                    if (!nameMatch.Success || !dirMatch.Success) continue;

                    string gameDir = Path.Combine(steamappsDir, "common", dirMatch.Groups[1].Value);
                    if (!Directory.Exists(gameDir)) continue;

                    string? exePath = Directory.GetFiles(gameDir, "*.exe", SearchOption.AllDirectories)
                        .Where(f => !LooksLikeUtilityExe(f, gameDir))
                        .OrderByDescending(f => new FileInfo(f).Length)
                        .FirstOrDefault();

                    if (exePath != null) results.Add(new DetectedGame(nameMatch.Groups[1].Value, exePath));
                } catch {
                    // Skip a malformed manifest.
                }
            }
        }
        return results;
    }

    private static bool LooksLikeUtilityExe(string path, string gameDir) {
        string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (NonGameExeHints.Any(hint => name.Contains(hint))) return true;

        string folder = (Path.GetDirectoryName(Path.GetRelativePath(gameDir, path)) ?? "").ToLowerInvariant();
        return NonGameFolderHints.Any(hint => folder.Contains(hint));
    }

    private static string? GetSteamInstallPath() {
        try {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        } catch {
            return null;
        }
    }
}
