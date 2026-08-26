using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace SkyScope.UI;

// Borderless popup showing a larger version of a clicked portrait. Closes on click, Escape, or the
// close button. Sized to fit within the screen's work area so a large source image never overflows.
public partial class ImagePreviewWindow : Window
{
    public ImagePreviewWindow(string imagePath, string caption)
    {
        InitializeComponent();

        CaptionText.Text = caption;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource   = new Uri(imagePath, UriKind.Absolute);
        bitmap.EndInit();
        PreviewImage.Source = bitmap;

        var workArea = SystemParameters.WorkArea;
        PreviewImage.MaxWidth  = workArea.Width  * 0.85;
        PreviewImage.MaxHeight = workArea.Height * 0.80;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
