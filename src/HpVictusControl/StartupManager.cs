using System.Diagnostics;
using Microsoft.Win32;

namespace HpVictusControl;

/// <summary>
/// Registers/unregisters the app to launch at logon via the classic per-user Registry Run key.
/// This shows up in Task Manager's Startup apps list (unlike a Scheduled Task), at the cost of
/// a UAC prompt on every login since the app requires admin.
/// </summary>
public static class StartupManager {

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HpVictusControl";

    // Name used by an earlier version of this app that registered via Scheduled Task instead.
    private const string LegacyTaskName = "HpVictusControlStartup";

    public static void SetEnabled(bool enabled) {
        RunSchTasks($"/Delete /TN \"{LegacyTaskName}\" /F");

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled) {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;
            key.SetValue(ValueName, $"\"{exePath}\" --minimized");
        } else {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static void RunSchTasks(string arguments) {
        try {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments) {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process?.WaitForExit(10_000);
        } catch {
            // Best-effort cleanup — not fatal if the old task is already gone or can't be reached.
        }
    }
}
