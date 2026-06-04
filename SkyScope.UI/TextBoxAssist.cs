using System.Windows;
using System.Windows.Controls;

namespace SkyScope.UI;

// Attached behavior for the embedded "clear" (×) button used inside text boxes. Set
// TextBoxAssist.ClearTarget on the button to the TextBox it should clear; clicking it empties and
// re-focuses that box. Pairs with the "ClearTextButton" style in App.xaml.
public static class TextBoxAssist
{
    public static readonly DependencyProperty ClearTargetProperty =
        DependencyProperty.RegisterAttached(
            "ClearTarget", typeof(TextBox), typeof(TextBoxAssist),
            new PropertyMetadata(null, OnClearTargetChanged));

    public static void SetClearTarget(DependencyObject o, TextBox? value) => o.SetValue(ClearTargetProperty, value);
    public static TextBox? GetClearTarget(DependencyObject o) => (TextBox?)o.GetValue(ClearTargetProperty);

    private static void OnClearTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Button btn) return;
        btn.Click -= OnClearClick;
        if (e.NewValue is TextBox) btn.Click += OnClearClick;
    }

    private static void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && GetClearTarget(btn) is TextBox tb)
        {
            tb.Clear();
            tb.Focus();
        }
    }
}
