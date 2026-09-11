using System.Threading;
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

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);

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

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        DispatcherUnhandledException += (_, args) => {
            MessageBox.Show(
                $"Unexpected error: {args.Exception.Message}",
                "HP Victus Control",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

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

        if (e.Args.Contains("--minimized")) {
            window.WindowState = WindowState.Minimized;
            window.ShowInTaskbar = false;
            window.Show();
            window.Hide();
        } else {
            window.Show();
        }
    }
}
