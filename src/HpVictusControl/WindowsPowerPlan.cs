using System.Diagnostics;
using System.Runtime.InteropServices;
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

    // Note on CPU temperature: the High performance scheme holds the cores near maximum turbo
    // regardless of how little work there is — measured on this machine at ~2.0x the 2.1GHz base
    // under a 30% load, against ~1.0x for the same load under Balanced. That is a scheme-level
    // (HWP energy-preference) behaviour: lowering "Minimum processor state" to 5% was measured to
    // change nothing, and this firmware exposes no energy-preference setting to adjust, so the
    // only real lever is which scheme is active — i.e. which mode the user (or a game profile)
    // picks. Nothing here tries to soften Performance mode behind the user's back.
    public static void SetForMode(HpFanMode mode) {
        RunPowercfg($"/setactive {SchemeForMode(mode)}");

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

    /// <summary>
    /// Puts the scheme back when something outside the app has changed it. The mode shown in the
    /// app is meant to be the truth, and a scheme that has drifted is invisible until you notice
    /// the machine running hot: the High performance scheme holds the CPU near max turbo, which
    /// measured ~4.5GHz and 90°C+ in a game that sits at ~2.5GHz and the 60s under Balanced.
    /// Returns true when it had to correct something.
    /// </summary>
    public static bool EnsureActiveForMode(HpFanMode mode) {
        Guid? active = GetActiveScheme();
        if (active == null || active.Value == Guid.Parse(SchemeForMode(mode))) return false;

        SetForMode(mode);
        return true;
    }

    private static string SchemeForMode(HpFanMode mode) => mode switch {
        HpFanMode.Performance => HighPerformanceGuid,
        HpFanMode.Cool => PowerSaverGuid,
        _ => BalancedGuid
    };

    // Reading the active scheme through powrprof rather than "powercfg /getactivescheme" keeps
    // this cheap enough to run on every poll tick — no process to start and parse.
    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static Guid? GetActiveScheme() {
        IntPtr buffer = IntPtr.Zero;
        try {
            if (PowerGetActiveScheme(IntPtr.Zero, out buffer) != 0 || buffer == IntPtr.Zero) return null;
            return Marshal.PtrToStructure<Guid>(buffer);
        } catch {
            return null;
        } finally {
            if (buffer != IntPtr.Zero) LocalFree(buffer);
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
