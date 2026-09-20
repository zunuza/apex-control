using System.Collections.ObjectModel;
using System.Windows.Media;
using ApexControl.App.Backend;
using ApexControl.App.Infrastructure;
using ApexControl.Core;

namespace ApexControl.App.ViewModels;

// The Actuation tab: a draft of the whole actuation table (one global value plus per-key values), sent only when
// "Send to keyboard" is pressed. Only values GG itself was seen sending are offered; the keyboard can't report its
// actuation, so the tab remembers what this app last sent, separately for each profile (Config 1-5), and shows the one being
// edited. A table saved into the profile stays with it on the keyboard; ReapplyOnSwitch can also re-send a live-only one.
public sealed class ActuationViewModel : ObservableObject
{
    private const double KeyUnit = 40;

    private readonly IKeyboardBackend _backend;
    private readonly IPanelHost _host;
    private readonly IReadOnlyList<double> _values = Actuation.KnownGoodValues.Select(v => v.Mm).ToList();
    private readonly Dictionary<byte, int> _overrides = new();     // HID -> index into _values (only where different from global)

    private double _globalIndex, _selectionIndex;
    private string? _sentSignature;
    private string _lastSentText = "";
    private ActuationDraft? _last;
    private bool _reapplyOnSwitch;

    public ActuationViewModel(IKeyboardBackend backend, IPanelHost host)
    {
        _backend = backend;
        _host = host;

        (Rows, Keys) = KeyboardBuilder.Build(Actuation.IsAdjustable, "not part of the command GG uses for actuation, so it can't be changed here.");
        _globalIndex = _selectionIndex = IndexOf(2.0);

        _host.EditSlotChanged += () => LoadDraft(_host.EditSlot);

        ApplyCommand = new AsyncCommand(_ => SendAsync(), _ => _host.CanUseHardware);
        SaveToProfileCommand = new AsyncCommand(_ => SaveToProfileAsync(), _ => _host.CanUseHardware);
        ToggleKeyCommand = new RelayCommand(p => Toggle(p as KeyCapViewModel));
        SetSelectedCommand = new RelayCommand(_ => SetSelected(), _ => SelectedCount > 0);
        ClearSelectedCommand = new RelayCommand(_ => ClearSelected(), _ => SelectedCount > 0);
        ClearAllOverridesCommand = new RelayCommand(_ => { _overrides.Clear(); Refresh(); });
        SelectAllCommand = new RelayCommand(_ => { foreach (KeyCapViewModel k in Keys.Where(k => k.Adjustable)) k.Selected = true; Refresh(); });
        SelectNoneCommand = new RelayCommand(_ => { foreach (KeyCapViewModel k in Keys) k.Selected = false; Refresh(); });
        RevertCommand = new RelayCommand(_ => Revert(), _ => _last is not null);
        LoadDraft(_host.EditSlot);
    }

    // Put the profile's own remembered table on screen (or the default, if this app has never sent one for it).
    private void LoadDraft(int slot)
    {
        _last = _backend.LoadLastActuation(slot);
        _overrides.Clear();
        foreach (KeyCapViewModel k in Keys) k.Selected = false;
        _globalIndex = _selectionIndex = IndexOf(2.0);
        _sentSignature = null;
        if (_last is not null && TryIndexOf(_last.GlobalMm, out int g))
        {
            _globalIndex = _selectionIndex = g;
            foreach (var (key, mm) in _last.PerKeyMm)
                if (Actuation.IsAdjustable(key) && TryIndexOf(mm, out int i) && i != g) _overrides[key] = i;
            _sentSignature = Signature();
            LastSentText = $"Config {slot}: last sent by this app on {_last.SentAt:d MMM} at {_last.SentAt:HH:mm}. The keyboard can't tell us its own values, so this is only what we sent - GG's Save can overwrite it.";
        }
        else
        {
            _last = null;
            LastSentText = $"Config {slot}: nothing sent by this app for this profile yet. The keyboard can't tell us its current values, so the starting point below is just a default.";
        }
        Raise(nameof(GlobalIndex)); Raise(nameof(SaveToProfileText));
        Refresh();
        RevertCommand?.RaiseCanExecuteChanged();
    }

    // When on, switching to a profile also sends the table this app last sent live for it. Off by default: a table saved into the
    // profile (Save to Config N) stays with the profile on the keyboard by itself (confirmed on the real keyboard).
    public bool ReapplyOnSwitch { get => _reapplyOnSwitch; set => Set(ref _reapplyOnSwitch, value); }

    public async Task ReapplyAsync(int slot)
    {
        if (!ReapplyOnSwitch) return;
        ActuationDraft? d = _backend.LoadLastActuation(slot);
        if (d is null) return;
        await _host.RunAsync($"Putting Config {slot}'s actuation back on the keyboard...", sink => _backend.ApplyActuationAsync(d.GlobalMm, d.PerKeyMm, sink, slot));
    }

    public double MaxIndex => _values.Count - 1;
    public ObservableCollection<ObservableCollection<KeyCapViewModel>> Rows { get; } = new();
    public List<KeyCapViewModel> Keys { get; } = new();

    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand SaveToProfileCommand { get; }
    public string SaveToProfileText => $"Save to Config {_host.EditSlot}...";
    public RelayCommand ToggleKeyCommand { get; }
    public RelayCommand SetSelectedCommand { get; }
    public RelayCommand ClearSelectedCommand { get; }
    public RelayCommand ClearAllOverridesCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand RevertCommand { get; }

    // ---- sliders ---------------------------------------------------------------------------------

    public double GlobalIndex
    {
        get => _globalIndex;
        set
        {
            double v = Math.Clamp(Math.Round(value), 0, MaxIndex);
            if (!Set(ref _globalIndex, v)) return;
            // An override that now equals the global value is no override at all.
            foreach (byte k in _overrides.Where(o => o.Value == (int)v).Select(o => o.Key).ToList()) _overrides.Remove(k);
            Refresh();
        }
    }

    public double SelectionIndex
    {
        get => _selectionIndex;
        set { if (Set(ref _selectionIndex, Math.Clamp(Math.Round(value), 0, MaxIndex))) { Raise(nameof(SelectionText)); Raise(nameof(SetSelectedText)); } }
    }

    public string GlobalText => Mm(_globalIndex);
    public string SelectionText => Mm(_selectionIndex);
    public string SetSelectedText => SelectedCount == 0 ? "Set selected keys" : $"Set {SelectedCount} selected key{(SelectedCount == 1 ? "" : "s")} to {SelectionText}";

    // ---- what the draft says ---------------------------------------------------------------------

    public int SelectedCount => Keys.Count(k => k.Selected);
    public string LastSentText { get => _lastSentText; private set => Set(ref _lastSentText, value); }

    public string Summary
    {
        get
        {
            if (_overrides.Count == 0) return $"Will send: every key at {GlobalText}.";
            var groups = _overrides.GroupBy(o => o.Value).OrderBy(g => g.Key)
                .Select(g => $"{string.Join(", ", g.Select(o => KeyNames.Name(o.Key)).OrderBy(n => n))} at {Mm(g.Key)}");
            return $"Will send: {GlobalText} for every key, except {string.Join("; ", groups)}.";
        }
    }

    public bool HasUnsentChanges => _sentSignature != Signature();

    public string StateText => _sentSignature is null ? "Not sent yet - press Send to keyboard." : _sentSignature == Signature() ? "Matches what was last sent." : "Changes NOT sent yet - press Send to keyboard.";

    public bool CanSend => _host.CanUseHardware;

    // ---- actions ---------------------------------------------------------------------------------

    private void Toggle(KeyCapViewModel? key)
    {
        if (key is null) return;
        if (!key.Adjustable)
        {
            _host.Notify(false, $"{key.Label} can't be changed here: it isn't one of the keys GG's actuation command covers (only the letters, numbers, punctuation, Space, Enter, Tab, Caps, Backspace and the modifiers are).");
            return;
        }
        key.Selected = !key.Selected;
        // Picking a key that already has its own value is a good starting point for the selection slider.
        if (key.Selected && key.Hid is byte h) SelectionIndex = _overrides.TryGetValue(h, out int i) ? i : _globalIndex;
        Refresh();
    }

    private void SetSelected()
    {
        int idx = (int)_selectionIndex;
        var names = new List<string>();
        foreach (KeyCapViewModel k in Keys.Where(k => k.Selected && k.Hid is not null))
        {
            byte hid = k.Hid!.Value;
            names.Add(k.Label);
            if (idx == (int)_globalIndex) _overrides.Remove(hid);
            else _overrides[hid] = idx;
        }
        Refresh();
        if (names.Count > 0)
            _host.Notify(true, $"{string.Join(", ", names)} set to {Mm(idx)} in the draft. It is NOT on the keyboard yet: press \"Send to keyboard\" (bottom right).");
    }

    private void ClearSelected()
    {
        var names = Keys.Where(k => k.Selected && k.Hid is not null).Select(k => k.Label).ToList();
        foreach (KeyCapViewModel k in Keys.Where(k => k.Selected && k.Hid is not null)) _overrides.Remove(k.Hid!.Value);
        Refresh();
        if (names.Count > 0)
            _host.Notify(true, $"{string.Join(", ", names)} back to the all-keys value ({GlobalText}) in the draft. Press \"Send to keyboard\" to put that on the keyboard.");
    }

    private void Revert()
    {
        ActuationDraft? last = _backend.LoadLastActuation(_host.EditSlot);
        if (last is null || !TryIndexOf(last.GlobalMm, out int g)) return;
        _overrides.Clear();
        foreach (var (key, mm) in last.PerKeyMm)
            if (Actuation.IsAdjustable(key) && TryIndexOf(mm, out int i) && i != g) _overrides[key] = i;
        _globalIndex = g;
        Raise(nameof(GlobalIndex));
        Refresh();
    }

    private async Task SendAsync()
    {
        double global = _values[(int)_globalIndex];
        var perKey = _overrides.ToDictionary(o => o.Key, o => _values[o.Value]);
        string signature = Signature();

        int slot = _host.EditSlot;
        bool ok = await _host.RunAsync($"Sending the actuation table for Config {slot}...", sink => _backend.ApplyActuationAsync(global, perKey, sink, slot), announce: true);
        if (!ok) return;

        _last = _backend.LoadLastActuation(slot);
        RevertCommand?.RaiseCanExecuteChanged();
        _sentSignature = signature;
        LastSentText = $"Config {slot}: last sent by this app on {DateTime.Now:d MMM} at {DateTime.Now:HH:mm}. The keyboard can't tell us its own values, so this is only what we sent - GG's Save can overwrite it.";
        Refresh();
    }

    // Also writes the table into the profile on the keyboard, the way GG's Save does (live table first, then the profile), so the
    // profile itself holds it. Shown and confirmed like a macro or SOCD save.
    private async Task SaveToProfileAsync()
    {
        int slot = _host.EditSlot;
        double global = _values[(int)_globalIndex];
        var perKey = _overrides.ToDictionary(o => o.Key, o => _values[o.Value]);
        string signature = Signature();

        if (!_host.TryGetSlot(slot, out byte[] r2, out byte[] r3, out _))
        {
            if (!await _host.ReadSlotAsync(slot) || !_host.TryGetSlot(slot, out r2, out r3, out _))
            {
                _host.Notify(false, $"Couldn't read Config {slot} from the keyboard, so nothing was saved.");
                return;
            }
        }

        MacroPlan plan;
        try { plan = ActuationPlanner.Plan(r2, r3, global, perKey, slot); }
        catch (Exception ex) { _host.SetResult(false, "Could not work out that change: " + ex.Message); return; }
        if (!plan.Ok) { _host.Notify(false, plan.Error ?? "That change was refused."); return; }

        bool go = _host.ConfirmDetailed("Save actuation",
            plan.Headline + PlanFormatter.WriteNotice(slot) + "\n\nGG's live actuation table is sent first, as GG does when it saves.",
            PlanFormatter.Describe(plan, "Actuation stored in this profile now:", "Actuation after this change:"));
        if (!go) { _host.SetResult(true, "Cancelled. Nothing was written."); return; }

        bool ok = await _host.RunAsync($"Saving actuation into Config {slot} (about 15 seconds)...", sink => _backend.ApplyProfilePlanAsync(plan, sink), activeAfter: slot, announce: true);
        if (!ok) { _host.ForgetSlot(slot); return; }

        _host.StoreSlot(slot, plan.After.Region02.AsSpan(0, ProfileWriter.ReadLen).ToArray(), plan.After.Region03.ToArray());
        _backend.RememberActuation(slot, new ActuationDraft(global, perKey, DateTime.Now));
        _last = _backend.LoadLastActuation(slot);
        RevertCommand?.RaiseCanExecuteChanged();
        _sentSignature = signature;
        LastSentText = $"Config {slot}: saved into the profile on the keyboard on {DateTime.Now:d MMM} at {DateTime.Now:HH:mm} (and sent live).";
        Refresh();
    }

    // Called by the main window whenever the keyboard/GG/busy state changes.
    public void HardwareStateChanged()
    {
        Raise(nameof(CanSend));
        ApplyCommand?.RaiseCanExecuteChanged();
        SaveToProfileCommand?.RaiseCanExecuteChanged();
    }

    // ---- plumbing --------------------------------------------------------------------------------

    private string Signature() => $"{(int)_globalIndex}|" + string.Join(",", _overrides.OrderBy(o => o.Key).Select(o => $"{o.Key:X2}={o.Value}"));

    private string Mm(double index) => $"{_values[(int)index]:0.0} mm";

    private int IndexOf(double mm) => TryIndexOf(mm, out int i) ? i : throw new ArgumentException($"{mm} mm is not a known-good value.");

    private bool TryIndexOf(double mm, out int index)
    {
        for (int i = 0; i < _values.Count; i++)
            if (Math.Abs(_values[i] - mm) < 0.0005) { index = i; return true; }
        index = -1;
        return false;
    }

    private void Refresh()
    {
        foreach (KeyCapViewModel k in Keys)
        {
            if (k.Hid is not byte h) continue;
            bool over = _overrides.TryGetValue(h, out int i);
            k.Show(_values[over ? i : (int)_globalIndex].ToString("0.0"), over);
        }
        Raise(nameof(GlobalText)); Raise(nameof(SelectionText)); Raise(nameof(SetSelectedText));
        Raise(nameof(SelectedCount)); Raise(nameof(Summary)); Raise(nameof(StateText)); Raise(nameof(HasUnsentChanges));
        SetSelectedCommand?.RaiseCanExecuteChanged();
        ClearSelectedCommand?.RaiseCanExecuteChanged();
    }
}
