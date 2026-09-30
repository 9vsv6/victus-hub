using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace HpVictusControl;

/// <summary>One program that starts with Windows, from a Run key or a Startup folder.</summary>
public sealed record StartupApp(
    string Name, string Command, string? ExePath, string? Publisher, bool Enabled,
    RegistryHive ApprovalHive, string ApprovalKey, string ApprovalValue, string Source);

/// <summary>
/// The same list Task Manager's Startup apps page shows, read from the Run keys and Startup
/// folders. Switching one off works the way Task Manager does it: a flag under
/// Explorer\StartupApproved, so nothing is deleted and switching it back on restores it exactly.
/// Takes effect at the next sign-in.
/// </summary>
public static class StartupApps {

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupApp> List() {
        var apps = new List<StartupApp>();
        AddRunKey(apps, RegistryHive.CurrentUser, RunKey, "Run", "Current user");
        AddRunKey(apps, RegistryHive.LocalMachine, RunKey, "Run", "All users");
        AddRunKey(apps, RegistryHive.LocalMachine, Run32Key, "Run32", "All users");
        AddFolder(apps, RegistryHive.CurrentUser, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Startup folder");
        AddFolder(apps, RegistryHive.LocalMachine, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Startup folder (all users)");

        // This app manages its own start in Settings → Startup; listing it here would be two switches for one thing.
        return apps
            .Where(app => !string.Equals(app.ApprovalValue, "HpVictusControl", StringComparison.OrdinalIgnoreCase))
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void SetEnabled(StartupApp app, bool enabled) {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(app.ApprovalHive, RegistryView.Registry64);
        using RegistryKey key = baseKey.CreateSubKey(app.ApprovalKey, writable: true);
        // 12 bytes: 02 = enabled; 03 plus the time it was switched off = disabled (Task Manager's format).
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        key.SetValue(app.ApprovalValue, data, RegistryValueKind.Binary);
    }

    private static void AddRunKey(List<StartupApp> apps, RegistryHive hive, string keyPath, string approvalName, string source) {
        try {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(keyPath);
            if (key == null) return;
            string approvalKey = ApprovedRoot + approvalName;
            foreach (string valueName in key.GetValueNames()) {
                if (key.GetValue(valueName) is not string command || command.Length == 0) continue;
                string? exe = ExeFromCommand(Environment.ExpandEnvironmentVariables(command));
                apps.Add(new StartupApp(DisplayName(exe, valueName), command, exe, Publisher(exe),
                    IsEnabled(hive, approvalKey, valueName), hive, approvalKey, valueName, source));
            }
        } catch {
            // An unreadable key just leaves its entries out.
        }
    }

    private static void AddFolder(List<StartupApp> apps, RegistryHive hive, string folder, string source) {
        try {
            if (!Directory.Exists(folder)) return;
            string approvalKey = ApprovedRoot + "StartupFolder";
            foreach (string file in Directory.GetFiles(folder)) {
                string fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                string? exe = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) : file;
                apps.Add(new StartupApp(DisplayName(exe, Path.GetFileNameWithoutExtension(file)), file, exe, Publisher(exe),
                    IsEnabled(hive, approvalKey, fileName), hive, approvalKey, fileName, source));
            }
        } catch {
            // Same as above.
        }
    }

    private static bool IsEnabled(RegistryHive hive, string approvalKey, string valueName) {
        try {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(approvalKey);
            // No flag at all means it was never switched off. Odd first bytes (03, 07…) mean disabled.
            return key?.GetValue(valueName) is not byte[] data || data.Length == 0 || (data[0] & 1) == 0;
        } catch {
            return true;
        }
    }

    // "C:\Program Files\App\app.exe" --flag  →  C:\Program Files\App\app.exe
    internal static string? ExeFromCommand(string command) {
        command = command.Trim();
        if (command.StartsWith('"')) {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : command.Split(' ')[0];
    }

    private static string DisplayName(string? exe, string fallback) {
        try {
            if (exe != null && File.Exists(exe)) {
                string? description = FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(description)) return description;
            }
        } catch {
            // Fall back to the entry's own name.
        }
        return fallback;
    }

    private static string? Publisher(string? exe) {
        try {
            if (exe == null || !File.Exists(exe)) return null;
            string? company = FileVersionInfo.GetVersionInfo(exe).CompanyName?.Trim();
            return string.IsNullOrEmpty(company) ? null : company;
        } catch {
            return null;
        }
    }

    private static string? ShortcutTarget(string lnkPath) {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return null;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try {
            string target = shell.CreateShortcut(lnkPath).TargetPath;
            return string.IsNullOrEmpty(target) ? null : target;
        } catch {
            return null;
        } finally {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
