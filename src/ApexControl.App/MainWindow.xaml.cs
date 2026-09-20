using System.Windows;
using System.Windows.Input;
using ApexControl.App.Infrastructure;
using ApexControl.App.ViewModels;

namespace ApexControl.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        DarkTitleBar.Apply(this);
        viewModel.Macros.OpenEditorRequested += OpenMacroEditor;
    }

    private void OnCompatibilityClick(object sender, RoutedEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        vm.OpenCompatibility();
        new CompatibilityWindow(vm) { Owner = this }.ShowDialog();
    }

    // The macro editor is a pop-up over this window.
    private void OpenMacroEditor()
    {
        var vm = (MainViewModel)DataContext;
        var editor = new MacroEditorWindow(vm) { Owner = this };
        editor.ShowDialog();
        vm.Macros.StopRecording();
    }

    public System.Windows.Controls.TabControl TabControl => Tabs;

    // The Ko-fi link in the title bar opens in the default browser (fixed address from the XAML, nothing else is sent).
    private void OnLinkNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { /* no browser available: nothing to do */ }
        e.Handled = true;
    }

    // Double-clicking a profile in the sidebar switches the keyboard to it, like GG. (A single click does nothing,
    // so a stray click can't change the keyboard.)
    private void OnProfileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        var vm = (MainViewModel)DataContext;
        if (((FrameworkElement)sender).DataContext is ProfileCardViewModel card && !card.IsEditing && vm.SwitchProfileCommand.CanExecute(card.Slot))
            vm.SwitchProfileCommand.Execute(card.Slot);
        e.Handled = true;
    }

    // Inline rename: the box appears, takes focus with its text selected; Enter keeps the name, Esc drops it, clicking away keeps it.
    private void OnRenameBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box && box.IsVisible)
            box.Dispatcher.BeginInvoke(new Action(() => { box.Focus(); box.SelectAll(); }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ProfileCardViewModel card) return;
        if (e.Key == Key.Enter) { card.CommitRename(); e.Handled = true; }
        else if (e.Key == Key.Escape) { card.CancelRename(); e.Handled = true; }
    }

    private void OnRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ProfileCardViewModel { IsEditing: true } card) card.CommitRename();
    }

    // "Restore...": pick a saved copy of Config 1; the view model then asks for confirmation before writing anything.
    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        if (!vm.CanUseHardware) { vm.SetResult(false, "Restoring needs the keyboard connected, GG closed and nothing else running."); return; }
        var dialog = new RestoreWindow(vm) { Owner = this };
        if (dialog.ShowDialog() == true) vm.RestoreCommand.Execute(null);
    }
}