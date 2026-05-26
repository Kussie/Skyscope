using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace SkyScope.UI;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException         += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        bool createdNew;
        try
        {
            _mutex = new Mutex(initiallyOwned: true, "SkyScope_SingleInstance", out createdNew);
        }
        catch (AbandonedMutexException)
        {
            // A previous crash left the mutex abandoned — we effectively own it now.
            createdNew = true;
        }

        if (!createdNew)
        {
            MessageBox.Show("SkyScope is already running.", "SkyScope",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _mutex?.Dispose();
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

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatalError(e.Exception);
        e.Handled = true;
        Current.Shutdown(1);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            ShowFatalError(ex);
    }

    private static void ShowFatalError(Exception ex)
    {
        MessageBox.Show(
            $"SkyScope encountered an unexpected error and needs to close.\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
            "SkyScope — Unexpected Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
