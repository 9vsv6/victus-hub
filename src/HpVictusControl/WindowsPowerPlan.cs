using System.Diagnostics;
using HpVictusControl.Bios;

namespace HpVictusControl;

/// <summary>
/// Switches Windows' own power plan alongside the BIOS performance mode. These built-in
/// schemes are hidden from "powercfg /list" on this machine (only Balanced shows), but the
/// GUIDs are the standard ones Windows ships with and "powercfg /setactive" still works on
/// them directly — confirmed by querying and activating each one directly.
/// </summary>
public static class WindowsPowerPlan {

    private const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string PowerSaverGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";

    public static void SetForMode(HpFanMode mode) {
        string guid = mode switch {
            HpFanMode.Performance => HighPerformanceGuid,
            HpFanMode.Cool => PowerSaverGuid,
            _ => BalancedGuid
        };
        RunPowercfg($"/setactive {guid}");
    }

    private static void RunPowercfg(string arguments) {
        try {
            using Process? process = Process.Start(new ProcessStartInfo("powercfg.exe", arguments) {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(5000);
        } catch {
            // Best-effort — not every system exposes every scheme.
        }
    }
}
