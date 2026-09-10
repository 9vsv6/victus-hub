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
}
