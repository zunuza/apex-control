using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using ApexControl.App.Infrastructure;
using ApexControl.App.ViewModels;

namespace ApexControl.App;

// The macro editor pop-up. While "Record" is on, the keys pressed in THIS window (only) are captured with their timing
// into the timeline; nothing is read from other programs. The window closes itself after a successful save.
public partial class MacroEditorWindow : Window
{
    private readonly MacrosViewModel _macros;

    public MacroEditorWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _macros = viewModel.Macros;
        DarkTitleBar.Apply(this);

        _macros.WriteFinished += OnWriteFinished;
        _macros.PropertyChanged += OnMacrosChanged;
        _macros.Events.CollectionChanged += OnEventsChanged;
        Closed += (_, _) =>
        {
            _macros.WriteFinished -= OnWriteFinished;
            _macros.PropertyChanged -= OnMacrosChanged;
            _macros.Events.CollectionChanged -= OnEventsChanged;
            _macros.StopRecording();
        };
    }

    // While recording, keep the newest block in view.
    private void OnEventsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_macros.IsRecording) Dispatcher.BeginInvoke(new Action(TimelineScroll.ScrollToEnd), System.Windows.Threading.DispatcherPriority.Background);
    }

    private static TimeSpan Now() => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);

    // When recording starts, take keyboard focus away from the Record button so a recorded Space or Enter can't press it.
    private void OnMacrosChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MacrosViewModel.IsRecording) && _macros.IsRecording)
            Dispatcher.BeginInvoke(new Action(() => { Focus(); Keyboard.Focus(this); }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e) => Capture(e, down: true);
    private void OnPreviewKeyUp(object sender, KeyEventArgs e) => Capture(e, down: false);

    private void Capture(KeyEventArgs e, bool down)
    {
        if (!_macros.IsRecording) return;
        e.Handled = true;                       // while recording, keys are data, not commands (no Enter/Space clicks, no Tab, no Alt menu)
        if (down && e.IsRepeat) return;

        Key key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };
        if (KeyMap.TryGetHid(key, out byte hid)) _macros.RecordKey(hid, down, Now());
        else if (down) _macros.NoteUnsupportedKey(key.ToString());
    }

    // Leaving the window mid-recording would lose the key releases, so recording ends there.
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_macros.IsRecording) _macros.StopRecording("Recording stopped because you left this window.");
    }

    private void OnWriteFinished(bool ok)
    {
        if (ok) Dispatcher.BeginInvoke(new Action(Close));
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
