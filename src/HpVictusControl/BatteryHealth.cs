using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Xml;

namespace HpVictusControl;

/// <summary>What the battery can hold now compared with new, and how it's been used.</summary>
public sealed record BatteryHealthReport(
    int DesignCapacityMwh,
    int FullChargeCapacityMwh,
    int? CycleCount,
    string Chemistry,
    DateTime? HistoryStart,
    int? HistoryStartFullChargeMwh,
    double? PluggedInShare) {

    public double HealthPercent => DesignCapacityMwh > 0 ? Math.Min(100, 100.0 * FullChargeCapacityMwh / DesignCapacityMwh) : 0;
}

/// <summary>
/// Reads Windows' own battery report ("powercfg /batteryreport /xml"). WMI on this laptop reports the
/// current full-charge capacity and cycle count but fails for the design capacity, which the report
/// has — along with a few weeks of history to compare against.
/// </summary>
public static class BatteryHealth {

    public static BatteryHealthReport? TryRead() {
        string path = Path.Combine(Path.GetTempPath(), $"hpvc-battery-{Environment.ProcessId}.xml");
        try {
            using (Process? process = Process.Start(new ProcessStartInfo("powercfg.exe", $"/batteryreport /xml /output \"{path}\"") {
                       UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                   })) {
                if (process == null) return null;
                process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(15000) || !File.Exists(path)) return null;
            }

            var document = new XmlDocument();
            document.Load(path);
            var ns = new XmlNamespaceManager(document.NameTable);
            ns.AddNamespace("b", document.DocumentElement?.NamespaceURI ?? "");

            XmlNode? battery = document.SelectSingleNode("//b:Batteries/b:Battery", ns);
            if (battery == null) return null; // desktop, or no battery Windows can read

            int design = Int(battery, "b:DesignCapacity", ns) ?? 0;
            int full = Int(battery, "b:FullChargeCapacity", ns) ?? 0;
            if (design <= 0 || full <= 0) return null;

            XmlNodeList? history = document.SelectNodes("//b:History/b:HistoryEntry", ns);
            XmlElement? oldest = history?.Count > 0 ? history[0] as XmlElement : null;

            return new BatteryHealthReport(
                design, full, Int(battery, "b:CycleCount", ns),
                battery.SelectSingleNode("b:Chemistry", ns)?.InnerText ?? "",
                oldest != null && DateTime.TryParse(oldest.GetAttribute("LocalStartDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime start) ? start : null,
                oldest != null && int.TryParse(oldest.GetAttribute("FullChargeCapacity"), out int oldFull) ? oldFull : null,
                PluggedInShare(history));
        } catch {
            return null;
        } finally {
            try { File.Delete(path); } catch { /* temp file, not worth failing over */ }
        }
    }

    private static int? Int(XmlNode parent, string xpath, XmlNamespaceManager ns) =>
        int.TryParse(parent.SelectSingleNode(xpath, ns)?.InnerText, out int value) ? value : null;

    // Share of powered-on time spent on AC across the report's history (screen-on plus standby).
    private static double? PluggedInShare(XmlNodeList? history) {
        if (history == null) return null;
        TimeSpan ac = TimeSpan.Zero, dc = TimeSpan.Zero;
        foreach (XmlElement entry in history.OfType<XmlElement>()) {
            // Windows sometimes writes nonsense here — this laptop's report has one week claiming
            // 592,000 hours of standby on battery — so a value longer than its own entry is dropped.
            TimeSpan period = DateTime.TryParse(entry.GetAttribute("StartDate"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime start)
                && DateTime.TryParse(entry.GetAttribute("EndDate"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime end)
                && end > start ? end - start : TimeSpan.FromDays(7);
            TimeSpan Plausible(string attribute) {
                TimeSpan value = Duration(entry, attribute);
                return value <= period ? value : TimeSpan.Zero;
            }

            ac += Plausible("ActiveAcTime") + Plausible("CsAcTime");
            dc += Plausible("ActiveDcTime") + Plausible("CsDcTime");
        }
        TimeSpan total = ac + dc;
        return total > TimeSpan.FromHours(1) ? ac / total : null;
    }

    private static TimeSpan Duration(XmlElement entry, string attribute) {
        try {
            string value = entry.GetAttribute(attribute);
            return value.Length == 0 ? TimeSpan.Zero : XmlConvert.ToTimeSpan(value);
        } catch {
            return TimeSpan.Zero;
        }
    }
}
