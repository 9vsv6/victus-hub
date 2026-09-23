using System.Diagnostics;
using System.IO;

namespace HpVictusControl;

/// <summary>
/// Puts a downloaded build in place of the running one. A running executable can't be overwritten,
/// but it can be renamed, so the current file is moved aside and the new one takes its path; the
/// leftover is deleted on the next start, and doubles as a copy to fall back to in the meantime.
/// </summary>
public static class SelfUpdate {

    private static string? CurrentExePath {
        get {
            string? path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? null : path;
        }
    }

    private static string? PreviousExePath =>
        CurrentExePath is string current ? Path.ChangeExtension(current, ".previous.exe") : null;

    /// <summary>False when the app can't replace itself, e.g. it's running from a folder it can't write to.</summary>
    public static bool IsSupported {
        get {
            if (CurrentExePath is not string current) return false;
            try {
                string probe = Path.Combine(Path.GetDirectoryName(current)!, $".write-test-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            } catch {
                return false;
            }
        }
    }

    /// <summary>Swaps the downloaded build in. Throws if anything goes wrong; the old build is left in place.</summary>
    public static void Apply(string downloadedExePath) {
        if (CurrentExePath is not string current || PreviousExePath is not string previous)
            throw new InvalidOperationException(Loc.T("This build can't replace itself."));
        if (!File.Exists(downloadedExePath)) throw new FileNotFoundException(Loc.T("The downloaded build is missing."), downloadedExePath);

        TryDeletePrevious();
        File.Move(current, previous);
        try {
            File.Copy(downloadedExePath, current);
        } catch {
            File.Move(previous, current); // put the running build back before giving up
            throw;
        }
    }

    /// <summary>Starts the (now replaced) executable once this process has had time to exit.</summary>
    public static void RestartAfterExit() {
        if (CurrentExePath is not string current) return;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak > nul & start \"\" \"{current}\"") {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }

    /// <summary>Removes the build left behind by an earlier update; safe to call on every start.</summary>
    public static void TryDeletePrevious() {
        try {
            if (PreviousExePath is string previous && File.Exists(previous)) File.Delete(previous);
        } catch {
            // Still running or locked — it'll go on a later start.
        }
    }
}
