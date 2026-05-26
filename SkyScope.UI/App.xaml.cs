using System.Threading;
using System.Windows;

namespace SkyScope.UI;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(initiallyOwned: true, "SkyScope_SingleInstance", out bool createdNew);

        if (!createdNew)
        {
            MessageBox.Show("SkyScope is already running.", "SkyScope",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _mutex.Dispose();
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
