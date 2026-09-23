using System.IO;
using System.Text;

namespace HpVictusControl;

/// <summary>
/// Writes unexpected errors to %APPDATA%\HP Victus Control\crash.log, and remembers a crash that
/// closed the app so the next launch can say so. Without it a crash at sign-in (like a missing DLL)
/// just looks like the app never started.
/// </summary>
public static class CrashLog {

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HP Victus Control");

    public static string LogPath { get; } = Path.Combine(Folder, "crash.log");

    // Present only after a crash that ended the app; removed once the next launch has shown it.
    private static readonly string PendingPath = Path.Combine(Folder, "last_crash.txt");

    // Enough for dozens of reports; older ones are trimmed so the file can't grow without limit.
    private const int MaxLogBytes = 256 * 1024;

    /// <summary>Call first thing at start-up, before anything that could fail.</summary>
    public static void Initialize() {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => {
            if (args.ExceptionObject is Exception exception) Write(exception, fatal: args.IsTerminating);
        };
        TaskScheduler.UnobservedTaskException += (_, args) => {
            Write(args.Exception, fatal: false);
            args.SetObserved();
        };
    }

    /// <summary>Records an error. Fatal ones are also flagged for the next launch to report.</summary>
    public static void Write(Exception exception, bool fatal) {
        try {
            Directory.CreateDirectory(Folder);
            string report = Format(exception, fatal);
            TrimIfLarge();
            File.AppendAllText(LogPath, report + Environment.NewLine, Encoding.UTF8);
            if (fatal) File.WriteAllText(PendingPath, report, Encoding.UTF8);
        } catch {
            // Logging must never be the thing that fails.
        }
    }

    /// <summary>The report of a crash that closed the app last time, if any — returned once.</summary>
    public static string? TakePendingCrash() {
        try {
            if (!File.Exists(PendingPath)) return null;
            string report = File.ReadAllText(PendingPath, Encoding.UTF8);
            File.Delete(PendingPath);
            return report;
        } catch {
            return null;
        }
    }

    private static string Format(Exception exception, bool fatal) {
        var text = new StringBuilder();
        text.AppendLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {(fatal ? "CRASH" : "error")}");
        text.AppendLine($"App {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}  ·  {Environment.OSVersion.VersionString}");
        text.AppendLine($"Running from {Environment.ProcessPath}");
        text.AppendLine(exception.ToString());
        return text.ToString();
    }

    private static void TrimIfLarge() {
        var log = new FileInfo(LogPath);
        if (!log.Exists || log.Length < MaxLogBytes) return;
        string text = File.ReadAllText(LogPath, Encoding.UTF8);
        File.WriteAllText(LogPath, text[(text.Length / 2)..], Encoding.UTF8);
    }
}
