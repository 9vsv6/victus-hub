using System.Management;

namespace HpVictusControl.Updates;

/// <summary>Reads the versions currently installed on this machine, for comparing against HP's catalog.</summary>
public static class InstalledDriverInfo {

    /// <summary>e.g. "F.08" — the BIOS's own reported version string.</summary>
    public static string? GetBiosVersion() {
        using var searcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS");
        foreach (ManagementBaseObject item in searcher.Get()) {
            using (item) {
                return item["SMBIOSBIOSVersion"]?.ToString()?.Trim();
            }
        }
        return null;
    }

    /// <summary>Every currently-installed signed driver's device name and version, for fuzzy matching.</summary>
    public static List<(string DeviceName, string? DriverVersion)> GetSignedDrivers() {
        var results = new List<(string, string?)>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceName, DriverVersion FROM Win32_PnPSignedDriver WHERE DeviceName IS NOT NULL");
        foreach (ManagementBaseObject item in searcher.Get()) {
            using (item) {
                string? name = item["DeviceName"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    results.Add((name, item["DriverVersion"]?.ToString()));
            }
        }
        return results;
    }

    /// <summary>
    /// Drivers of display adapters only. Matching a graphics package by vendor name across every
    /// device picked "Intel(R) Innovation Platform Framework Manager" (2.3.x) over the Intel GPU
    /// (32.0.x), so a graphics driver older than the installed one was offered as an update.
    /// </summary>
    public static List<(string DeviceName, string? DriverVersion)> GetDisplayDrivers() {
        var results = new List<(string, string?)>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceName, DriverVersion FROM Win32_PnPSignedDriver WHERE DeviceClass = 'DISPLAY' AND DeviceName IS NOT NULL");
        foreach (ManagementBaseObject item in searcher.Get()) {
            using (item) results.Add((item["DeviceName"]!.ToString()!, item["DriverVersion"]?.ToString()));
        }
        return results;
    }

    /// <summary>
    /// Plug-and-play device ids present on this machine, driver or not — for packages that only
    /// apply when a particular piece of hardware exists.
    /// </summary>
    public static List<string> GetDeviceIds() {
        var ids = new List<string>();
        using var searcher = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_PnPEntity");
        foreach (ManagementBaseObject item in searcher.Get()) {
            using (item) {
                if (item["PNPDeviceID"] is string id) ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>
    /// Whether any device is waiting for a driver (problem code 28, "drivers not installed", or 1,
    /// "not configured"). A disabled device (22) doesn't count: it has its driver.
    /// </summary>
    public static bool AnyDeviceMissingDriver() {
        using var searcher = new ManagementObjectSearcher(
            "SELECT ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode = 28 OR ConfigManagerErrorCode = 1");
        using ManagementObjectCollection results = searcher.Get();
        return results.Count > 0;
    }
}
