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

        if (mode == HpFanMode.Cool) {
            // Windows 11's "Energy saver" quick-settings toggle is a separate feature from the
            // classic power plan above — it only engages automatically below a battery-percentage
            // threshold (20% by default), and there's no supported API to force it on directly.
            // Setting that threshold to 100% on the Power saver scheme (now active) makes it kick
            // in immediately whenever Cool mode is running on battery. It only affects Power saver's
            // own stored threshold, so switching to another mode activates a different scheme with
            // its own untouched value — nothing needs to be restored. It has no effect on AC power,
            // since Energy Saver itself is battery-only.
            RunPowercfg("/setdcvalueindex SCHEME_CURRENT SUB_ENERGYSAVER ESBATTTHRESHOLD 100");
        }
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
