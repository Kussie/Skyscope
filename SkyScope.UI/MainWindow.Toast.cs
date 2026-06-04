using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SkyScope.UI;

// Clipboard-copy interactions and the top-right "Copied to clipboard" toast.
public partial class MainWindow
{
    private DispatcherTimer? _toastTimer;

    // Copies a clicked plugin-name TextBlock's text to the clipboard and shows the toast.
    // Wired to the plugin-name TextBlocks in the Settings tab (thumbnail + ignored lists).
    private void CopyPluginName_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock { Text: { Length: > 0 } text })
            ClipboardHelper.SetTextAsync(text, () => ShowToast("Copied to clipboard"));
    }

    // Fades the toast in, holds it briefly, then fades it out. Re-entrant: a new call restarts
    // the timer so rapid copies keep the toast visible rather than stacking.
    public void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastNotification.Visibility = Visibility.Visible;
        ToastNotification.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer!.Stop();
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
            fadeOut.Completed += (_, _) => ToastNotification.Visibility = Visibility.Collapsed;
            ToastNotification.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        };
        _toastTimer.Start();
    }
}
