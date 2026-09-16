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

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;

    protected override async void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);

        // The manifest doesn't demand admin, because Windows won't auto-start such apps at sign-in.
        // A normal-user launch hands off to an elevated copy and exits.
        if (!IsElevated()) {
            HandOffToElevatedInstance(e.Args);
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

        DispatcherUnhandledException += (_, args) => {
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
        // that window and exits.
        if (IsInstanceRunning() && StartupManager.TryRunStartupTask()) return;

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
