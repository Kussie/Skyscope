using System.Windows;
using System.Windows.Controls;

namespace SkyScope.UI;

public partial class CodeContextBlock : UserControl
{
    public static readonly DependencyProperty HasPrecedingLineProperty =
        DependencyProperty.Register(nameof(HasPrecedingLine), typeof(bool),   typeof(CodeContextBlock));
    public static readonly DependencyProperty PrecedingLineNumberProperty =
        DependencyProperty.Register(nameof(PrecedingLineNumber), typeof(int),  typeof(CodeContextBlock));
    public static readonly DependencyProperty PrecedingLineProperty =
        DependencyProperty.Register(nameof(PrecedingLine), typeof(string),    typeof(CodeContextBlock));
    public static readonly DependencyProperty LineNumberProperty =
        DependencyProperty.Register(nameof(LineNumber), typeof(int),          typeof(CodeContextBlock));
    public static readonly DependencyProperty LineTextProperty =
        DependencyProperty.Register(nameof(LineText), typeof(string),         typeof(CodeContextBlock), new PropertyMetadata(""));
    public static readonly DependencyProperty HasFollowingLineProperty =
        DependencyProperty.Register(nameof(HasFollowingLine), typeof(bool),   typeof(CodeContextBlock));
    public static readonly DependencyProperty FollowingLineNumberProperty =
        DependencyProperty.Register(nameof(FollowingLineNumber), typeof(int),  typeof(CodeContextBlock));
    public static readonly DependencyProperty FollowingLineProperty =
        DependencyProperty.Register(nameof(FollowingLine), typeof(string),    typeof(CodeContextBlock));

    public bool    HasPrecedingLine    { get => (bool)GetValue(HasPrecedingLineProperty);    set => SetValue(HasPrecedingLineProperty,    value); }
    public int     PrecedingLineNumber { get => (int)GetValue(PrecedingLineNumberProperty);  set => SetValue(PrecedingLineNumberProperty,  value); }
    public string? PrecedingLine       { get => (string?)GetValue(PrecedingLineProperty);    set => SetValue(PrecedingLineProperty,       value); }
    public int     LineNumber          { get => (int)GetValue(LineNumberProperty);           set => SetValue(LineNumberProperty,          value); }
    public string  LineText            { get => (string)GetValue(LineTextProperty);          set => SetValue(LineTextProperty,            value); }
    public bool    HasFollowingLine    { get => (bool)GetValue(HasFollowingLineProperty);    set => SetValue(HasFollowingLineProperty,    value); }
    public int     FollowingLineNumber { get => (int)GetValue(FollowingLineNumberProperty);  set => SetValue(FollowingLineNumberProperty,  value); }
    public string? FollowingLine       { get => (string?)GetValue(FollowingLineProperty);    set => SetValue(FollowingLineProperty,       value); }

    public CodeContextBlock() => InitializeComponent();
}
