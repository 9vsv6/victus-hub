using System.Management;

namespace HpVictusControl;

/// <summary>One reading of the battery's live power figures, from the ACPI battery driver.</summary>
public readonly record struct BatteryReading(bool OnBattery, int DischargeMilliwatts, int RemainingMwh, int FullChargeMwh);

/// <summary>How much power one performance mode has drawn on battery, learned over time.</summary>
public sealed class ModeDrain {
    public double AverageMilliwatts { get; set; }
    public double MinutesMeasured { get; set; }
}

/// <summary>
/// Estimates battery time for each performance mode. The live discharge rate only describes the
/// mode that's active right now, so every mode's typical draw is learned while the laptop actually
/// runs on battery in it, and kept in settings — that's what lets the app say how long Cool would
/// last while you're sitting in Performance, or before you unplug at all.
/// </summary>
public static class BatteryRuntime {

    // Windows' own estimate swings with every spike; a mode's typical draw should reflect a whole
    // session, so the average follows over roughly 20 minutes once there's enough history.
    private const double AverageTimeConstantMinutes = 20;

    // Below this, a learned figure is a guess from a moment of idling or a single load spike.
    public const double MinimumMinutesToTrust = 3;

    // Anything outside this isn't a real drain figure for this laptop (the driver reports 0 between
    // updates, and occasionally garbage).
    private const int MinPlausibleMilliwatts = 1_000;
    private const int MaxPlausibleMilliwatts = 150_000;

    public static BatteryReading? Read() {
        try {
            int discharge = 0, remaining = 0, full = 0;
            bool onBattery = false, found = false;

            using (var status = new ManagementObjectSearcher(@"root\wmi",
                       "SELECT PowerOnline, Discharging, DischargeRate, RemainingCapacity FROM BatteryStatus")) {
                foreach (ManagementBaseObject item in status.Get()) {
                    using (item) {
                        found = true;
                        onBattery |= item["PowerOnline"] is bool online && !online;
                        discharge += ToInt(item["DischargeRate"]);
                        remaining += ToInt(item["RemainingCapacity"]);
                    }
                }
            }
            if (!found) return null;

            using (var capacity = new ManagementObjectSearcher(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity")) {
                foreach (ManagementBaseObject item in capacity.Get()) {
                    using (item) full += ToInt(item["FullChargedCapacity"]);
                }
            }

            return new BatteryReading(onBattery, discharge, remaining, full);
        } catch {
            return null;
        }
    }

    public static bool IsPlausibleDrain(int milliwatts) =>
        milliwatts is >= MinPlausibleMilliwatts and <= MaxPlausibleMilliwatts;

    /// <summary>Folds <paramref name="minutes"/> of drain at <paramref name="milliwatts"/> into a mode's average.</summary>
    public static void AddSample(ModeDrain drain, double milliwatts, double minutes) {
        if (minutes <= 0) return;

        // A plain running mean at first, so a mode's figure settles within minutes rather than
        // creeping up from zero; after that, an exponential average so it follows how the laptop
        // is actually used now rather than a year ago.
        double weight = drain.MinutesMeasured < AverageTimeConstantMinutes
            ? minutes / (drain.MinutesMeasured + minutes)
            : 1 - Math.Exp(-minutes / AverageTimeConstantMinutes);

        drain.AverageMilliwatts += (milliwatts - drain.AverageMilliwatts) * weight;
        drain.MinutesMeasured += minutes;
    }

    public static TimeSpan? TimeLeft(int remainingMwh, double drainMilliwatts) {
        if (remainingMwh <= 0 || drainMilliwatts < MinPlausibleMilliwatts) return null;
        return TimeSpan.FromHours(remainingMwh / drainMilliwatts);
    }

    public static string Format(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}h {time.Minutes:00}m" : $"{Math.Max(1, (int)time.TotalMinutes)}m";

    private static int ToInt(object? value) {
        try { return value == null ? 0 : Convert.ToInt32(value); } catch { return 0; }
    }
}
