using System.Management;

namespace HpVictusControl.Updates;

/// <summary>The drives actually fitted to this machine, for filtering HP's storage firmware list.</summary>
public static class InstalledStorage {

    public readonly record struct Disk(string Model, string FirmwareRevision);

    /// <summary>
    /// e.g. ("SAMSUNG MZVL8512HELU-00BH1", "HPS4NKXM"). HP's catalog lists firmware for every drive
    /// its factory ever fitted to this model, so the model and the running firmware revision are
    /// what tell an applicable update from someone else's.
    /// </summary>
    public static List<Disk> GetDisks() {
        var disks = new List<Disk>();
        try {
            using var searcher = new ManagementObjectSearcher("SELECT Model, FirmwareRevision FROM Win32_DiskDrive");
            foreach (ManagementBaseObject item in searcher.Get()) {
                using (item) {
                    string model = item["Model"]?.ToString()?.Trim() ?? "";
                    string firmware = item["FirmwareRevision"]?.ToString()?.Trim() ?? "";
                    if (model.Length > 0 || firmware.Length > 0) disks.Add(new Disk(model, firmware));
                }
            }
        } catch {
            // No list means no filtering — better to show an update too many than hide a real one.
        }
        return disks;
    }
}
