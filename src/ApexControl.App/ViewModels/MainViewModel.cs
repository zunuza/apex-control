using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using ApexControl.App.Backend;
using ApexControl.App.Infrastructure;
using ApexControl.Core;

namespace ApexControl.App.ViewModels;

public sealed class ProfileCardViewModel : ObservableObject
{
    public const int MaxNameLength = 24;

    private readonly Action _namesChanged;
    private string? _nickname;
    private string _editText = "";
    private bool _switchedHere, _editing;

    public ProfileCardViewModel(int slot, Action namesChanged)
    {
        Slot = slot;
        _namesChanged = namesChanged;
        BeginRenameCommand = new RelayCommand(_ => BeginRename());
    }

    public int Slot { get; }
    public string DefaultName => $"Config {Slot}";

    // What the sidebar shows: the nickname if there is one, otherwise "Config N".
    public string Title => _nickname ?? DefaultName;
    public string? Nickname
    {
        get => _nickname;
        set { if (Set(ref _nickname, value)) Raise(nameof(Title)); }
    }

    public RelayCommand BeginRenameCommand { get; }

    public bool IsEditing { get => _editing; private set { if (Set(ref _editing, value)) Raise(nameof(IsNotEditing)); } }
    public bool IsNotEditing => !_editing;
    public string EditText { get => _editText; set => Set(ref _editText, value); }

    public void BeginRename()
    {
        EditText = Title;
        IsEditing = true;
    }

    // Empty (or the default name) clears the nickname; long names are cut to MaxNameLength.
    public void CommitRename()
    {
        if (!IsEditing) return;
        string text = (EditText ?? "").Trim();
        if (text.Length > MaxNameLength) text = text[..MaxNameLength].TrimEnd();
        Nickname = text.Length == 0 || text == DefaultName ? null : text;
        IsEditing = false;
        _namesChanged();
    }

    public void CancelRename() => IsEditing = false;

    // True for the profile this app most recently switched to in this session (the keyboard can't tell us).
    public bool SwitchedHere { get => _switchedHere; set => Set(ref _switchedHere, value); }
}
public sealed class BackupItemViewModel
{
    public BackupItemViewModel(BackupInfo info) => Info = info;

    public BackupInfo Info { get; }
    public string Name => Info.Name;
    public string Kind => Info.HasAllSlots ? "All 5 profiles"
        : Info.Slots is { Count: 1 } one ? $"Config {one[0]} only (saved before a write)"
        : Info.Slots is { Count: > 1 } many ? "Configs " + string.Join(", ", many)
        : Info.HasSlot1 ? "Config 1 only (saved before a write)"
        : "Incomplete";
    public string Details => string.IsNullOrWhiteSpace(Info.Summary) ? "(no summary)" : Info.Summary.Trim();
}

public sealed class MainViewModel : ObservableObject, IPanelHost
{
    private static readonly Brush Green = Frozen("#22C55E"), Red = Frozen("#F87171"), Amber = Frozen("#F59E0B"), Muted = Frozen("#98A1AD");

    private readonly IKeyboardBackend _backend;
    private readonly Func<string, string, bool> _confirm;
    private readonly Func<string, string, string, bool> _confirmDetailed;
    private readonly UiSink _sink;
    private readonly DispatcherTimer? _timer;

    private bool _present, _ggRunning, _busy, _statusIsError, _progressIndeterminate;
    private string _ggNames = "";
    private string _status = "Ready.";
    private string _log = "";
    private double _progressValue, _progressMax = 1;
    private BackupItemViewModel? _selectedBackup;
    private int _editSlot = 1;

    public MainViewModel(IKeyboardBackend backend, Func<string, string, bool> confirm, Func<string, string, string, bool> confirmDetailed, bool autoRefresh)
    {
        _backend = backend;
        _confirm = confirm;
        _confirmDetailed = confirmDetailed;
        _sink = new UiSink(this);
        Macros = new MacrosViewModel(backend, this);
        ActuationTab = new ActuationViewModel(backend, this);
        SocdTab = new SocdViewModel(backend, this);
        ReadAutoStart();

        for (int slot = 1; slot <= ReadSession.Slots; slot++) Profiles.Add(new ProfileCardViewModel(slot, SaveProfileNames));
        foreach (var kv in backend.LoadProfileNames()) if (kv.Key >= 1 && kv.Key <= Profiles.Count) Profiles[kv.Key - 1].Nickname = kv.Value;

        RefreshCommand = new RelayCommand(_ => Refresh());
        SwitchProfileCommand = new AsyncCommand(p => SwitchAsync(Convert.ToInt32(p)), _ => CanUseHardware);
        BackupAllCommand = new AsyncCommand(_ => BackupAllAsync(), _ => CanUseHardware);
        RestoreCommand = new AsyncCommand(_ => RestoreAsync(), _ => CanUseHardware && RestoreSlotFor(SelectedBackup) is not null);
        OpenBackupsFolderCommand = new RelayCommand(_ => OpenBackupsFolder());

        Refresh();

        if (autoRefresh)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += (_, _) => { if (!IsBusy) RefreshState(); };
            _timer.Start();
        }
    }

    // ---- start with Windows ----------------------------------------------------------------------

    private bool _startWithWindows;
    private string _autoStartHint = "";

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (value == _startWithWindows) return;
            try
            {
                _backend.SetAutoStart(value);
                ReadAutoStart();
                SetResult(true, value ? "Apex Control will start with Windows, hidden in the tray." : "Apex Control will no longer start with Windows.");
            }
            catch (Exception ex)
            {
                SetResult(false, "Couldn't change the startup setting: " + ex.Message);
                ReadAutoStart();
            }
            Raise();
        }
    }

    public string AutoStartHint { get => _autoStartHint; private set => Set(ref _autoStartHint, value); }

    private void ReadAutoStart()
    {
        AutoStartState s = _backend.GetAutoStart();
        _startWithWindows = s.Enabled;
        AutoStartHint = s.DisabledInWindows ? "Windows has this switched off in Settings > Apps > Startup." : "";
        Raise(nameof(StartWithWindows));
    }

    // The profile (Config 1-5) the Macros, Actuation and SOCD tabs read and write. It follows the sidebar: switching to a profile
    // (or writing, restoring or reading one) makes it the one being edited; changing it makes every tab drop what it had loaded.
    public int EditSlot
    {
        get => _editSlot;
        private set
        {
            if (!Set(ref _editSlot, value)) return;
            Raise(nameof(EditingText));
            EditSlotChanged?.Invoke();
        }
    }

    public event Action? EditSlotChanged;

    public string EditingText => Profiles.Count >= EditSlot && Profiles[EditSlot - 1].Nickname is string nick
        ? $"Editing: {nick} (Config {EditSlot})"
        : $"Editing: Config {EditSlot}";

    public MacrosViewModel Macros { get; }
    public ActuationViewModel ActuationTab { get; }
    public SocdViewModel SocdTab { get; }

    // Created fresh each time the Compatibility window opens (it scans the USB devices when it is created).
    public CompatibilityViewModel? Compatibility { get; private set; }

    public void OpenCompatibility()
    {
        Compatibility = new CompatibilityViewModel(_backend, this);
        Raise(nameof(Compatibility));
    }

    public ObservableCollection<ProfileCardViewModel> Profiles { get; } = new();
    public ObservableCollection<BackupItemViewModel> Backups { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public AsyncCommand SwitchProfileCommand { get; }
    public AsyncCommand BackupAllCommand { get; }
    public AsyncCommand RestoreCommand { get; }
    public RelayCommand OpenBackupsFolderCommand { get; }

    // ---- state shown in the header ------------------------------------------------------------

    public bool KeyboardPresent { get => _present; private set { if (Set(ref _present, value)) RaiseHeader(); } }
    public bool GgRunning { get => _ggRunning; private set { if (Set(ref _ggRunning, value)) RaiseHeader(); } }

    public string KeyboardPillText => KeyboardPresent ? "Keyboard connected" : "Keyboard not found";
    public Brush KeyboardPillBrush => KeyboardPresent ? Green : Red;
    public string GgPillText => GgRunning ? "SteelSeries GG is running" : "GG is not running";
    public Brush GgPillBrush => GgRunning ? Amber : Green;
    public string GgBannerText => $"SteelSeries software is running ({_ggNames}). It holds the keyboard, so ApexControl can't talk to it. " +
                                  "Quit it from its tray icon (right-click, then Quit), and this will clear by itself.";

    public bool CanUseHardware => KeyboardPresent && !GgRunning && !IsBusy;

    // ---- busy / progress / messages -----------------------------------------------------------

    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) { Raise(nameof(CanUseHardware)); UpdateCommands(); } } }
    public string StatusMessage { get => _status; private set => Set(ref _status, value); }
    public bool StatusIsError { get => _statusIsError; private set { if (Set(ref _statusIsError, value)) Raise(nameof(StatusBrush)); } }
    public Brush StatusBrush => StatusIsError ? Red : Muted;
    public string LogText { get => _log; private set => Set(ref _log, value); }
    public double ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }
    public double ProgressMax { get => _progressMax; private set => Set(ref _progressMax, value); }
    public bool ProgressIndeterminate { get => _progressIndeterminate; private set => Set(ref _progressIndeterminate, value); }

    public BackupItemViewModel? SelectedBackup
    {
        get => _selectedBackup;
        set { if (Set(ref _selectedBackup, value)) UpdateCommands(); }
    }

    // ---- refreshing ---------------------------------------------------------------------------

    private void SaveProfileNames()
    {
        _backend.SaveProfileNames(Profiles.Where(p => p.Nickname is not null).ToDictionary(p => p.Slot, p => p.Nickname!));
        Raise(nameof(EditingText));
    }

    public void Refresh()
    {
        RefreshState();
        ReadAutoStart();

        var selected = SelectedBackup?.Name;
        Backups.Clear();
        foreach (BackupInfo b in _backend.ListBackups().Reverse()) Backups.Add(new BackupItemViewModel(b));
        SelectedBackup = Backups.FirstOrDefault(b => b.Name == selected) ?? Backups.FirstOrDefault();


    }

    private void RefreshState()
    {
        KeyboardState st = _backend.GetState();
        _ggNames = string.Join(", ", st.GgNames);
        Raise(nameof(GgBannerText));
        KeyboardPresent = st.Present;
        GgRunning = st.GgRunning;
        Raise(nameof(CanUseHardware));
        UpdateCommands();
    }

    private void RaiseHeader()
    {
        Raise(nameof(KeyboardPillText)); Raise(nameof(KeyboardPillBrush));
        Raise(nameof(GgPillText)); Raise(nameof(GgPillBrush));
        Raise(nameof(CanUseHardware));
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        SwitchProfileCommand.RaiseCanExecuteChanged();
        BackupAllCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        Macros?.HardwareStateChanged();
        ActuationTab?.HardwareStateChanged();
        SocdTab?.HardwareStateChanged();
        Compatibility?.HardwareStateChanged();
    }

    public bool ConfirmDetailed(string caption, string summary, string details) => _confirmDetailed(caption, summary, details);

    // ---- the latest known copy of each profile --------------------------------------------------------
    // What the Macros and SOCD tabs show: the profile as last read from the keyboard or last written (and read back) by this app,
    // kept per profile so switching profiles shows each one's own settings. A write always re-reads the keyboard and refuses if it
    // no longer matches the copy the edit was planned from, so a copy that has gone stale (GG changed it) can never be saved over.
    private readonly Dictionary<int, (byte[] R2, byte[] R3, DateTime At)> _slots = new();

    public event Action<int>? SlotDataChanged;

    public bool TryGetSlot(int slot, out byte[] region02, out byte[] region03, out DateTime at)
    {
        if (_slots.TryGetValue(slot, out var s)) { (region02, region03, at) = s; return true; }
        region02 = region03 = Array.Empty<byte>(); at = default;
        return false;
    }

    public void StoreSlot(int slot, byte[] region02, byte[] region03)
    {
        _slots[slot] = (region02, region03, DateTime.Now);
        SlotDataChanged?.Invoke(slot);
    }

    public void ForgetSlot(int slot)
    {
        _slots.Remove(slot);
        SlotDataChanged?.Invoke(slot);
    }

    // Reads a profile from the keyboard into the copy the tabs show. `announce`: also show the pop-up message when it finishes.
    public async Task<bool> ReadSlotAsync(int slot, bool announce = false)
    {
        SlotReadResult? read = null;
        bool ok = await RunAsync($"Reading Config {slot} (about 5 seconds)...", async sink =>
        {
            read = await _backend.ReadSlotAsync(slot, sink);
            return new OpResult(read.Ok, read.Ok ? $"Read Config {slot} from the keyboard. Nothing was changed." : read.Message);
        }, activeAfter: slot, announce: announce);
        if (!ok || read is null || read.Region02 is null || read.Region03 is null) return false;
        StoreSlot(slot, read.Region02, read.Region03);
        return true;
    }

    // ---- pop-up message ("saved", "sent", or what went wrong) -------------------------------------------
    private string _toastText = "";
    private bool _toastOk = true, _toastVisible;
    private DispatcherTimer? _toastTimer;

    public string ToastText { get => _toastText; private set => Set(ref _toastText, value); }
    public bool ToastOk { get => _toastOk; private set => Set(ref _toastOk, value); }
    public bool ToastVisible { get => _toastVisible; private set => Set(ref _toastVisible, value); }
    public RelayCommand DismissToastCommand => _dismissToast ??= new RelayCommand(_ => HideToast());
    private RelayCommand? _dismissToast;

    private void ShowToast(bool ok, string text)
    {
        ToastOk = ok;
        ToastText = (ok ? "\u2713  " : "\u2717  ") + text;
        ToastVisible = true;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ok ? 7 : 20) };
        _toastTimer.Tick += (_, _) => HideToast();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer?.Stop();
        ToastVisible = false;
    }

    // ---- operations ---------------------------------------------------------------------------

    private async Task SwitchAsync(int slot)
    {
        bool ok = await RunAsync($"Switching to {Profiles[slot - 1].Title}...", sink => _backend.SwitchProfileAsync(slot, sink), announce: true);
        if (!ok) return;
        foreach (ProfileCardViewModel c in Profiles) c.SwitchedHere = c.Slot == slot;
        EditSlot = slot;

        // Show that profile's own macros and SOCD straight away (read it once if this app has not seen it yet), and put its
        // remembered actuation back on the keyboard, so the tabs never show another profile's settings or a blank.
        if (!_slots.ContainsKey(slot)) await ReadSlotAsync(slot);
        await ActuationTab.ReapplyAsync(slot);
    }

    // Reading or writing a profile ends with GG's "89 <slot>", which leaves that Config active; the backup leaves the one being edited.
    private Task BackupAllAsync()
    {
        int slot = EditSlot;
        return RunAsync("Reading all 5 profiles (about 15 seconds)...", sink => _backend.BackupAllAsync(sink, slot), activeAfter: slot, announce: true);
    }

    // Which profile restoring this backup would write: a single-profile snapshot restores its own profile; an all-profiles
    // backup restores the profile being edited.
    private int? RestoreSlotFor(BackupItemViewModel? b)
    {
        if (b is null) return null;
        if (b.Info.Slots is { Count: 1 } one) return one[0];
        return b.Info.HasSlot(EditSlot) ? EditSlot : null;
    }

    private async Task RestoreAsync()
    {
        BackupItemViewModel? b = SelectedBackup;
        if (b is null) return;
        if (RestoreSlotFor(b) is not int slot) { SetResult(false, $"That backup has no Config {EditSlot} files to restore."); return; }
        if (!BackupStore.TryLoadSlot(b.Info.Path, slot, out byte[] r2, out byte[] r3)) { SetResult(false, $"That backup has no slot {slot} files."); return; }

        SlotSummary s = ProfileInfo.Summarize(slot, r2);
        if (!s.CrcOk) { SetResult(false, "That backup's checksum is not valid, so it will not be written."); return; }

        bool go = _confirm(
            $"Restore Config {slot} from this backup?\n\n" +
            $"Backup: {b.Name}\n{s}\n\n" +
            $"This overwrites Config {slot} on the keyboard with the saved copy, then reads it back to check it. " +
            "The other profiles are not touched. Restoring an older backup will undo any changes made to that profile since.",
            $"Restore Config {slot}");
        if (!go) { SetResult(true, "Cancelled. Nothing was written."); return; }

        if (await RunAsync($"Writing Config {slot} (about 10 seconds)...", sink => _backend.RestoreSlotAsync(b.Info.Path, slot, sink), activeAfter: slot, announce: true))
            StoreSlot(slot, r2, r3);        // what was written (and read back) is now what the tabs show
        else
            ForgetSlot(slot);
    }

    private void OpenBackupsFolder()
    {
        Directory.CreateDirectory(_backend.BackupsRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_backend.BackupsRoot}\"") { UseShellExecute = true });
    }

    public async Task<bool> RunAsync(string start, Func<IOperationSink, Task<OpResult>> work, int? activeAfter = null, bool announce = false)
    {
        IsBusy = true;
        StatusIsError = false;
        StatusMessage = start;
        ProgressValue = 0;
        ProgressIndeterminate = true;
        AppendLog("--- " + start);

        OpResult result = await work(_sink);

        ProgressIndeterminate = false;
        IsBusy = false;
        SetResult(result.Ok, result.Message);
        AppendLog(result.Message);
        if (announce || !result.Ok) ShowToast(result.Ok, result.Message);
        Refresh();
        if (result.Ok && activeAfter is int slot)
        {
            foreach (ProfileCardViewModel c in Profiles) c.SwitchedHere = c.Slot == slot;
            EditSlot = slot;
        }
        return result.Ok;
    }

    public void Notify(bool ok, string message)
    {
        SetResult(ok, message);
        ShowToast(ok, message);
    }

    public void SetResult(bool ok, string message)
    {
        StatusIsError = !ok;
        StatusMessage = message;
    }

    private void AppendLog(string line)
    {
        string combined = LogText.Length == 0 ? line : LogText + Environment.NewLine + line;
        var lines = combined.Split(Environment.NewLine);
        LogText = lines.Length > 300 ? string.Join(Environment.NewLine, lines.Skip(lines.Length - 300)) : combined;
    }

    private static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    // Core reports from a worker thread; hop to the UI thread.
    private sealed class UiSink : IOperationSink
    {
        private readonly MainViewModel _vm;
        private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;

        public UiSink(MainViewModel vm) => _vm = vm;

        public void Info(string message) => _ui.BeginInvoke(() => _vm.AppendLog(message.Trim()));

        public void Progress(int done, int total, string? label = null) => _ui.BeginInvoke(() =>
        {
            _vm.ProgressIndeterminate = false;
            _vm.ProgressMax = Math.Max(1, total);
            _vm.ProgressValue = done;
        });
    }
}
