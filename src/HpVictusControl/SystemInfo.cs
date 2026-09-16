using System.Globalization;
using System.Management;
using System.Text;
using Microsoft.Win32;

namespace HpVictusControl;

/// <summary>One line on the System page, e.g. ("BIOS", "F.08 · Apr 14, 2026").</summary>
public sealed record SystemInfoItem(string Label, string Value);

/// <summary>
/// Gathers the facts people get asked for in support chats and warranty checks, all from WMI and the
/// registry so nothing extra needs installing. Every lookup is independent and best-effort: one that
/// fails just leaves its line out.
/// </summary>
public static class SystemInfo {

    public static List<SystemInfoItem> Collect() {
        var items = new List<SystemInfoItem>();

        void Add(string label, Func<string?> read) {
            try {
                string? value = read();
                if (!string.IsNullOrWhiteSpace(value)) items.Add(new SystemInfoItem(label, value.Trim()));
            } catch {
                // Leave the line out rather than show an error for one missing fact.
            }
        }

        Add("Model", () => First("Win32_ComputerSystem", o => $"{o["Manufacturer"]} {o["Model"]}".Trim()));
        Add("Product number", () => First("Win32_ComputerSystem", o => o["SystemSKUNumber"] as string));
        Add("Serial number", () => First("Win32_BIOS", o => o["SerialNumber"] as string));
        Add("BIOS", () => First("Win32_BIOS", o => {
            string version = o["SMBIOSBIOSVersion"] as string ?? "";
            string? date = FormatWmiDate(o["ReleaseDate"] as string);
            return date == null ? version : $"{version}  ·  {date}";
        }));
        Add("Windows", DescribeWindows);
        Add("Processor", () => First("Win32_Processor", o =>
            $"{(o["Name"] as string)?.Trim()}  ·  {o["NumberOfCores"]} cores, {o["NumberOfLogicalProcessors"]} threads"));
        Add("Graphics", DescribeGraphics);
        Add("Memory", DescribeMemory);
        Add("Storage", DescribeStorage);
        Add("Display", DescribeDisplay);

        return items;
    }

    /// <summary>Plain text for pasting into a support chat or forum post.</summary>
    public static string ToClipboardText(IEnumerable<SystemInfoItem> items) {
        var text = new StringBuilder();
        foreach (SystemInfoItem item in items) text.AppendLine($"{item.Label}: {item.Value.Replace(Environment.NewLine, " / ")}");
        return text.ToString();
    }

    private static string? DescribeWindows() {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string? caption = First("Win32_OperatingSystem", o => (o["Caption"] as string)?.Replace("Microsoft ", ""));
        string? display = key?.GetValue("DisplayVersion") as string;
        string? build = key?.GetValue("CurrentBuildNumber") as string;
        object? ubr = key?.GetValue("UBR");
        string buildText = build == null ? "" : ubr == null ? $"build {build}" : $"build {build}.{ubr}";
        return string.Join("  ·  ", new[] { caption, display, buildText }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static string DescribeGraphics() {
        var lines = new List<string>();
        foreach (ManagementBaseObject gpu in Query("SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController")) {
            using (gpu) {
                string name = gpu["Name"] as string ?? "";
                string version = gpu["DriverVersion"] as string ?? "";
                // NVIDIA's Windows driver version hides the version people know (32.0.16.1692 = 616.92).
                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                    && Updates.NvidiaDriverUpdateService.TryConvertToMarketingVersion(version, out double marketing)) {
                    version = $"{marketing.ToString("0.00", CultureInfo.InvariantCulture)} ({version})";
                }
                string? date = FormatWmiDate(gpu["DriverDate"] as string);
                lines.Add($"{name}  ·  driver {version}{(date == null ? "" : $", {date}")}");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string? DescribeMemory() {
        var sticks = new List<(ulong Bytes, uint Speed, int Type, string Maker)>();
        foreach (ManagementBaseObject stick in Query("SELECT Capacity, ConfiguredClockSpeed, Speed, SMBIOSMemoryType, Manufacturer FROM Win32_PhysicalMemory")) {
            using (stick) {
                uint speed = Convert.ToUInt32(stick["ConfiguredClockSpeed"] ?? stick["Speed"] ?? 0u);
                sticks.Add((Convert.ToUInt64(stick["Capacity"] ?? 0UL), speed, Convert.ToInt32(stick["SMBIOSMemoryType"] ?? 0), stick["Manufacturer"] as string ?? ""));
            }
        }
        if (sticks.Count == 0) return null;

        ulong total = (ulong)sticks.Sum(s => (decimal)s.Bytes);
        string type = sticks[0].Type switch { 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 35 => "LPDDR5", _ => "" };
        uint speedMhz = sticks.Max(s => s.Speed);
        string layout = string.Join(" + ", sticks.Select(s => $"{s.Bytes / (1024 * 1024 * 1024)} GB {s.Maker.Trim()}".Trim()));
        string header = string.Join(" ", new[] { $"{total / (1024 * 1024 * 1024)} GB", type, speedMhz > 0 ? $"{speedMhz} MHz" : "" }.Where(s => s.Length > 0));
        return $"{header}  ·  {layout}";
    }

    private static string DescribeStorage() {
        var lines = new List<string>();
        using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, Size, MediaType, BusType FROM MSFT_PhysicalDisk");
        foreach (ManagementBaseObject disk in searcher.Get()) {
            using (disk) {
                ulong size = Convert.ToUInt64(disk["Size"] ?? 0UL);
                string media = Convert.ToInt32(disk["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", _ => "" };
                string bus = Convert.ToInt32(disk["BusType"] ?? 0) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", _ => "" };
                string kind = string.Join(" ", new[] { bus, media }.Where(s => s.Length > 0));
                lines.Add($"{disk["FriendlyName"]}  ·  {size / 1_000_000_000} GB{(kind.Length > 0 ? $" {kind}" : "")}");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string? DescribeDisplay() {
        System.Windows.Forms.Screen? screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return null;
        int? hz = DisplayRefreshRate.GetMaxRefreshRate();
        return $"{screen.Bounds.Width} × {screen.Bounds.Height}{(hz.HasValue ? $"  ·  up to {hz} Hz" : "")}";
    }

    private static string? FormatWmiDate(string? wmiDate) {
        if (string.IsNullOrWhiteSpace(wmiDate)) return null;
        try {
            return ManagementDateTimeConverter.ToDateTime(wmiDate).ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        } catch {
            return null;
        }
    }

    private static string? First(string wmiClass, Func<ManagementBaseObject, string?> read) {
        foreach (ManagementBaseObject item in Query($"SELECT * FROM {wmiClass}")) {
            using (item) return read(item);
        }
        return null;
    }

    private static IEnumerable<ManagementBaseObject> Query(string wql) {
        using var searcher = new ManagementObjectSearcher(wql);
        foreach (ManagementBaseObject item in searcher.Get()) yield return item;
    }
}
