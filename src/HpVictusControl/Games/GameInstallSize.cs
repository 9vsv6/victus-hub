using System.IO;
using System.Text.RegularExpressions;

namespace HpVictusControl.Games;

/// <summary>
/// How much disk space an installed game takes. Steam games use the size Steam itself records in the
/// library's appmanifest (instant, and it counts the whole install rather than just the exe's folder);
/// everything else is measured by adding up the game's install folder.
/// </summary>
public static class GameInstallSize {

    public static long? TryGet(string exePath) {
        try {
            return FromSteamManifest(exePath) ?? FromFolder(InstallRoot(exePath));
        } catch {
            return null;
        }
    }

    private static long? FromSteamManifest(string exePath) {
        const string marker = @"\steamapps\common\";
        int index = exePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        string steamapps = exePath[..(index + @"\steamapps".Length)];
        string installDir = exePath[(index + marker.Length)..].Split('\\')[0];

        foreach (string manifest in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf")) {
            string content = File.ReadAllText(manifest);
            Match dir = Regex.Match(content, "\"installdir\"\\s*\"([^\"]+)\"");
            if (!dir.Success || !dir.Groups[1].Value.Equals(installDir, StringComparison.OrdinalIgnoreCase)) continue;

            Match size = Regex.Match(content, "\"SizeOnDisk\"\\s*\"(\\d+)\"");
            return size.Success && long.TryParse(size.Groups[1].Value, out long bytes) && bytes > 0 ? bytes : null;
        }
        return null;
    }

    // The game's top folder inside a known library ("...\Epic Games\<Game>", "...\XboxGames\<Game>"),
    // otherwise the exe's own folder.
    private static string? InstallRoot(string exePath) {
        foreach (string library in new[] { @"\steamapps\common\", @"\Epic Games\", @"\XboxGames\" }) {
            int index = exePath.IndexOf(library, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            string rest = exePath[(index + library.Length)..];
            int slash = rest.IndexOf('\\');
            if (slash > 0) return exePath[..(index + library.Length + slash)];
        }
        return Path.GetDirectoryName(exePath);
    }

    private static long? FromFolder(string? folder) {
        if (folder == null || !Directory.Exists(folder)) return null;
        long total = 0;
        foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions {
                     RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
                 })) {
            try { total += file.Length; } catch { /* vanished mid-scan */ }
        }
        return total > 0 ? total : null;
    }
}
