using System.Management;

namespace HpVictusControl;

/// <summary>Reads/sets the built-in panel's brightness via WMI (root\WMI, WmiMonitorBrightness*).</summary>
public static class ScreenBrightness {

    public static bool TryGetBrightness(out byte percent) {
        try {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightness");
            foreach (ManagementBaseObject item in searcher.Get()) {
                using (item) {
                    percent = (byte)item["CurrentBrightness"];
                    return true;
                }
            }
        } catch {
            // Fall through to failure below.
        }
        percent = 0;
        return false;
    }

    public static bool SetBrightness(byte percent) {
        try {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementBaseObject baseItem in searcher.Get()) {
                using var item = (ManagementObject)baseItem;
                item.InvokeMethod("WmiSetBrightness", new object[] { 1, percent }); // 1 = transition time in seconds
                return true;
            }
        } catch {
            // Fall through to failure below.
        }
        return false;
    }
}
