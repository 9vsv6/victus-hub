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

    // Plugged-in ("AC") values in all three schemes the modes use. On battery nothing changes.
    // Windows' own defaults are USB selective suspend on (1) and Wi-Fi at Maximum Performance (0);
    // on this machine Wi-Fi was already 0 in every scheme, the USB one was the real change.
    private const string UsbSubgroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string WirelessSubgroup = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1";
    private const string WirelessPowerSaving = "12bbebe6-58d6-4636-95bb-3217ef867c1a";

    /// <summary>
    /// On: USB selective suspend off and Wi-Fi at Maximum Performance while plugged in. Off: USB
    /// selective suspend back to Windows' default (on); Wi-Fi's plugged-in default already is full power.
    /// </summary>
    public static void SetPluggedInTweaks(bool on) {
        foreach (string scheme in new[] { BalancedGuid, HighPerformanceGuid, PowerSaverGuid }) {
            RunPowercfg($"/setacvalueindex {scheme} {UsbSubgroup} {UsbSelectiveSuspend} {(on ? 0 : 1)}");
            RunPowercfg($"/setacvalueindex {scheme} {WirelessSubgroup} {WirelessPowerSaving} 0");
        }
        // Values written to the active scheme only take effect once it's re-applied.
        RunPowercfg("/setactive SCHEME_CURRENT");
    }

    // ----- Performance plan tuning -----
    // Only the High performance scheme (the one Performance mode uses) and only its plugged-in values.
    // These settings are hidden from "powercfg /q" on this machine, so they're read and written through
    // powrprof directly, which doesn't care whether a setting is hidden.
    private static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");

    private static readonly (string Name, Guid Setting, uint Tuned)[] PerformanceTuning = {
        ("Processor boost mode", new Guid("be337238-0d82-4146-a960-4f3749d470c7"), 2),          // Aggressive
        ("Core parking, performance cores", new Guid("0cc5b647-c1df-4637-891a-dec35c318583"), 100), // min cores unparked: all
        ("Core parking, efficiency cores", new Guid("0cc5b647-c1df-4637-891a-dec35c318584"), 100),
    };

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr rootKey, ref Guid scheme);

    /// <summary>
    /// Sets faster boost and no core parking in the High performance scheme. Returns what each changed
    /// setting was before (for <see cref="RestorePerformanceScheme"/>); already-tuned ones aren't listed.
    /// </summary>
    public static Dictionary<string, uint> TunePerformanceScheme(out List<string> changed) {
        var originals = new Dictionary<string, uint>();
        changed = new List<string>();
        Guid scheme = Guid.Parse(HighPerformanceGuid), subgroup = ProcessorSubgroup;
        foreach ((string name, Guid setting, uint tuned) in PerformanceTuning) {
            Guid id = setting;
            if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref id, out uint current) != 0 || current == tuned) continue;
            if (PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref id, tuned) != 0) continue;
            originals[setting.ToString()] = current;
            changed.Add($"{name}: {current} → {tuned}");
        }
        ReapplyIfActive(scheme);
        return originals;
    }

    public static void RestorePerformanceScheme(Dictionary<string, uint> originals) {
        Guid scheme = Guid.Parse(HighPerformanceGuid), subgroup = ProcessorSubgroup;
        foreach ((string settingText, uint value) in originals) {
            Guid setting = Guid.Parse(settingText);
            PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value);
        }
        ReapplyIfActive(scheme);
    }

    // Changed values of the active scheme only take effect once it's set active again.
    private static void ReapplyIfActive(Guid scheme) {
        if (GetActiveScheme() == scheme) PowerSetActiveScheme(IntPtr.Zero, ref scheme);
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
