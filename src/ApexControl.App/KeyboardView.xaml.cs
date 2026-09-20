using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ApexControl.App;

// The on-screen keyboard (rows of key caps). Clicking a key runs KeyCommand with that key as the parameter.
public partial class KeyboardView : UserControl
{
    public static readonly DependencyProperty RowsProperty =
        DependencyProperty.Register(nameof(Rows), typeof(IEnumerable), typeof(KeyboardView));

    public static readonly DependencyProperty KeyCommandProperty =
        DependencyProperty.Register(nameof(KeyCommand), typeof(ICommand), typeof(KeyboardView));

    public KeyboardView() => InitializeComponent();

    public IEnumerable? Rows { get => (IEnumerable?)GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public ICommand? KeyCommand { get => (ICommand?)GetValue(KeyCommandProperty); set => SetValue(KeyCommandProperty, value); }
}