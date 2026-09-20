using System.Windows;
using ApexControl.App.ViewModels;

namespace ApexControl.App;

// Lists the saved backups so one can be picked for a restore. Cancel is the default button.
public partial class RestoreWindow : Window
{
    public RestoreWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ApexControl.App.Infrastructure.DarkTitleBar.Apply(this);
        DataContext = viewModel;
    }

    private void OnRestore(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}