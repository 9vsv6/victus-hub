using System.Management;

namespace HpVictusControl;

/// <summary>What happened when a restore point was asked for.</summary>
public enum RestorePointOutcome {
    Created,
    /// <summary>Windows accepted the request but kept its one-a-day limit: the newest point is older.</summary>
    SkippedRecentExists,
    Failed,
}

/// <summary>
/// Windows System Restore points, through the same WMI class Checkpoint-Computer uses. Creating one
/// needs admin rights (which the app has) and System Protection turned on for the system drive.
/// </summary>
public static class RestorePoint {

    private const uint DeviceDriverInstall = 10;
    private const uint BeginSystemChange = 100;

    public static (RestorePointOutcome Outcome, string? Detail) Create(string description) {
        try {
            DateTime? before = NewestCreationTime();
            var scope = new ManagementScope(@"\\.\root\default");
            using var restore = new ManagementClass(scope, new ManagementPath("SystemRestore"), null);
            using ManagementBaseObject input = restore.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description;
            input["RestorePointType"] = DeviceDriverInstall;
            input["EventType"] = BeginSystemChange;
            using ManagementBaseObject result = restore.InvokeMethod("CreateRestorePoint", input, null);
            uint code = Convert.ToUInt32(result["ReturnValue"]);
            if (code != 0) return (RestorePointOutcome.Failed, $"0x{code:X}");

            // Windows answers "done" even when its 24-hour limit made it skip the point.
            DateTime? after = NewestCreationTime();
            if (after == null || (before != null && after <= before))
                return (RestorePointOutcome.SkippedRecentExists, after?.ToString("g"));
            return (RestorePointOutcome.Created, null);
        } catch (Exception ex) {
            return (RestorePointOutcome.Failed, ex.Message);
        }
    }

    private static DateTime? NewestCreationTime() {
        DateTime? newest = null;
        try {
            using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT CreationTime FROM SystemRestore");
            foreach (ManagementBaseObject point in searcher.Get()) {
                using (point) {
                    if (point["CreationTime"] is string wmiTime) {
                        DateTime created = ManagementDateTimeConverter.ToDateTime(wmiTime);
                        if (newest == null || created > newest) newest = created;
                    }
                }
            }
        } catch {
            // No points yet, or protection off: nothing to compare against.
        }
        return newest;
    }
}
