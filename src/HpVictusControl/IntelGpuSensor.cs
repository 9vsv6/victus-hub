using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;

namespace HpVictusControl;

/// <summary>
/// Usage of the integrated Intel GPU from Windows' "GPU Engine" performance counters, the same
/// source Task Manager uses, so no Intel software is needed. Counter instances are named by adapter
/// LUID, which Windows records per adapter under HKLM\SOFTWARE\Microsoft\DirectX.
/// </summary>
public sealed class IntelGpuSensor {

    private const int IntelVendorId = 0x8086;

    private readonly string? _luidTag;
    private InstanceDataCollection? _previous;

    public IntelGpuSensor() => (_luidTag, Name) = FindIntelAdapter();

    public bool IsAvailable => _luidTag != null;

    public string Name { get; }

    /// <summary>Busiest 3D engine's utilization in percent; null on the first call (counters need two samples) or on failure.</summary>
    public double? TryReadUsagePercent() {
        if (_luidTag == null) return null;

        try {
            InstanceDataCollectionCollection category = new PerformanceCounterCategory("GPU Engine").ReadCategory();
            string? counterKey = category.Keys.Cast<string>()
                .FirstOrDefault(key => key.Equals("Utilization Percentage", StringComparison.OrdinalIgnoreCase));
            if (counterKey == null) return null;

            InstanceDataCollection current = category[counterKey];
            InstanceDataCollection? previous = _previous;
            _previous = current;
            if (previous == null) return null;

            // Instances are per process and engine; sum processes per engine, then report the busiest engine.
            var perEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (InstanceData sample in current.Values) {
                string name = sample.InstanceName;
                if (!name.Contains(_luidTag, StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith("engtype_3d", StringComparison.OrdinalIgnoreCase)
                    || !previous.Contains(name)) continue;

                string engine = name[name.IndexOf(_luidTag, StringComparison.OrdinalIgnoreCase)..];
                perEngine[engine] = perEngine.GetValueOrDefault(engine) + CounterSample.Calculate(previous[name].Sample, sample.Sample);
            }
            return perEngine.Count == 0 ? 0 : Math.Min(100, perEngine.Values.Max());
        } catch {
            return null;
        }
    }

    private static (string? LuidTag, string Name) FindIntelAdapter() {
        try {
            using RegistryKey? directX = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\DirectX");
            if (directX == null) return (null, "");

            foreach (string subKey in directX.GetSubKeyNames()) {
                using RegistryKey? adapter = directX.OpenSubKey(subKey);
                if (adapter?.GetValue("VendorId") is not int vendorId || vendorId != IntelVendorId) continue;
                if (adapter.GetValue("AdapterLuid") is not long luid) continue;

                string name = (adapter.GetValue("Description") as string ?? "Intel GPU").Replace("(R)", "").Replace("  ", " ").Trim();
                return ($"luid_0x{(uint)(luid >> 32):x8}_0x{(uint)luid:x8}_", name);
            }
        } catch {
            // No readable adapter info - the sensor just reports unavailable.
        }
        return (null, "");
    }
}
