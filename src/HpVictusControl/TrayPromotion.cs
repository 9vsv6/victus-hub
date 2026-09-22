using Microsoft.Win32;

namespace HpVictusControl;

/// <summary>
/// Windows 11 hides a tray icon it hasn't seen before behind the notification area's "^" chevron.
/// For an app that lives in the tray that looks like it never started — it's running in Task Manager
/// with no window and no visible icon. Windows keeps that choice per executable under
/// HKCU\Control Panel\NotifyIconSettings, so this puts our own icon on the taskbar once. It's done
/// a single time (see AppSettings.TrayIconPromoted): after that, hiding it again is the user's call.
/// </summary>
public static class TrayPromotion {

    private const string SettingsKey = @"Control Panel\NotifyIconSettings";

    public enum Result {
        /// <summary>Windows hasn't recorded this executable's icon yet — worth trying again shortly.</summary>
        NoEntry,
        AlreadyPromoted,
        /// <summary>Changed — the icon has to be re-added for Explorer to notice.</summary>
        Promoted,
    }

    public static Result TryPromoteOwnIcon() {
        try {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return Result.NoEntry;

            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(SettingsKey, writable: true);
            if (root == null) return Result.NoEntry;

            foreach (string name in root.GetSubKeyNames()) {
                using RegistryKey? entry = root.OpenSubKey(name, writable: true);
                if (entry?.GetValue("ExecutablePath") is not string recorded) continue;
                if (!IsSameExecutable(recorded, exePath)) continue;
                if (entry.GetValue("IsPromoted") is int promoted && promoted == 1) return Result.AlreadyPromoted;

                entry.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                return Result.Promoted;
            }
        } catch {
            // Worst case the icon stays behind the chevron — not worth bothering the user about.
        }
        return Result.NoEntry;
    }

    // Windows writes the path with its known folder as a GUID, e.g.
    // "{6D809377-6AF0-444B-8957-A3773F02200E}\HP Victus Control\HpVictusControl.exe" for Program
    // Files, so what can be compared is the tail after the GUID.
    private static bool IsSameExecutable(string recorded, string exePath) {
        if (string.Equals(recorded, exePath, StringComparison.OrdinalIgnoreCase)) return true;
        if (!recorded.StartsWith('{')) return false;

        int end = recorded.IndexOf('}');
        if (end < 0) return false;

        string tail = recorded[(end + 1)..].TrimStart('\\');
        return tail.Length > 0 && exePath.EndsWith("\\" + tail, StringComparison.OrdinalIgnoreCase);
    }
}
