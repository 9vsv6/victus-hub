using System.Linq;
using Microsoft.Win32;

namespace HpVictusControl;

/// <summary>
/// The Realtek speaker driver powers the audio chip down after a few seconds of silence, so the next
/// sound (like a UAC prompt's) starts late while it wakes. Setting the driver's idle timers to 0 keeps
/// it powered. The driver reads these when it starts, so a change applies after a Windows restart.
/// </summary>
public static class SpeakerPowerSettings {

    private const string AudioClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e96c-e325-11ce-bfc1-08002be10318}";
    private static readonly string[] TimerNames = { "ConservationIdleTime", "PerformanceIdleTime" };

    public static bool IsSupported => FindPowerSettingsKeys().Count > 0;

    public static bool IsKeptAwake() {
        List<string> paths = FindPowerSettingsKeys();
        return paths.Count > 0 && paths.All(path => {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path);
            return TimerNames.All(name => ReadSeconds(key, name) == 0);
        });
    }

    public static int? GetIdleSeconds() {
        foreach (string path in FindPowerSettingsKeys()) {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path);
            int? seconds = ReadSeconds(key, TimerNames[0]);
            if (seconds.HasValue) return seconds;
        }
        return null;
    }

    /// <summary>Requires admin.</summary>
    public static void SetIdleSeconds(int seconds) {
        byte[] value = BitConverter.GetBytes(seconds);
        foreach (string path in FindPowerSettingsKeys()) {
            using RegistryKey key = Registry.LocalMachine.OpenSubKey(path, writable: true)
                ?? throw new InvalidOperationException($"Couldn't open HKLM\\{path}.");
            foreach (string name in TimerNames) key.SetValue(name, value, RegistryValueKind.Binary);
        }
    }

    private static int? ReadSeconds(RegistryKey? key, string name) =>
        key?.GetValue(name) is byte[] { Length: >= 4 } data ? BitConverter.ToInt32(data, 0) : null;

    private static List<string> FindPowerSettingsKeys() {
        var paths = new List<string>();
        using RegistryKey? audioClass = Registry.LocalMachine.OpenSubKey(AudioClassKey);
        if (audioClass == null) return paths;

        foreach (string device in audioClass.GetSubKeyNames().Where(name => name.Length == 4 && name.All(char.IsDigit))) {
            try {
                using RegistryKey? deviceKey = audioClass.OpenSubKey(device);
                if (deviceKey?.GetValue("DriverDesc") is not string description
                    || !description.Contains("Realtek", StringComparison.OrdinalIgnoreCase)) continue;

                using RegistryKey? power = deviceKey.OpenSubKey("PowerSettings");
                if (power?.GetValue(TimerNames[0]) is byte[]) paths.Add($@"{AudioClassKey}\{device}\PowerSettings");
            } catch {
                // Skip a device key this account can't read.
            }
        }
        return paths;
    }
}
