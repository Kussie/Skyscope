using System;
using System.Windows;

namespace SkyScope.UI;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        try
        {
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"SkyScope failed to start:\n\n{ex.GetType().Name}: {ex.Message}",
                "SkyScope — Startup Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
