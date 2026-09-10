using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace HpVictusControl;

public partial class App : System.Windows.Application {

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) => {
            MessageBox.Show(
                $"Unexpected error: {args.Exception.Message}",
                "HP Victus Control",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        var window = new MainWindow();
        window.Show();
    }
}
