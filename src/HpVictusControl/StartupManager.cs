using System.Diagnostics;
using Microsoft.Win32;

namespace HpVictusControl;

/// <summary>
/// Registers/unregisters the app to launch at logon via a Scheduled Task running at highest
/// privileges — unlike a registry Run-key entry, a task configured this way launches the
/// already-elevated app without showing a UAC prompt on every login.
/// </summary>
public static class StartupManager {

    private const string TaskName = "HpVictusControlStartup";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "HpVictusControl";

    public static void SetEnabled(bool enabled) {
        // Clean up the old registry-based approach from before the scheduled-task switch.
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);

        if (enabled) {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            RunSchTasks($"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\" --minimized\" " +
                        "/SC ONLOGON /RL HIGHEST /F");
        } else {
            RunSchTasks($"/Delete /TN \"{TaskName}\" /F");
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
            // Best-effort — if this fails, the checkbox state just won't have taken effect.
        }
    }
}
