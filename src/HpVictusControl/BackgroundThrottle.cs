using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HpVictusControl;

/// <summary>
/// While a game runs, puts browsers, game launchers and chat apps on low priority and Windows'
/// Efficiency mode (EcoQoS, the same thing Task Manager's "Efficiency mode" sets), so the scheduler
/// keeps them on the efficiency cores and off the game's. Everything is put back when the game ends.
/// Discord, OBS and anything recording or streaming are left alone on purpose: slowing them hurts
/// voice and captures.
/// </summary>
public sealed class BackgroundThrottle {

    private static readonly HashSet<string> Targets = new(StringComparer.OrdinalIgnoreCase) {
        // Browsers
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi",
        // Launchers that sit in the background once the game is up
        "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "EADesktop", "GalaxyClient", "upc", "UbisoftConnect",
        // Messaging
        "Teams", "ms-teams", "slack", "WhatsApp", "Telegram",
    };

    private readonly Dictionary<int, ProcessPriorityClass> _throttled = new();

    public bool IsActive { get; private set; }

    public void Start() {
        IsActive = true;
        Keep();
    }

    /// <summary>Throttles anything on the list that started since the last call.</summary>
    public void Keep() {
        if (!IsActive) return;
        foreach (Process process in Process.GetProcesses()) {
            using (process) {
                try {
                    if (!Targets.Contains(process.ProcessName) || _throttled.ContainsKey(process.Id)) continue;
                    ProcessPriorityClass original = process.PriorityClass;
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                    SetEfficiencyMode(process.Handle, true);
                    _throttled[process.Id] = original;
                } catch {
                    // Exited between listing and changing, or protected — skip it.
                }
            }
        }
    }

    public void Stop() {
        IsActive = false;
        foreach ((int pid, ProcessPriorityClass original) in _throttled) {
            try {
                using Process process = Process.GetProcessById(pid);
                process.PriorityClass = original;
                SetEfficiencyMode(process.Handle, false);
            } catch {
                // Already closed: nothing to put back.
            }
        }
        _throttled.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private const int ProcessPowerThrottling = 4;
    private const uint ExecutionSpeed = 0x1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

    // On: EcoQoS forced on. Off: control mask 0 hands the decision back to Windows, its default.
    private static void SetEfficiencyMode(IntPtr handle, bool on) {
        var state = new PowerThrottlingState {
            Version = 1,
            ControlMask = on ? ExecutionSpeed : 0,
            StateMask = on ? ExecutionSpeed : 0,
        };
        SetProcessInformation(handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
    }
}
