using System.Management;

namespace HpVictusControl;

/// <summary>A camera as Windows' device manager lists it.</summary>
public sealed record CameraDevice(string Name, string DeviceId, bool Enabled);

/// <summary>
/// Turns the webcam off at the device level, the same as "Disable device" in Device Manager: no
/// app, browser or driver can open it until it's switched back on, which also survives restarts.
/// Uses Win32_PnPEntity's Enable/Disable methods (what Disable-PnpDevice calls); needs admin.
/// </summary>
public static class CameraSwitch {

    // Device Manager's "this device is disabled" problem code.
    private const uint DisabledCode = 22;

    public static List<CameraDevice> List() {
        var cameras = new List<CameraDevice>();
        try {
            // "Camera" is the class webcams use on Windows 10 and later; older drivers file them under
            // "Image" alongside scanners, so those only count when the name says camera.
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = 'Camera' OR PNPClass = 'Image'");
            foreach (ManagementBaseObject device in searcher.Get()) {
                using (device) {
                    string name = device["Name"] as string ?? "";
                    string cls = device["PNPClass"] as string ?? "";
                    if (cls == "Image" && !name.Contains("cam", StringComparison.OrdinalIgnoreCase)) continue;
                    uint code = Convert.ToUInt32(device["ConfigManagerErrorCode"] ?? 0u);
                    cameras.Add(new CameraDevice(name, device["PNPDeviceID"] as string ?? "", code != DisabledCode));
                }
            }
        } catch {
            // No cameras, or WMI unavailable: an empty list hides the switch.
        }
        return cameras;
    }

    /// <summary>Switches every camera on or off; returns an error message, or null when all took it.</summary>
    public static string? SetEnabled(bool enabled) {
        var problems = new List<string>();
        foreach (CameraDevice camera in List()) {
            if (camera.Enabled == enabled) continue;
            try {
                string escaped = camera.DeviceId.Replace("\\", "\\\\").Replace("'", "\\'");
                using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID = '{escaped}'");
                foreach (ManagementObject device in searcher.Get().Cast<ManagementObject>()) {
                    using (device) {
                        object result = device.InvokeMethod(enabled ? "Enable" : "Disable", null);
                        uint code = Convert.ToUInt32(result ?? 0u);
                        if (code != 0) problems.Add($"{camera.Name} (0x{code:X})");
                    }
                }
            } catch (Exception ex) {
                problems.Add($"{camera.Name}: {ex.Message}");
            }
        }
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }
}
