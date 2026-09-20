using System.Collections.ObjectModel;
using System.Text;
using ApexControl.App.Backend;
using ApexControl.App.Infrastructure;
using ApexControl.Core;

namespace ApexControl.App.ViewModels;

// What the tabs need from the main window: run a hardware operation with progress, report, and ask.
public interface IPanelHost
{
    bool CanUseHardware { get; }
    // `announce`: show the result as a pop-up too (failures always are).
    Task<bool> RunAsync(string start, Func<IOperationSink, Task<OpResult>> work, int? activeAfter = null, bool announce = false);
    void SetResult(bool ok, string message);

    // Shows a short message as a pop-up and in the status line (for feedback on edits that do not touch the keyboard).
    void Notify(bool ok, string message);

    // The latest known copy of each profile (read from the keyboard, or written and read back by this app), shared by the tabs.
    bool TryGetSlot(int slot, out byte[] region02, out byte[] region03, out DateTime at);
    void StoreSlot(int slot, byte[] region02, byte[] region03);
    void ForgetSlot(int slot);
    event Action<int>? SlotDataChanged;
    Task<bool> ReadSlotAsync(int slot, bool announce = false);

    // The profile (Config 1-5) the tabs read and write: the one chosen in the sidebar. Changing it makes every tab drop its copy.
    int EditSlot { get; }
    event Action? EditSlotChanged;
    bool ConfirmDetailed(string caption, string summary, string details);
}

public sealed class MacroRowViewModel
{
    public MacroRowViewModel(MacroRecord rec)
    {
        byte? key = KeymapLayout.KeyAt(rec.EntryOffset);
        Key = key ?? 0;
        KeyName = key is byte k ? KeyNames.Name(k) : "?";
        Description = MacroEditor.DescribeRecord(rec.Bytes);
        IsEditable = MacroEditor.TryParseEvents(rec.Bytes, out List<MacroEvent> events);
        Events = events;

        if (!IsEditable) Kind = "Custom sequence made in GG";
        else if (MacroEditor.TryParseSequence(rec.Bytes, out _))
        {
            string text = MacroEditor.DescribeEvents(events);
            Kind = char.ToUpperInvariant(text[0]) + text[1..];
        }
        else Kind = $"Timeline of {events.Count} events";
    }

    public byte Key { get; }
    public string KeyName { get; }
    public string Description { get; }
    public string Kind { get; }

    // True if this is a macro we can show as a timeline (and so load into the editor).
    public bool IsEditable { get; }
    public List<MacroEvent> Events { get; }
}

// One block of the timeline in the editor: a key going down, a key coming up, or a wait.
public sealed class EventViewModel : ObservableObject
{
    public const string DownText = "Key down", UpText = "Key up", WaitText = "Wait";

    private readonly Action _changed;
    private string _kindName, _key, _msText;
    private int _number = 1;

    public EventViewModel(string kindName, string key, string msText, Action changed)
    {
        _kindName = kindName;
        _key = key;
        _msText = msText;
        _changed = changed;
    }

    public static EventViewModel From(MacroEvent e, Action changed) => e.Kind switch
    {
        MacroEventKind.Down => new EventViewModel(DownText, KeyNames.Name(e.Key), "100", changed),
        MacroEventKind.Up => new EventViewModel(UpText, KeyNames.Name(e.Key), "100", changed),
        _ => new EventViewModel(WaitText, "Q", e.Ms.ToString(), changed),
    };

    public string KindName
    {
        get => _kindName;
        set
        {
            if (!Set(ref _kindName, value)) return;
            Raise(nameof(IsKey)); Raise(nameof(IsWait));
            _changed();
        }
    }

    public string Key { get => _key; set { if (Set(ref _key, value)) _changed(); } }
    public string MsText { get => _msText; set { if (Set(ref _msText, value)) _changed(); } }

    public bool IsWait => KindName == WaitText;
    public bool IsKey => !IsWait;

    public int Number { get => _number; set => Set(ref _number, value); }
}

public sealed class MacrosViewModel : ObservableObject
{
    private readonly IKeyboardBackend _backend;
    private readonly IPanelHost _host;

    private byte[]? _r2, _r3;
    private int _loadedSlot = 1;
    private string _loadedText = "Not loaded yet. Click \"Load from keyboard\" to read the profile you are editing.";
    private MacroRowViewModel? _selectedRow;
    private string _editorKey = "";

    public MacrosViewModel(IKeyboardBackend backend, IPanelHost host)
    {
        _backend = backend;
        _host = host;
        _host.SlotDataChanged += slot => { if (slot == _host.EditSlot) ShowCached(); };
        _host.EditSlotChanged += ShowCached;

        // Keys that can carry a macro: the ones in the keymap table that we have a name for.
        BindableKeys = KeyNames.OrderedNames.Where(n => KeyNames.TryParse(n, out byte h) && KeymapLayout.DefaultOffsets.ContainsKey(h)).ToList();
        KeyChoices = KeyNames.OrderedNames;
        EventKindChoices = new[] { EventViewModel.DownText, EventViewModel.UpText, EventViewModel.WaitText };

        // The keyboard you click: every key that has a name we can bind a macro to.
        var named = new HashSet<byte>(KeyNames.OrderedNames.Select(n => KeyNames.TryParse(n, out byte h) ? h : (byte)0));
        (KeyboardRows, KeyCaps) = KeyboardBuilder.Build(hid => named.Contains(hid) && KeymapLayout.DefaultOffsets.ContainsKey(hid), "can't carry a macro.");
        SelectKeyCommand = new RelayCommand(p => SelectKey(p as KeyCapViewModel));
        OpenEditorCommand = new RelayCommand(_ => OpenEditorRequested?.Invoke(), _ => CanOpenEditor);
        StartRecordingCommand = new RelayCommand(_ => StartRecording(), _ => !IsRecording);
        StopRecordingCommand = new RelayCommand(_ => StopRecording(), _ => IsRecording);
        SetAllWaitsCommand = new RelayCommand(_ => SetAllWaits(), _ => !IsRecording && Events.Any(e => e.IsWait));

        LoadCommand = new AsyncCommand(_ => LoadAsync(), _ => _host.CanUseHardware);
        ApplyCommand = new AsyncCommand(_ => ApplyAsync(false), _ => CanApply);
        RemoveCommand = new AsyncCommand(_ => ApplyAsync(true), _ => CanRemove);

        AddDownCommand = new RelayCommand(_ => Append(new EventViewModel(EventViewModel.DownText, "Q", "100", EditorChanged)), _ => CanAddEvents(1));
        AddUpCommand = new RelayCommand(_ => Append(new EventViewModel(EventViewModel.UpText, HeldKeyAtEnd() ?? "Q", "100", EditorChanged)), _ => CanAddEvents(1));
        AddWaitCommand = new RelayCommand(_ => Append(new EventViewModel(EventViewModel.WaitText, "Q", "100", EditorChanged)), _ => CanAddEvents(1));
        AddTapCommand = new RelayCommand(_ =>
        {
            Events.Add(new EventViewModel(EventViewModel.DownText, "Q", "100", EditorChanged));
            Events.Add(new EventViewModel(EventViewModel.WaitText, "Q", "100", EditorChanged));
            Events.Add(new EventViewModel(EventViewModel.UpText, "Q", "100", EditorChanged));
            Renumber();
        }, _ => CanAddEvents(3));
        RemoveEventCommand = new RelayCommand(p => { if (p is EventViewModel e) { Events.Remove(e); Renumber(); } });
        MoveUpCommand = new RelayCommand(p => Move(p as EventViewModel, -1));
        MoveDownCommand = new RelayCommand(p => Move(p as EventViewModel, +1));
        ClearEventsCommand = new RelayCommand(_ => { Events.Clear(); Renumber(); });

        LoadDefaultTimeline();
    }

    public ObservableCollection<ObservableCollection<KeyCapViewModel>> KeyboardRows { get; }
    public List<KeyCapViewModel> KeyCaps { get; }
    public RelayCommand SelectKeyCommand { get; }
    public RelayCommand OpenEditorCommand { get; }
    public RelayCommand StartRecordingCommand { get; }
    public RelayCommand StopRecordingCommand { get; }
    public RelayCommand SetAllWaitsCommand { get; }

    // Raised when the "Edit / Create macro" button is pressed (the main window opens the editor pop-up), and after a
    // write attempt finishes (true = written and verified) so the pop-up can close itself.
    public event Action? OpenEditorRequested;
    public event Action<bool>? WriteFinished;

    public IReadOnlyList<string> BindableKeys { get; }
    public IReadOnlyList<string> KeyChoices { get; }
    public IReadOnlyList<string> EventKindChoices { get; }
    public ObservableCollection<MacroRowViewModel> Rows { get; } = new();
    public ObservableCollection<EventViewModel> Events { get; } = new();

    public AsyncCommand LoadCommand { get; }
    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public RelayCommand AddDownCommand { get; }
    public RelayCommand AddUpCommand { get; }
    public RelayCommand AddWaitCommand { get; }
    public RelayCommand AddTapCommand { get; }
    public RelayCommand RemoveEventCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand ClearEventsCommand { get; }

    public bool HasLoaded => _r2 is not null;
    public string LoadedText { get => _loadedText; private set => Set(ref _loadedText, value); }
    public string EmptyText => !HasLoaded ? "" : Rows.Count == 0 ? $"No macros on Config {_loadedSlot}." : "";

    public MacroRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!Set(ref _selectedRow, value)) return;
            if (value is not null)
            {
                _editorKey = value.KeyName;
                Raise(nameof(EditorKey));
                if (value.IsEditable)
                {
                    Events.Clear();
                    foreach (MacroEvent e in value.Events) Events.Add(EventViewModel.From(e, EditorChanged));
                    Renumber();
                    return;
                }
            }
            EditorChanged();
        }
    }

    public string EditorKey { get => _editorKey; set { if (Set(ref _editorKey, value)) EditorChanged(); } }

    // ---- the timeline editor --------------------------------------------------------------------

    private void LoadDefaultTimeline()
    {
        Events.Clear();
        Events.Add(new EventViewModel(EventViewModel.DownText, "Q", "100", EditorChanged));
        Events.Add(new EventViewModel(EventViewModel.WaitText, "Q", "100", EditorChanged));
        Events.Add(new EventViewModel(EventViewModel.UpText, "Q", "100", EditorChanged));
        Renumber();
    }

    private bool CanAddEvents(int n) => Events.Count + n <= MacroEditor.MaxEvents;

    private void Append(EventViewModel e)
    {
        Events.Add(e);
        Renumber();
    }

    private void Move(EventViewModel? e, int delta)
    {
        if (e is null) return;
        int i = Events.IndexOf(e), j = i + delta;
        if (i < 0 || j < 0 || j >= Events.Count) return;
        Events.Move(i, j);
        Renumber();
    }

    // The first key that the timeline so far presses and has not released (a sensible default for "Key up").
    private string? HeldKeyAtEnd()
    {
        var held = new List<string>();
        foreach (EventViewModel e in Events)
        {
            if (e.KindName == EventViewModel.DownText) { if (!held.Contains(e.Key)) held.Add(e.Key); }
            else if (e.KindName == EventViewModel.UpText) held.Remove(e.Key);
        }
        return held.FirstOrDefault();
    }

    private void Renumber()
    {
        for (int i = 0; i < Events.Count; i++) Events[i].Number = i + 1;
        AddDownCommand?.RaiseCanExecuteChanged();
        AddUpCommand?.RaiseCanExecuteChanged();
        AddWaitCommand?.RaiseCanExecuteChanged();
        AddTapCommand?.RaiseCanExecuteChanged();
        EditorChanged();
    }

    // ---- what the editor currently says ---------------------------------------------------------

    private bool KeyIsBound => _r2 is not null && KeyNames.TryParse(EditorKey ?? "", out byte k) && Rows.Any(r => r.Key == k);

    public string ApplyText => !KeyNames.TryParse(EditorKey ?? "", out _) ? "Add macro..." : KeyIsBound ? $"Replace macro on {EditorKey}..." : $"Add macro to {EditorKey}...";

    // Null when the editor holds a usable request; otherwise what is wrong, in plain words.
    public string? ValidationMessage => Validate(out _);

    // A plain-words reading of the timeline as it stands (empty while it is not valid).
    public string Summary => Validate(out MacroEditRequest? r) is null && r?.Events is { } ev ? "Reads as: " + MacroEditor.DescribeEvents(ev) : "";

    public bool CanApply => _host.CanUseHardware && HasLoaded && Validate(out _) is null;
    public bool CanRemove => _host.CanUseHardware && HasLoaded && SelectedRow is not null;

    private string? Validate(out MacroEditRequest? req)
    {
        req = null;
        if (!KeyNames.TryParse(EditorKey ?? "", out byte key)) return "Click a key on the keyboard above.";
        if (!KeymapLayout.DefaultOffsets.ContainsKey(key)) return $"{EditorKey} can't carry a macro.";

        var events = new List<MacroEvent>();
        foreach (EventViewModel e in Events)
        {
            if (e.IsWait)
            {
                if (!int.TryParse(e.MsText?.Trim(), out int ms) || ms is < 1 or > MacroEditor.MaxWaitMs)
                    return $"Event {e.Number}: a wait must be a whole number from 1 to {MacroEditor.MaxWaitMs} ms.";
                events.Add(MacroEvent.Wait((ushort)ms));
            }
            else
            {
                if (!KeyNames.TryParse(e.Key ?? "", out byte k)) return $"Event {e.Number}: pick a key.";
                events.Add(e.KindName == EventViewModel.DownText ? MacroEvent.Down(k) : MacroEvent.Up(k));
            }
        }

        string? bad = MacroEditor.ValidateEvents(events);
        if (bad is not null) return bad;
        req = MacroEditRequest.ForEvents(key, events);
        return null;
    }

    private void EditorChanged()
    {
        Raise(nameof(ValidationMessage));
        Raise(nameof(Summary));
        Raise(nameof(ApplyText));
        Raise(nameof(EditorTitle));
        Raise(nameof(PanelText)); Raise(nameof(EditButtonText)); Raise(nameof(CanOpenEditor));
        RefreshKeys();
        HardwareStateChanged();
    }

    // What the header above the editor says: which key is picked and what it does now.
    public string EditorTitle
    {
        get
        {
            if (!HasLoaded) return "Click Load from keyboard, then click a key to see or edit its macro.";
            if (!KeyNames.TryParse(EditorKey ?? "", out byte hid)) return "Click a key above to add or edit its macro.";
            MacroRowViewModel? row = Rows.FirstOrDefault(r => r.Key == hid);
            return row is null ? $"{EditorKey}  -  no macro yet" : $"{EditorKey}  -  {row.Kind}";
        }
    }

    // Clicking a key: show its macro if it has one, otherwise start a fresh one for it.
    private void SelectKey(KeyCapViewModel? cap)
    {
        if (cap is null || !cap.Enabled || cap.Hid is not byte hid) return;

        // Clicking the key that is already picked puts it down again (like the Actuation keyboard).
        if (KeyNames.TryParse(EditorKey ?? "", out byte current) && current == hid) { Deselect(); return; }

        MacroRowViewModel? row = Rows.FirstOrDefault(r => r.Key == hid);
        if (row is not null) { SelectedRow = row; return; }

        string name = KeyNames.Name(hid);
        bool differentKey = EditorKey != name;
        SelectedRow = null;
        EditorKey = name;
        if (differentKey) LoadDefaultTimeline();
    }

    // ---- the small panel on the tab -------------------------------------------------------------

    public bool CanOpenEditor => HasLoaded && KeyNames.TryParse(EditorKey ?? "", out _);

    public string EditButtonText => KeyNames.TryParse(EditorKey ?? "", out byte hid) && Rows.Any(r => r.Key == hid) ? "Edit macro..." : "Create macro...";

    // What the key does now, in plain words (empty until a key is picked).
    public string PanelText
    {
        get
        {
            if (!HasLoaded || !KeyNames.TryParse(EditorKey ?? "", out byte hid)) return "";
            MacroRowViewModel? row = Rows.FirstOrDefault(r => r.Key == hid);
            return row is null ? "This key has no macro yet. Create one to record it live or build it block by block." : row.Description;
        }
    }

    // ---- recording ---------------------------------------------------------------------------------
    //
    // While recording, the editor pop-up passes every key press and release here (only from its own window, never
    // system-wide). Each becomes a Key down / Key up block, with a Wait block for the time since the previous one.

    private bool _recording;
    private readonly List<byte> _held = new();      // keys pressed and not yet released, in the order pressed
    private TimeSpan? _lastAt;
    private string _recordingNote = "";
    private string _bulkWaitText = "100";

    public bool IsRecording { get => _recording; private set { if (Set(ref _recording, value)) { Raise(nameof(IsNotRecording)); EditorChanged(); } } }
    public bool IsNotRecording => !_recording;
    public string RecordingNote { get => _recordingNote; private set => Set(ref _recordingNote, value); }
    public string BulkWaitText { get => _bulkWaitText; set => Set(ref _bulkWaitText, value); }

    public void StartRecording()
    {
        Events.Clear();
        _held.Clear();
        _lastAt = null;
        Renumber();
        RecordingNote = "Recording - press the keys you want, in this window. Click Stop when you are done.";
        IsRecording = true;
    }

    // Returns true if the key was taken (a repeat of a held key, or a release we did not see the press of, is ignored).
    public bool RecordKey(byte hid, bool down, TimeSpan at)
    {
        if (!IsRecording) return false;
        if (down)
        {
            if (_held.Contains(hid)) return true;   // auto-repeat while held
            // keep room for the wait + this press and for releasing every key still held
            if (Events.Count + 2 + _held.Count + 1 > MacroEditor.MaxEvents) { StopRecording("Stopped: a macro can hold at most " + MacroEditor.MaxEvents + " blocks."); return true; }
            AppendWait(at);
            _held.Add(hid);
            Events.Add(new EventViewModel(EventViewModel.DownText, KeyNames.Name(hid), "100", EditorChanged));
        }
        else
        {
            if (!_held.Remove(hid)) return true;
            AppendWait(at);
            Events.Add(new EventViewModel(EventViewModel.UpText, KeyNames.Name(hid), "100", EditorChanged));
        }
        _lastAt = at;
        Renumber();
        RecordingNote = $"Recording - {Events.Count} block{(Events.Count == 1 ? "" : "s")} so far. Click Stop when you are done.";
        return true;
    }

    public void NoteUnsupportedKey(string name) =>
        RecordingNote = $"{name} can't be used in a macro here (numpad and media keys aren't supported), so it was skipped.";

    public void StopRecording(string? reason = null)
    {
        if (!IsRecording) return;
        foreach (byte hid in _held.ToList())
            Events.Add(new EventViewModel(EventViewModel.UpText, KeyNames.Name(hid), "100", EditorChanged));   // never leave a key held
        _held.Clear();
        _lastAt = null;
        IsRecording = false;
        if (Events.Count == 0) { LoadDefaultTimeline(); RecordingNote = reason ?? "Nothing was recorded."; }
        else
        {
            Renumber();
            RecordingNote = reason ?? $"Recorded {Events.Count} blocks. Edit any timing below, then save.";
        }
    }

    private void AppendWait(TimeSpan at)
    {
        if (_lastAt is not TimeSpan prev) return;
        int ms = (int)Math.Round((at - prev).TotalMilliseconds);
        ms = Math.Clamp(ms, 0, MacroEditor.MaxWaitMs);
        if (ms >= 1) Events.Add(new EventViewModel(EventViewModel.WaitText, "Q", ms.ToString(), EditorChanged));
    }

    private void SetAllWaits()
    {
        if (!int.TryParse(BulkWaitText?.Trim(), out int ms) || ms is < 1 or > MacroEditor.MaxWaitMs)
        {
            RecordingNote = $"Type a whole number from 1 to {MacroEditor.MaxWaitMs} for the waits.";
            return;
        }
        foreach (EventViewModel e in Events.Where(e => e.IsWait)) e.MsText = ms.ToString();
        RecordingNote = $"Every wait is now {ms} ms.";
    }

    // No key picked: the editor goes back to its "click a key" state with a fresh tap.
    private void Deselect()
    {
        SelectedRow = null;
        EditorKey = "";
        LoadDefaultTimeline();
    }

    private void RefreshKeys()
    {
        KeyNames.TryParse(EditorKey ?? "", out byte current);
        bool haveKey = KeyNames.TryParse(EditorKey ?? "", out _);
        foreach (KeyCapViewModel k in KeyCaps)
        {
            if (k.Hid is not byte h) continue;
            bool has = HasLoaded && Rows.Any(r => r.Key == h);
            k.Show(has ? "macro" : "", has);
            k.Selected = haveKey && h == current;
        }
    }

    // Called by the main window whenever the keyboard/GG/busy state changes.
    public void HardwareStateChanged()
    {
        Raise(nameof(CanApply));
        Raise(nameof(CanRemove));
        LoadCommand?.RaiseCanExecuteChanged();
        ApplyCommand?.RaiseCanExecuteChanged();
        RemoveCommand?.RaiseCanExecuteChanged();
        OpenEditorCommand?.RaiseCanExecuteChanged();
        StartRecordingCommand?.RaiseCanExecuteChanged();
        StopRecordingCommand?.RaiseCanExecuteChanged();
        SetAllWaitsCommand?.RaiseCanExecuteChanged();
    }

    // ---- operations -----------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        int slot = _host.EditSlot;
        if (!await _host.ReadSlotAsync(slot, announce: true)) { Invalidate($"Could not read Config {slot}. Try \"Load from keyboard\" again."); return; }
        LoadedText = "Read from the keyboard at " + DateTime.Now.ToString("HH:mm:ss") + ".";
    }

    // Show the profile being edited from the shared copy (or say it has not been read yet).
    private void ShowCached()
    {
        int slot = _host.EditSlot;
        if (_host.TryGetSlot(slot, out byte[] r2, out byte[] r3, out DateTime at))
        {
            _loadedSlot = slot;
            SetSnapshot(r2, r3, $"Config {slot}, as last read or written at {at:HH:mm:ss}. Load from keyboard to read it again.");
        }
        else Invalidate($"Config {slot} has not been read yet. Click Load from keyboard.");
    }
    private void SetSnapshot(byte[] r2, byte[] r3, string note)
    {
        try
        {
            ProfileImage img = ProfileWriter.ImageFromDump(r2, r3);
            if (img.StoredCrc != img.ComputedCrc)
            {
                Invalidate($"Config {_loadedSlot}'s checksum is not valid, so this app won't edit it. Use Restore... (bottom left) to put a good backup back.");
                return;
            }
            var rows = MacroEditor.ReadMacros(img, out _).Select(m => new MacroRowViewModel(m)).OrderBy(r => r.Key).ToList();
            _r2 = r2; _r3 = r3;
            Rows.Clear();
            foreach (MacroRowViewModel r in rows) Rows.Add(r);
            LoadedText = note;
        }
        catch (InvalidOperationException ex)
        {
            Invalidate($"Config {_loadedSlot}'s macro area isn't in a shape this app understands, so it won't edit it: " + ex.Message);
            return;
        }
        SelectedRow = null;
        Raise(nameof(HasLoaded)); Raise(nameof(EmptyText));
        EditorChanged();
    }

    // Forget what we loaded (after any failure, or when what we hold may be out of date).
    private void Invalidate(string note)
    {
        _r2 = _r3 = null;
        Rows.Clear();
        SelectedRow = null;
        LoadedText = note;
        Raise(nameof(HasLoaded)); Raise(nameof(EmptyText));
        EditorChanged();
    }

    private async Task ApplyAsync(bool remove)
    {
        if (_r2 is null || _r3 is null) return;
        if (_loadedSlot != _host.EditSlot) { Invalidate($"Now editing Config {_host.EditSlot}. Click Load from keyboard to read it."); return; }
        int slot = _loadedSlot;

        MacroEditRequest req;
        if (remove)
        {
            if (SelectedRow is null) return;
            req = MacroEditRequest.Removal(SelectedRow.Key);
        }
        else
        {
            if (Validate(out MacroEditRequest? r) is not null || r is null) return;
            req = r;
        }

        MacroPlan plan;
        try { plan = MacroPlanner.Plan(_r2, _r3, req, slot); }
        catch (Exception ex) { _host.SetResult(false, "Could not work out that change: " + ex.Message); return; }
        if (!plan.Ok) { _host.SetResult(false, plan.Error ?? "That change was refused."); return; }

        bool go = _host.ConfirmDetailed(
            remove ? "Remove macro" : "Write macro",
            plan.Headline + PlanFormatter.WriteNotice(slot),
            PlanFormatter.Describe(plan));
        if (!go) { _host.SetResult(true, "Cancelled. Nothing was written."); return; }

        bool ok = await _host.RunAsync($"Writing Config {slot} (about 15 seconds)...", sink => _backend.ApplyProfilePlanAsync(plan, sink), activeAfter: slot, announce: true);
        if (ok)
        {
            // The write was read back and matched plan.After, so that IS what the keyboard holds now; every tab shows it.
            _host.StoreSlot(slot, plan.After.Region02.AsSpan(0, ProfileWriter.ReadLen).ToArray(), plan.After.Region03.ToArray());
            LoadedText = $"Saved to Config {slot} and verified by reading it back, at " + DateTime.Now.ToString("HH:mm:ss") + ".";
        }
        else
        {
            _host.ForgetSlot(slot);
            Invalidate($"The last write did not finish cleanly, so the list was cleared. Load from keyboard to see what Config {slot} holds now.");
        }
        WriteFinished?.Invoke(ok);
    }
}

// The confirm dialog's text: friendly lines first, then the exact bytes.
public static class PlanFormatter
{
    public static string WriteNotice(int slot) =>
        $"\n\nThis overwrites Config {slot} on the keyboard. The current Config {slot} is saved to your backups first, the write is read back to check it, and the other profiles are not touched.";

    public static string Describe(MacroPlan p, string? beforeTitle = null, string afterTitle = "Macros after this change:")
    {
        var sb = new StringBuilder();
        sb.AppendLine(beforeTitle ?? $"Macros on Config {p.Slot} now:");
        foreach (string l in p.MacrosBefore.DefaultIfEmpty("(none)")) sb.AppendLine("  " + l);
        sb.AppendLine();
        sb.AppendLine(afterTitle);
        foreach (string l in p.MacrosAfter.DefaultIfEmpty("(none)")) sb.AppendLine("  " + l);
        sb.AppendLine();
        sb.AppendLine("Exactly what changes in the profile data (before -> after):");
        if (p.Diff is { } d)
        {
            foreach (DiffEntry e in d.Entries)
            {
                string more = e.Truncated ? " ..." : "";
                if (e.Area == "keymap entry")
                {
                    sb.AppendLine($"  key entry for {e.KeyName} (offset {e.Offset}): {Spaced(e.BeforeHex)}  ->  {Spaced(e.AfterHex)}");
                    continue;
                }
                sb.AppendLine($"  {e.Area} (offset {e.Offset}, {e.Length} bytes)");
                sb.AppendLine($"      before: {Spaced(e.BeforeHex)}{more}");
                sb.AppendLine($"      after:  {Spaced(e.AfterHex)}{more}");
            }
            sb.AppendLine($"  {d.TotalChanged} bytes change in total. Nothing else in the profile changes, and the checksum is recalculated.");
            if (d.TailNote is not null) sb.AppendLine(d.TailNote.Trim());
        }
        sb.AppendLine();
        if (p.PreWriteCommand is { } live) sb.AppendLine($"Just before saving, the live command {live[0]:x2} {live[1]:x2} (the SOCD on/off switch) is sent, as GG does.");
        sb.Append($"The write is {p.WriteSteps} steps, the same ones GG uses when it saves a profile.");
        return sb.ToString();
    }

    private static string Spaced(string hex) => string.Join(" ", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
}
