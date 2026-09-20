using System.Windows;
using ApexControl.App.Infrastructure;
using ApexControl.App.ViewModels;

namespace ApexControl.App;

// The compatibility report window (see CompatibilityViewModel).
public partial class CompatibilityWindow : Window
{
    private readonly MainViewModel _main;

    public CompatibilityWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _main = viewModel;
        DarkTitleBar.Apply(this);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_main.Compatibility?.Report ?? ""); _main.SetResult(true, "Report copied to the clipboard."); }
        catch (Exception ex) { _main.SetResult(false, "Could not copy the report: " + ex.Message); }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
