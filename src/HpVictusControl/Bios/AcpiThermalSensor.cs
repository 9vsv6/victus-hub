using System.Management;

namespace HpVictusControl.Bios;

/// <summary>
/// Reads temperature via Windows' built-in ACPI thermal zone WMI class
/// (root\wmi, MSAcpi_ThermalZoneTemperature). No extra driver required — this is a
/// standard Windows-exposed sensor, used as a fallback on hardware where the HP BIOS
/// command in <see cref="HpBiosClient"/> reports a zeroed-out (unpopulated) value.
/// </summary>
public static class AcpiThermalSensor {

    /// <summary>Highest reported thermal zone temperature, in Celsius, if any zone is available.</summary>
    public static bool TryRead(out double celsius) {
        celsius = 0;

        try {
            using var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
            using ManagementObjectCollection results = searcher.Get();

            double? highest = null;
            foreach (ManagementBaseObject zone in results) {
                using (zone) {
                    // Reported in tenths of a Kelvin.
                    double tenthsKelvin = Convert.ToDouble(zone["CurrentTemperature"]);
                    double zoneCelsius = tenthsKelvin / 10.0 - 273.15;
                    if (highest is null || zoneCelsius > highest) highest = zoneCelsius;
                }
            }

            if (highest is null) return false;
            celsius = highest.Value;
            return true;
        } catch {
            return false;
        }
    }
}
