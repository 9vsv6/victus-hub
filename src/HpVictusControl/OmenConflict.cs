using System.Diagnostics;
using System.Management;

namespace HpVictusControl;

/// <summary>One piece of HP software that can send its own fan or performance commands.</summary>
public sealed record OmenComponent(string Name, string DisplayName, bool IsService, bool Running, string StartMode);

/// <summary>
/// Finds HP's own fan/performance software — Omen Gaming Hub and its HSA background service
/// (HPOmenCap, OmenCap.exe, installed with HP's drivers) — which talks to the same BIOS interface
/// as this app and can undo its fan and mode changes. Switching it off stops the services and sets
/// them to Disabled through WMI's Win32_Service; nothing is deleted, and each one's previous start
/// mode is kept so it can be put back exactly.
/// </summary>
public static class OmenConflict {

    private static readonly string[] KnownProcesses = { "OmenCommandCenterBackground", "OmenCommandCenter" };

    public static List<OmenComponent> Detect() {
        var found = new List<OmenComponent>();
        try {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DisplayName, State, StartMode FROM Win32_Service WHERE Name LIKE '%omen%' OR DisplayName LIKE '%omen%'");
            foreach (ManagementBaseObject service in searcher.Get()) {
                using (service) {
                    found.Add(new OmenComponent(
                        service["Name"] as string ?? "", service["DisplayName"] as string ?? "", true,
                        string.Equals(service["State"] as string, "Running", StringComparison.OrdinalIgnoreCase),
                        service["StartMode"] as string ?? ""));
                }
            }
        } catch {
            // Service list unreadable: report what the process list shows.
        }
        foreach (string name in KnownProcesses) {
            Process[] running = Process.GetProcessesByName(name);
            foreach (Process process in running) process.Dispose();
            if (running.Length > 0) found.Add(new OmenComponent(name, "OMEN Gaming Hub", false, true, ""));
        }
        return found;
    }

    /// <summary>
    /// Stops and disables every Omen service found and closes Omen Gaming Hub's background program.
    /// Returns each service's previous start mode ("Auto", "Manual"…) for <see cref="Restore"/>.
    /// </summary>
    public static Dictionary<string, string> Disable(out List<string> problems) {
        problems = new List<string>();
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (OmenComponent component in Detect()) {
            try {
                if (!component.IsService) {
                    foreach (Process process in Process.GetProcessesByName(component.Name)) {
                        using (process) process.Kill();
                    }
                    continue;
                }
                if (component.StartMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) continue;

                using ManagementObject service = ServiceObject(component.Name);
                uint changed = Invoke(service, "ChangeStartMode", "Disabled");
                if (changed != 0) {
                    problems.Add($"{component.DisplayName} (0x{changed:X})");
                    continue;
                }
                previous[component.Name] = component.StartMode;
                if (component.Running) Invoke(service, "StopService");
            } catch (Exception ex) {
                problems.Add($"{component.DisplayName}: {ex.Message}");
            }
        }
        return previous;
    }

    /// <summary>Puts each service's start mode back, and starts the ones that started automatically.</summary>
    public static void Restore(Dictionary<string, string> previous, out List<string> problems) {
        problems = new List<string>();
        foreach ((string name, string startMode) in previous) {
            try {
                using ManagementObject service = ServiceObject(name);
                // Win32_Service reports "Auto" but ChangeStartMode takes "Automatic".
                string mode = startMode.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? "Automatic" : startMode;
                uint changed = Invoke(service, "ChangeStartMode", mode);
                if (changed != 0) {
                    problems.Add($"{name} (0x{changed:X})");
                    continue;
                }
                if (mode == "Automatic") Invoke(service, "StartService");
            } catch (Exception ex) {
                problems.Add($"{name}: {ex.Message}");
            }
        }
    }

    private static ManagementObject ServiceObject(string name) =>
        new($"Win32_Service.Name='{name.Replace("'", "\\'")}'");

    private static uint Invoke(ManagementObject service, string method, string? argument = null) {
        object? result = service.InvokeMethod(method, argument == null ? null : new object[] { argument });
        return Convert.ToUInt32(result ?? 0u);
    }
}
