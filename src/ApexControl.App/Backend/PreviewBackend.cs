using ApexControl.Core;

namespace ApexControl.App.Backend;

// Canned data so the window can be rendered and exercised without a keyboard. Nothing here touches hardware.
public sealed class PreviewBackend : IKeyboardBackend
{
    private readonly bool _ggRunning;
    private readonly bool _present;

    public PreviewBackend(bool ggRunning = false, bool present = true)
    {
        _ggRunning = ggRunning;
        _present = present;
    }

    public string BackupsRoot => @"C:\Users\You\ApexControl\backups";

    public KeyboardState GetState() => new(_present, _ggRunning, _ggRunning ? new[] { "SteelSeriesGGClient" } : Array.Empty<string>());

    // A test can point the backup list at real folders (the restore path reads the files itself).
    public IReadOnlyList<BackupInfo>? BackupsOverride { get; set; }

    public IReadOnlyList<BackupInfo> ListBackups() => BackupsOverride ?? new[]
    {
        new BackupInfo(@"backups\20260918-220230", "20260918-220230", true, true, "slot 1: 'Config 1'  CRC OK  macros: 2", new[] { 1, 2, 3, 4, 5 }),
        new BackupInfo(@"backups\20260918-221456-pre-bind-F11", "20260918-221456-pre-bind-F11", true, false, "Pre-write backup of slot 1\nslot 1: 'Config 1'  CRC OK  macros: 2", new[] { 1 }),
        new BackupInfo(@"backups\20260918-230949-pre-bind-F9", "20260918-230949-pre-bind-F9", true, false, "Pre-write backup of slot 1\nslot 1: 'Config 1'  CRC OK  macros: 3", new[] { 1 }),
        new BackupInfo(@"backups\20260919-000747", "20260919-000747", true, true, "slot 1: 'Config 1'  CRC OK  macros: 2\nslot 2: 'Config 2'  CRC OK  macros: 0", new[] { 1, 2, 3, 4, 5 }),
    };

    public SlotSummary? LatestSummary(int slot) => slot switch
    {
        1 => new SlotSummary(1, "Config 1", 1, 1, 2, new[] { (210, 7), (250, 0) }),
        5 => null,
        _ => new SlotSummary(slot, $"Config {slot}", 1, 1, 0, Array.Empty<(int, int)>()),
    };

    public List<int> BackupAllActive { get; } = new();
    public Task<OpResult> BackupAllAsync(IOperationSink sink, int activeSlot = 1)
    {
        BackupAllActive.Add(activeSlot);
        return Task.FromResult(new OpResult(true, "Preview: nothing was read."));
    }
    public Task<OpResult> SwitchProfileAsync(int slot, IOperationSink sink) => Task.FromResult(new OpResult(true, $"Preview: would switch to Config {slot}."));
    public List<(string Dir, int Slot)> RestoresRequested { get; } = new();
    public Task<OpResult> RestoreSlotAsync(string backupDir, int slot, IOperationSink sink)
    {
        RestoresRequested.Add((backupDir, slot));
        return Task.FromResult(new OpResult(true, "Preview: nothing was written."));
    }

    public List<int> SlotsRead { get; } = new();

    public Task<SlotReadResult> ReadSlotAsync(int slot, IOperationSink sink)
    {
        SlotsRead.Add(slot);
        // A made-up slot 1: every key at its normal function, F9 -> Q (200 ms), F10 -> W+Q (141 ms).
        var img = new ProfileImage();
        foreach (var kv in KeymapLayout.DefaultOffsets)
        {
            img.Region02[kv.Value] = 0x51;
            img.Region02[kv.Value + 1] = kv.Key;
        }
        MacroEditor.AddChordMacro(img, 0x42, new byte[] { 0x14 }, 200);
        MacroEditor.AddChordMacro(img, 0x43, new byte[] { 0x1A, 0x14 }, 141);
        SocdEditor.Apply(img, new SocdConfig(true, new[] { new SocdPair(0x04, 0x07, SocdBehavior.LastInputPriority) }));
        return Task.FromResult(new SlotReadResult(true, $"Preview: made-up slot {slot}.", img.Region02.AsSpan(0, ProfileWriter.ReadLen).ToArray(), img.Region03.ToArray()));
    }

    public List<MacroPlan> AppliedPlans { get; } = new();

    public Task<OpResult> ApplyProfilePlanAsync(MacroPlan plan, IOperationSink sink)
    {
        AppliedPlans.Add(plan);
        return Task.FromResult(new OpResult(true, "Preview: nothing was written."));
    }

    // What the window asked to send (nothing is ever sent from the preview backend).
    public List<(double GlobalMm, Dictionary<byte, double> PerKeyMm)> ActuationRequests { get; } = new();
    public List<int> ActuationSlots { get; } = new();
    private readonly Dictionary<int, ActuationDraft> _sentActuation = new();

    public Task<OpResult> ApplyActuationAsync(double globalMm, IReadOnlyDictionary<byte, double> perKeyMm, IOperationSink sink, int slot = 1)
    {
        ActuationRequests.Add((globalMm, new Dictionary<byte, double>(perKeyMm)));
        ActuationSlots.Add(slot);
        _sentActuation[slot] = new ActuationDraft(globalMm, new Dictionary<byte, double>(perKeyMm), DateTime.Now);
        return Task.FromResult(new OpResult(true, $"Preview: would send for Config {slot}: {Actuation.Describe(globalMm, perKeyMm)}."));
    }

    // A made-up scan: the TKL, and a pretend other model with the same command interface.
    public IReadOnlyList<SteelSeriesDevice> ScanDevices()
    {
        static HidInterfaceInfo Cmd() => new("mi_01", 65, 65, 643, new uint[] { 0xFFC00001 });
        static HidInterfaceInfo Watch() => new("mi_04", 65, 0, 0, new uint[] { 0xFFC10001 });
        static HidInterfaceInfo Kbd() => new("mi_00 col02", 34, 2, 0, new uint[] { 0x00010006 });
        return new[]
        {
            new SteelSeriesDevice(0x1038, 0x1610, 0x0416, "SteelSeries Example Keyboard", "SteelSeries", new[] { Kbd(), Cmd(), Watch() }),
            new SteelSeriesDevice(0x1038, 0x1614, 0x0416, "SteelSeries Apex Pro TKL", "SteelSeries", new[] { Kbd(), Cmd(), Watch() }),
        };
    }

    public List<int> ReadChecksRequested { get; } = new();

    public async Task<ReadCheckResult> ReadCheckAsync(SteelSeriesDevice device, IOperationSink sink)
    {
        ReadChecksRequested.Add(device.ProductId);
        SlotReadResult slot = await ReadSlotAsync(1, sink);
        // The TKL reads as a full match; the pretend other model returns noise, which is not.
        byte[] r2 = slot.Region02!;
        if (device.ProductId != 0x1614) new Random(3).NextBytes(r2);
        return new ReadCheckResult(true, "ok", new[] { "4.16.8" }, Compatibility.Analyze(r2, slot.Region03!), r2, slot.Region03);
    }

    public bool AutoStartOn { get; set; }
    public bool AutoStartDisabledInWindows { get; set; }
    public bool AutoStartShouldFail { get; set; }
    public AutoStartState GetAutoStart() => new(AutoStartOn && !AutoStartDisabledInWindows, AutoStartOn && AutoStartDisabledInWindows, AutoStartOn ? "\"C:\\Apps\\ApexControl.exe\" --tray" : null);
    public void SetAutoStart(bool enabled)
    {
        if (AutoStartShouldFail) throw new InvalidOperationException("Windows would not let this change the startup entry.");
        AutoStartOn = enabled;
        if (enabled) AutoStartDisabledInWindows = false;
    }

    public List<Dictionary<int, string>> SavedProfileNames { get; } = new();
    public IReadOnlyDictionary<int, string> LoadProfileNames() => new Dictionary<int, string> { [1] = "Work", [2] = "Gaming" };
    public void SaveProfileNames(IReadOnlyDictionary<int, string> names) => SavedProfileNames.Add(new Dictionary<int, string>(names));

    public void RememberActuation(int slot, ActuationDraft draft) => _sentActuation[slot] = draft;

    // Config 1 starts with a remembered table (as if sent earlier); the other profiles have nothing remembered until something is sent.
    public ActuationDraft? LoadLastActuation(int slot = 1) =>
        _sentActuation.TryGetValue(slot, out ActuationDraft? d) ? d
        : slot == 1 ? new(2.0, new Dictionary<byte, double> { [0x16] = 0.1, [0x1A] = 1.5, [0x04] = 1.5, [0x07] = 1.5 }, new DateTime(2026, 9, 19, 1, 30, 0))
        : null;
}
