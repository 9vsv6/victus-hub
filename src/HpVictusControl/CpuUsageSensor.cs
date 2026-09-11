using System.Diagnostics;

namespace HpVictusControl;

/// <summary>
/// Reads system-wide CPU utilization via a persistent PerformanceCounter. This counter type
/// computes a rate between two samples, so it must stay alive across calls — a fresh counter
/// (or a one-shot WMI query) has no prior sample to diff against and always reads 0. The first
/// call after construction returns 0 by design; every call after that is accurate.
/// </summary>
public static class CpuUsageSensor {

    private static readonly PerformanceCounter? Counter = TryCreateCounter();

    private static PerformanceCounter? TryCreateCounter() {
        try {
            var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            counter.NextValue(); // Prime the baseline sample so the first real read is accurate.
            return counter;
        } catch {
            return null;
        }
    }

    public static bool TryGetUsagePercent(out double percent) {
        if (Counter != null) {
            try {
                percent = Counter.NextValue();
                return true;
            } catch {
                // Fall through to failure below.
            }
        }
        percent = 0;
        return false;
    }
}
