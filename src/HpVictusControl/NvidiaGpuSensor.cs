using System.Diagnostics;
using System.IO;

namespace HpVictusControl;

/// <summary>
/// Reads discrete GPU temperature via nvidia-smi, which ships with the NVIDIA display
/// driver on any machine with an NVIDIA GPU — no extra driver or install needed. Not
/// available on non-NVIDIA (e.g. AMD/Intel-only) hardware, in which case callers should
/// treat it as unavailable and show "N/A".
/// </summary>
public static class NvidiaGpuSensor {

    private static readonly string? SmiPath = FindNvidiaSmi();

    public static bool IsAvailable => SmiPath != null;

    private static string? FindNvidiaSmi() {
        string candidate = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Temperature (Celsius), utilization (percent), power draw (watts), and core clock
    /// (MHz), read in a single nvidia-smi call.</summary>
    public static async Task<(double? Temperature, double? Utilization, double? PowerDrawWatts, double? ClockMHz)> TryReadStatsAsync() {
        if (SmiPath == null) return (null, null, null, null);

        try {
            var psi = new ProcessStartInfo(SmiPath,
                "--query-gpu=temperature.gpu,utilization.gpu,power.draw,clocks.gr --format=csv,noheader,nounits") {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process? process = Process.Start(psi);
            if (process == null) return (null, null, null, null);

            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            string[] parts = output.Split(',', StringSplitOptions.TrimEntries);
            double? temperature = parts.Length > 0 && double.TryParse(parts[0], out double t) ? t : null;
            double? utilization = parts.Length > 1 && double.TryParse(parts[1], out double u) ? u : null;
            double? powerDraw = parts.Length > 2 && double.TryParse(parts[2], out double p) ? p : null;
            double? clockMhz = parts.Length > 3 && double.TryParse(parts[3], out double c) ? c : null;
            return (temperature, utilization, powerDraw, clockMhz);
        } catch {
            return (null, null, null, null);
        }
    }
}
