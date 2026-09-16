using System.Diagnostics;
using System.IO;

namespace HpVictusControl;

/// <summary>
/// "Game boost" for a running game: raises the game's CPU priority and closes OneDrive so its
/// syncing doesn't compete for disk, CPU and bandwidth, then puts both back when the game ends.
/// Deliberately limited to changes that undo themselves or are undone here: nothing that could be
/// left switched off if the app or the PC crashed mid-game (no pausing Windows Update, no services).
/// </summary>
public sealed class GameBoost {

    private readonly HashSet<int> _boostedProcessIds = new();
    private string? _closedOneDrivePath;

    public bool IsActive { get; private set; }

    public void Start() {
        IsActive = true;
        CloseOneDrive();
    }

    /// <summary>Raises priority on every process belonging to the game. Safe to call every poll tick:
    /// games often start a second process after their launcher, and already-boosted ones are skipped.</summary>
    public void BoostProcesses(IEnumerable<Process> gameProcesses) {
        if (!IsActive) return;
        foreach (Process process in gameProcesses) {
            // One attempt per process, successful or not. Anti-cheat drivers such as BattlEye (GTA Online)
            // refuse outside access to the game, and asking again every poll tick would achieve nothing
            // except repeatedly knocking on a protected process.
            if (!_boostedProcessIds.Add(process.Id)) continue;
            try {
                if (process.PriorityClass is ProcessPriorityClass.Normal or ProcessPriorityClass.BelowNormal) {
                    // High rather than Realtime: Realtime can starve input and audio.
                    process.PriorityClass = ProcessPriorityClass.High;
                }
            } catch {
                // Refused by anti-cheat, or the process already exited — leave it at its normal priority.
            }
        }
    }

    public void Stop() {
        if (!IsActive) return;
        IsActive = false;

        // Only matters if boost is switched off while the game is still running.
        foreach (int id in _boostedProcessIds) {
            try {
                using Process process = Process.GetProcessById(id);
                if (process.PriorityClass == ProcessPriorityClass.High) process.PriorityClass = ProcessPriorityClass.Normal;
            } catch {
                // Exited with the game, which is the usual case.
            }
        }
        _boostedProcessIds.Clear();

        ReopenOneDrive();
    }

    private void CloseOneDrive() {
        if (_closedOneDrivePath != null) return;
        foreach (Process process in Process.GetProcessesByName("OneDrive")) {
            using (process) {
                try {
                    string? path = process.MainModule?.FileName;
                    if (path == null || !File.Exists(path)) continue;

                    // OneDrive's own clean exit: it finishes the file it's on instead of being killed mid-write.
                    using Process? shutdown = Process.Start(new ProcessStartInfo(path, "/shutdown") { UseShellExecute = false, CreateNoWindow = true });
                    shutdown?.WaitForExit(10000);
                    _closedOneDrivePath = path;
                    return;
                } catch {
                    // Couldn't read its path — leave OneDrive alone rather than kill it blindly.
                }
            }
        }
    }

    private void ReopenOneDrive() {
        string? path = _closedOneDrivePath;
        _closedOneDrivePath = null;
        if (path == null) return;

        Process[] alreadyRunning = Process.GetProcessesByName("OneDrive");
        foreach (Process process in alreadyRunning) process.Dispose();
        if (alreadyRunning.Length > 0) return;

        try {
            // This app runs as administrator, and OneDrive refuses to run elevated. Asking Explorer to
            // start it runs it as the signed-in user instead, the same as it normally starts at sign-in.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        } catch {
            // It starts again at the next sign-in anyway.
        }
    }
}
