using System.Management;

namespace HpVictusControl;

/// <summary>
/// SSD/NVMe health from Windows' own storage stack: temperature, how much of the drive's write
/// endurance is used up, and how long it's been powered on. Reading the reliability counters needs
/// administrator rights, which this app already runs with.
/// </summary>
public static class DiskHealth {

    public sealed record DiskStatus(
        string Name,
        long SizeBytes,
        int? TemperatureCelsius,
        int? TemperatureMaxCelsius,
        int? WearPercent,
        long? PowerOnHours,
        long? ReadErrors,
        long? WriteErrors) {

        /// <summary>Endurance left, as the drive reports wear used.</summary>
        public int? LifeLeftPercent => WearPercent is int wear && wear is >= 0 and <= 100 ? 100 - wear : null;
    }

    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    public static List<DiskStatus> Read() {
        var disks = new List<DiskStatus>();
        try {
            var scope = new ManagementScope(StorageNamespace);
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));
            foreach (ManagementBaseObject item in searcher.Get()) {
                using var disk = (ManagementObject)item;
                string name = disk["FriendlyName"]?.ToString()?.Trim() ?? "Disk";
                long size = ToLong(disk["Size"]) ?? 0;

                int? temperature = null, temperatureMax = null, wear = null;
                long? powerOnHours = null, readErrors = null, writeErrors = null;
                try {
                    foreach (ManagementBaseObject related in disk.GetRelated("MSFT_StorageReliabilityCounter")) {
                        using (related) {
                            temperature = ToInt(related["Temperature"]);
                            temperatureMax = ToInt(related["TemperatureMax"]);
                            wear = ToInt(related["Wear"]);
                            powerOnHours = ToLong(related["PowerOnHours"]);
                            readErrors = ToLong(related["ReadErrorsTotal"]);
                            writeErrors = ToLong(related["WriteErrorsTotal"]);
                        }
                        break;
                    }
                } catch {
                    // Counters aren't exposed by every controller — the drive still gets listed.
                }

                disks.Add(new DiskStatus(name, size, Sane(temperature), Sane(temperatureMax), wear,
                    powerOnHours, readErrors, writeErrors));
            }
        } catch {
            // No storage WMI, no card.
        }
        return disks;
    }

    // Drives that don't report a temperature return 0, which would read as a 0°C SSD.
    private static int? Sane(int? celsius) => celsius is > 0 and < 150 ? celsius : null;

    private static int? ToInt(object? value) {
        try { return value == null ? null : Convert.ToInt32(value); } catch { return null; }
    }

    private static long? ToLong(object? value) {
        try { return value == null ? null : Convert.ToInt64(value); } catch { return null; }
    }
}
