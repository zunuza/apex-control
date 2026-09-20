using System.Collections.ObjectModel;
using ApexControl.App.Backend;
using ApexControl.App.Infrastructure;
using ApexControl.Core;

namespace ApexControl.App.ViewModels;

// One row of the SOCD tab: two keys and what happens when both are held.
public sealed class SocdPairViewModel : ObservableObject
{
    private readonly Action _changed;
    private string _key1, _key2, _behavior;
    private int _number = 1;

    public SocdPairViewModel(string key1, string key2, string behavior, Action changed)
    {
        _key1 = key1;
        _key2 = key2;
        _behavior = behavior;
        _changed = changed;
    }

    public string Key1 { get => _key1; set { if (Set(ref _key1, value)) _changed(); } }
    public string Key2 { get => _key2; set { if (Set(ref _key2, value)) _changed(); } }
    public string Behavior { get => _behavior; set { if (Set(ref _behavior, value)) _changed(); } }
    public int Number { get => _number; set => Set(ref _number, value); }
}

// The SOCD tab. It works on a copy of the selected profile read from the keyboard; saving plans the change (only the SOCD block and the
// checksum may change), shows the exact bytes, backs up, writes GG's own way, and reads back - like a macro edit.
public sealed class SocdViewModel : ObservableObject
{
    private static readonly string[] DefaultKeyOrder = { "A", "D", "W", "S", "Q", "E", "Z", "C", "X", "V", "R", "F" };

    private readonly IKeyboardBackend _backend;
    private readonly IPanelHost _host;

    private byte[]? _r2, _r3;
    private SocdConfig? _loaded;
    private bool _enabled;
    private int _loadedSlot = 1;
    private string _loadedText = "Not loaded yet. Click \"Load from keyboard\" to read the profile you are editing.";

    public SocdViewModel(IKeyboardBackend backend, IPanelHost host)
    {
        _backend = backend;
        _host = host;
        _host.SlotDataChanged += slot => { if (slot == _host.EditSlot) ShowCached(); };
        _host.EditSlotChanged += ShowCached;

        KeyChoices = KeyNames.OrderedNames;
        BehaviorChoices = Enum.GetValues<SocdBehavior>().Select(SocdEditor.BehaviorName).ToList();

        LoadCommand = new AsyncCommand(_ => LoadAsync(), _ => _host.CanUseHardware);
        AddPairCommand = new RelayCommand(_ => AddPair(), _ => HasLoaded && Pairs.Count < SocdEditor.MaxPairs);
        RemovePairCommand = new RelayCommand(p => { if (p is SocdPairViewModel row) { Pairs.Remove(row); Renumber(); } }, _ => HasLoaded);
        ApplyCommand = new AsyncCommand(_ => ApplyAsync(), _ => CanApply);
    }

    public IReadOnlyList<string> KeyChoices { get; }
    public IReadOnlyList<string> BehaviorChoices { get; }
    public ObservableCollection<SocdPairViewModel> Pairs { get; } = new();

    public AsyncCommand LoadCommand { get; }
    public RelayCommand AddPairCommand { get; }
    public RelayCommand RemovePairCommand { get; }
    public AsyncCommand ApplyCommand { get; }

    public bool HasLoaded => _r2 is not null;
    public string LoadedText { get => _loadedText; private set => Set(ref _loadedText, value); }

    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) Changed(); } }

    // ---- what the editor says ---------------------------------------------------------------------

    private static SocdBehavior BehaviorFromName(string name) =>
        Enum.GetValues<SocdBehavior>().FirstOrDefault(b => SocdEditor.BehaviorName(b) == name, (SocdBehavior)255);

    // The configuration the rows describe, or a plain-words reason why they don't describe a valid one.
    private string? Build(out SocdConfig? cfg)
    {
        cfg = null;
        var pairs = new List<SocdPair>();
        foreach (SocdPairViewModel row in Pairs)
        {
            if (!KeyNames.TryParse(row.Key1 ?? "", out byte k1) || !KeyNames.TryParse(row.Key2 ?? "", out byte k2)) return $"Pair {row.Number}: pick both keys.";
            SocdBehavior b = BehaviorFromName(row.Behavior ?? "");
            pairs.Add(new SocdPair(k1, k2, b));
        }
        var built = new SocdConfig(Enabled, pairs);
        string? bad = SocdEditor.Validate(built);
        if (bad is null) cfg = built;
        return bad;
    }

    public string? ValidationMessage => HasLoaded ? Build(out _) : null;

    public bool HasChanges => Build(out SocdConfig? cfg) is null && _loaded is not null && !SameConfig(cfg!, _loaded);

    private static bool SameConfig(SocdConfig a, SocdConfig b) => a.Enabled == b.Enabled && a.Pairs.SequenceEqual(b.Pairs);

    public string Summary
    {
        get
        {
            if (!HasLoaded) return "";
            if (Build(out SocdConfig? cfg) is not null) return "";
            return "Will write: " + string.Join("; ", SocdEditor.Describe(cfg!)) + (HasChanges ? "" : "  (no changes yet)");
        }
    }

    public bool CanApply => _host.CanUseHardware && HasLoaded && HasChanges;

    private void Changed()
    {
        Raise(nameof(ValidationMessage)); Raise(nameof(Summary)); Raise(nameof(HasChanges)); Raise(nameof(CanApply));
        AddPairCommand?.RaiseCanExecuteChanged();
        ApplyCommand?.RaiseCanExecuteChanged();
    }

    // Called by the main window whenever the keyboard/GG/busy state changes.
    public void HardwareStateChanged()
    {
        Raise(nameof(CanApply));
        LoadCommand?.RaiseCanExecuteChanged();
        ApplyCommand?.RaiseCanExecuteChanged();
    }

    private void AddPair()
    {
        var used = Pairs.SelectMany(p => new[] { p.Key1, p.Key2 }).ToHashSet();
        string[] free = DefaultKeyOrder.Where(k => !used.Contains(k)).Take(2).ToArray();
        if (free.Length < 2) return;
        Pairs.Add(new SocdPairViewModel(free[0], free[1], SocdEditor.BehaviorName(SocdBehavior.LastInputPriority), Changed));
        Renumber();
    }

    private void Renumber()
    {
        for (int i = 0; i < Pairs.Count; i++) Pairs[i].Number = i + 1;
        Changed();
    }

    // ---- operations -------------------------------------------------------------------------------

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
            if (img.StoredCrc != img.ComputedCrc) { Invalidate($"Config {_loadedSlot}'s checksum is not valid, so this app won't edit it. Use Restore... (bottom left) to put a good backup back."); return; }
            SocdConfig cfg = SocdEditor.Read(img);

            _r2 = r2; _r3 = r3; _loaded = cfg;
            _enabled = cfg.Enabled;
            Pairs.Clear();
            foreach (SocdPair p in cfg.Pairs)
                Pairs.Add(new SocdPairViewModel(KeyNames.Name(p.Key1), KeyNames.Name(p.Key2), SocdEditor.BehaviorName(p.Behavior), Changed));
            LoadedText = note;
        }
        catch (InvalidOperationException ex)
        {
            Invalidate($"Config {_loadedSlot}'s SOCD settings aren't in a shape this app understands, so it won't edit them: " + ex.Message);
            return;
        }
        Raise(nameof(Enabled)); Raise(nameof(HasLoaded));
        Renumber();
    }

    private void Invalidate(string note)
    {
        _r2 = _r3 = null;
        _loaded = null;
        Pairs.Clear();
        _enabled = false;
        LoadedText = note;
        Raise(nameof(Enabled)); Raise(nameof(HasLoaded));
        AddPairCommand?.RaiseCanExecuteChanged();
        Changed();
    }

    private async Task ApplyAsync()
    {
        if (_r2 is null || _r3 is null || Build(out SocdConfig? cfg) is not null || cfg is null) return;
        if (_loadedSlot != _host.EditSlot) { Invalidate($"Now editing Config {_host.EditSlot}. Click Load from keyboard to read it."); return; }
        int slot = _loadedSlot;

        MacroPlan plan;
        try { plan = SocdPlanner.Plan(_r2, _r3, cfg, slot); }
        catch (Exception ex) { _host.SetResult(false, "Could not work out that change: " + ex.Message); return; }
        if (!plan.Ok) { _host.SetResult(false, plan.Error ?? "That change was refused."); return; }

        bool go = _host.ConfirmDetailed(
            "Write SOCD",
            plan.Headline + PlanFormatter.WriteNotice(slot),
            PlanFormatter.Describe(plan, "SOCD settings now:", "SOCD settings after this change:"));
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
            Invalidate($"The last write did not finish cleanly, so the settings were cleared. Load from keyboard to see what Config {slot} holds now.");
        }
    }
}
