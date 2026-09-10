using System.Management;

namespace HpVictusControl;

/// <summary>Reads system-wide CPU utilization via WMI's pre-formatted performance counters.</summary>
public static class CpuUsageSensor {

    public static bool TryGetUsagePercent(out double percent) {
        try {
            using var searcher = new ManagementObjectSearcher(
                "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name = '_Total'");
            foreach (ManagementBaseObject item in searcher.Get()) {
                using (item) {
                    percent = Convert.ToDouble(item["PercentProcessorTime"]);
                    return true;
                }
            }
        } catch {
            // Fall through to failure below.
        }
        percent = 0;
        return false;
    }
}
