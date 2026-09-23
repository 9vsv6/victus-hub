using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace HpVictusControl;

public partial class App : System.Windows.Application {

    // Two instances (e.g. the auto-started tray one plus a manually double-clicked one) would
    // each independently load/save settings.json — whichever saves last wins, silently
    // clobbering the other's changes. This keeps the app to a single running instance.
    private const string MutexName = "HpVictusControl-SingleInstance-9F3C2B1A";
    private const string ShowEventName = "HpVictusControl-ShowRequest-9F3C2B1A";
    // Set by an uninstall so a running copy closes before its files are removed.
    private const string ExitEventName = "HpVictusControl-ExitRequest-9F3C2B1A";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _exitEvent;

    protected override async void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);
        // First, so even a failure while starting up (like a missing DLL at sign-in) leaves a report.
        CrashLog.Initialize();
        // Before anything is built: dialogs, the window and its tray menu all read the language as they're created.
        Loc.Use(AppSettings.Load().Language);

        // The manifest doesn't demand admin, because Windows won't auto-start such apps at sign-in.
        // A normal-user launch hands off to an elevated copy and exits.
        if (!IsElevated()) {
            HandOffToElevatedInstance(e.Args);
            Shutdown();
            return;
        }

        if (e.Args.Contains(Installer.UninstallArgument)) {
            RunUninstall();
            Shutdown();
            return;
        }

        _singleInstanceMutex = new Mutex(true, MutexName, out bool isNewInstance);
        if (!isNewInstance) {
            // Another instance already owns the mutex — ask it to show itself, then exit.
            try {
                using EventWaitHandle existingEvent = EventWaitHandle.OpenExisting(ShowEventName);
                existingEvent.Set();
            } catch {
                // Nothing more we can do if that fails — just exit quietly either way.
            }
            Shutdown();
            return;
        }

        // Only the logon task passes --minimized to a new instance; respect the Task Manager switch.
        if (e.Args.Contains("--minimized") && StartupManager.IsDisabledInTaskManager()) {
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);

        DispatcherUnhandledException += (_, args) => {
            CrashLog.Write(args.Exception, fatal: false);
            MessageBox.Show(
                $"Unexpected error: {args.Exception.Message}",
                "HP Victus Control",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        bool startMinimized = e.Args.Contains("--minimized");

        if (startMinimized) {
            // Launching this early in logon (via the Run key) can race the taskbar/notification
            // area still initializing, silently preventing the tray icon from ever appearing.
            // A short wait here avoids that — imperceptible for a background start anyway.
            await Task.Delay(TimeSpan.FromSeconds(8));
        }

        var window = new MainWindow();

        var watcherThread = new Thread(() => {
            while (_showEvent.WaitOne()) {
                Dispatcher.Invoke(() => {
                    window.ShowInTaskbar = true;
                    window.Show();
                    window.WindowState = WindowState.Normal;
                    window.Activate();
                });
            }
        }) { IsBackground = true };
        watcherThread.Start();

        var exitWatcher = new Thread(() => {
            if (_exitEvent.WaitOne()) Dispatcher.Invoke(window.ExitForUninstall);
        }) { IsBackground = true };
        exitWatcher.Start();

        if (startMinimized) {
            window.WindowState = WindowState.Minimized;
            window.ShowInTaskbar = false;
            window.Show();
            window.Hide();
        } else {
            window.Show();
        }
    }

    private static bool IsElevated() {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void HandOffToElevatedInstance(string[] args) {
        // The Run entry only keeps the app listed in Task Manager; the logon task does the starting.
        if (args.Contains(StartupManager.RunEntryArgument)) return;

        // Brings a running instance forward without a UAC prompt: the copy the task starts signals
        // that window and exits. Not for an uninstall, which has to reach the elevated copy itself.
        if (!args.Contains(Installer.UninstallArgument) && IsInstanceRunning() && StartupManager.TryRunStartupTask()) return;

        try {
            using Process? elevated = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', args.Select(arg => $"\"{arg}\""))
            });
        } catch (Win32Exception) {
            // The UAC prompt was declined.
        }
    }

    // Run from Settings → Apps → Installed apps → Uninstall (and from the app's own Uninstall button,
    // which relaunches with this argument): asks, closes a running copy, restores the fans and
    // power plan, then removes the app.
    private static void RunUninstall() {
        MessageBoxResult confirmed = Loc.Message(null,
            Loc.T("Uninstall HP Victus Control?\n\nThe fans and performance mode go back to the BIOS defaults, and the startup task, shortcut and app files are removed."),
            "HP Victus Control", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmed != MessageBoxResult.Yes) return;

        bool removeSettings = Loc.Message(null,
            Loc.T("Also delete your settings and game profiles?\n\nKeep them if you might install the app again."),
            "HP Victus Control", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        // A running copy holds its files open; ask it to close and wait for it to let go.
        try {
            using EventWaitHandle exitRequest = EventWaitHandle.OpenExisting(ExitEventName);
            exitRequest.Set();
            using var running = new Mutex(false, MutexName);
            try {
                running.WaitOne(TimeSpan.FromSeconds(10));
            } catch (AbandonedMutexException) {
                // It exited while holding it — that's the goal.
            }
        } catch (WaitHandleCannotBeOpenedException) {
            // Not running.
        }

        Installer.RestoreHardwareDefaults();
        Installer.Uninstall(removeSettings);

        Loc.Message(null, Loc.T("HP Victus Control has been uninstalled."), "HP Victus Control",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static bool IsInstanceRunning() {
        try {
            if (!Mutex.TryOpenExisting(MutexName, out Mutex? existing)) return false;
            existing.Dispose();
            return true;
        } catch (UnauthorizedAccessException) {
            // Held by an elevated instance, which a normal-user process isn't allowed to open.
            return true;
        }
    }
}
