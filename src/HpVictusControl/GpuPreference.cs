using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace HpVictusControl;

/// <summary>
/// Windows' per-app graphics preference (Settings > System > Display > Graphics), stored per
/// executable path under HKCU. Windows has no single switch that covers every app, so this is
/// applied app by app. Other per-app DirectX settings in the same value are preserved.
/// </summary>
public static class GpuPreference {

    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string PreferenceName = "GpuPreference";
    private const int HighPerformance = 2;

    public static List<string> GetHighPerformanceApps() {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (key == null) return new List<string>();

        // Skips non-app entries such as DirectXUserGlobalSettings, and lists an app once even if it
        // was saved under several spellings, preferring the backslash one.
        return key.GetValueNames()
            .Where(name => name.Contains('\\') || name.Contains('/'))
            .Where(name => ReadPreference(key.GetValue(name) as string) == HighPerformance)
            .GroupBy(PathNormalizer.Normalize, StringComparer.OrdinalIgnoreCase)
            .Select(group => PathNormalizer.Normalize(group.OrderBy(name => name.Contains('/')).First()))
            .OrderBy(name => Path.GetFileName(name), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsHighPerformance(string exePath) {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (key == null) return false;
        return MatchingValueNames(key, exePath).Any(name => ReadPreference(key.GetValue(name) as string) == HighPerformance);
    }

    public static void SetHighPerformance(string exePath) {
        string path = PathNormalizer.Normalize(exePath);
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(path, WithPreference(key.GetValue(path) as string, HighPerformance), RegistryValueKind.String);
    }

    public static void Remove(string exePath) {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key == null) return;

        foreach (string name in MatchingValueNames(key, exePath).ToList()) {
            string remaining = WithPreference(key.GetValue(name) as string, null);
            if (remaining.Length == 0) key.DeleteValue(name, throwOnMissingValue: false);
            else key.SetValue(name, remaining, RegistryValueKind.String);
        }
    }

    private static IEnumerable<string> MatchingValueNames(RegistryKey key, string exePath) {
        string target = PathNormalizer.Normalize(exePath);
        return key.GetValueNames()
            .Where(name => string.Equals(PathNormalizer.Normalize(name), target, StringComparison.OrdinalIgnoreCase));
    }

    private static int? ReadPreference(string? data) {
        foreach (string part in SplitParts(data)) {
            string[] pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals(PreferenceName, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(pair[1], out int value)) {
                return value;
            }
        }
        return null;
    }

    private static string WithPreference(string? data, int? preference) {
        List<string> parts = SplitParts(data)
            .Where(part => !part.StartsWith(PreferenceName + "=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (preference.HasValue) parts.Add($"{PreferenceName}={preference.Value}");
        return parts.Count == 0 ? "" : string.Join(";", parts) + ";";
    }

    private static IEnumerable<string> SplitParts(string? data) =>
        (data ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
