using System.Diagnostics;
using System.Management;

namespace HpVictusControl.Bios;

/// <summary>One BIOS setup option, as HP's BIOS reports it to Windows.</summary>
public sealed record BiosSetting(string Name, string CurrentValue, IReadOnlyList<string> Options, bool IsReadOnly);

/// <summary>
/// HP's BIOS setup options through its own WMI provider (root\HP\InstrumentedBIOS) — the same
/// settings as the F10 setup screen. Only a short list of harmless ones is ever changed from the app
/// (see <see cref="Offered"/>): security and boot options stay in the BIOS screen, where a mistake
/// can't lock anyone out of their laptop from inside Windows.
/// </summary>
public static class HpBiosSettings {

    private const string Namespace = @"root\HP\InstrumentedBIOS";

    public const string BatteryCare = "Adaptive Battery Extender";
    public const string BatteryCareStatus = "Adaptive Battery Extender Status";
    public const string FanAlwaysOn = "Fan Always On";
    public const string ActionKeys = "Action Keys Mode";
    public const string BootMenuDelay = "POST Hotkey Delay (sec)";

    /// <summary>The settings the app shows. Nothing outside this list is ever written.</summary>
    public static readonly string[] Offered = { BatteryCare, FanAlwaysOn, ActionKeys, BootMenuDelay };

    /// <summary>Reads the offered settings (plus battery care's live status); empty if the provider isn't there.</summary>
    public static Dictionary<string, BiosSetting> Read() {
        var settings = new Dictionary<string, BiosSetting>(StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<string>(Offered.Append(BatteryCareStatus), StringComparer.OrdinalIgnoreCase);
        try {
            using var searcher = new ManagementObjectSearcher(Namespace,
                "SELECT Name, CurrentValue, PossibleValues, IsReadOnly FROM HP_BIOSEnumeration");
            foreach (ManagementBaseObject item in searcher.Get()) {
                using (item) {
                    string name = (item["Name"] as string ?? "").Trim();
                    if (!wanted.Contains(name)) continue;

                    // HP pads the option list with empty entries to a fixed length.
                    string[] options = (item["PossibleValues"] as string[] ?? Array.Empty<string>())
                        .Where(option => !string.IsNullOrWhiteSpace(option)).ToArray();
                    bool readOnly = Convert.ToInt32(item["IsReadOnly"] ?? 1) != 0;
                    settings[name] = new BiosSetting(name, (item["CurrentValue"] as string ?? "").Trim(), options, readOnly);
                }
            }
        } catch {
            // No HP provider (or not elevated): the card just doesn't offer anything.
        }
        return settings;
    }

    /// <summary>True when a BIOS setup password is set — the app then leaves the settings alone.</summary>
    public static bool IsSetupPasswordSet() {
        try {
            using var searcher = new ManagementObjectSearcher(Namespace, "SELECT Name, IsSet FROM HP_BIOSPassword");
            foreach (ManagementBaseObject item in searcher.Get()) {
                using (item) {
                    if (string.Equals(item["Name"] as string, "Setup Password", StringComparison.OrdinalIgnoreCase))
                        return Convert.ToInt32(item["IsSet"] ?? 0) != 0;
                }
            }
        } catch {
            // Treated as "not set"; a write would then just be refused by the BIOS.
        }
        return false;
    }

    /// <summary>
    /// Writes one of the offered settings. The BIOS answers with a status code: 0 is success,
    /// 6 means it wants a setup password. Most changes take effect on the next restart.
    /// </summary>
    public static uint Set(string name, string value) {
        if (!Offered.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"\"{name}\" isn't one of the settings this app changes.");

        using var interfaces = new ManagementClass(new ManagementScope(Namespace), new ManagementPath("HP_BIOSSettingInterface"), null);
        foreach (ManagementObject bios in interfaces.GetInstances()) {
            using (bios) {
                using ManagementBaseObject input = bios.GetMethodParameters("SetBIOSSetting");
                input["Name"] = name;
                input["Value"] = value;
                // HP's format for "no password": an empty UTF-16 marker.
                input["Password"] = "<utf-16/>";
                using ManagementBaseObject output = bios.InvokeMethod("SetBIOSSetting", input, null);
                return Convert.ToUInt32(output["Return"]);
            }
        }
        throw new InvalidOperationException("HP's BIOS settings interface isn't available.");
    }

    public static string DescribeResult(uint code) => code switch {
        0 => "",
        1 => Loc.T("The BIOS doesn't support changing this setting."),
        3 => Loc.T("The BIOS didn't respond in time."),
        5 => Loc.T("The BIOS rejected that value."),
        6 => Loc.T("The BIOS asks for a setup password for this change."),
        _ => Loc.F("The BIOS reported error {0}.", code),
    };

    /// <summary>Restarts straight into the BIOS setup screen (UEFI firmware settings).</summary>
    public static void RestartIntoBios() {
        using Process? shutdown = Process.Start(new ProcessStartInfo("shutdown.exe", "/r /fw /t 0") {
            CreateNoWindow = true, UseShellExecute = false,
        });
    }
}
