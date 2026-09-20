using System.Windows;

namespace ApexControl.App;

// A modal "here is exactly what will be written" dialog. Cancel is the default button, so Enter never writes.
public partial class ConfirmWriteWindow : Window
{
    public ConfirmWriteWindow(string caption, string summary, string details)
    {
        InitializeComponent();
        ApexControl.App.Infrastructure.DarkTitleBar.Apply(this);
        Title = caption;
        SummaryText.Text = summary;
        DetailsText.Text = details;
        WriteButton.Content = caption == "Remove macro" ? "Remove from keyboard" : caption.StartsWith("Restore") ? "Restore to keyboard" : caption.StartsWith("Read") ? "Read the keyboard" : "Write to keyboard";
        if (string.IsNullOrWhiteSpace(details))
        {
            DetailsCard.Visibility = Visibility.Collapsed;   // a plain question: no byte listing to show
            Height = 320;
        }
    }

    private void OnWrite(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
