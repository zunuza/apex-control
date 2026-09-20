using System.IO;
using ApexControl.Core;

namespace ApexControl.App.Backend;

// The real thing: talks to the keyboard through ApexControl.Core. One hardware operation at a time.
public sealed class RealBackend : IKeyboardBackend
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string BackupsRoot => AppPaths.BackupsRoot;

    public KeyboardState GetState()
    {
        bool gg = GgProcess.IsRunning(out string[] names);
        bool present;
        try { present = KeyboardLink.IsPresent(); }
        catch { present = false; }
        return new KeyboardState(present, gg, names);
    }

    public IReadOnlyList<BackupInfo> ListBackups() => BackupStore.List(BackupsRoot);

    public SlotSummary? LatestSummary(int slot)
    {
        foreach (BackupInfo b in BackupStore.List(BackupsRoot).AsEnumerable().Reverse())
        {
            byte[]? r2 = BackupStore.TryLoad(b.Path, slot, 2);
            if (r2 is { Length: ReadSession.Region02Pages * ProfileImage.PageSize }) return ProfileInfo.Summarize(slot, r2);
        }
        return null;
    }

    public Task<OpResult> BackupAllAsync(IOperationSink sink, int activeSlot = 1) => Run(() =>
    {
        RequireGgClosed();
        using KeyboardLink link = KeyboardLink.Open();
        DumpResult res = ProfileReader.ReadSlots(link, Enumerable.Range(1, ReadSession.Slots).ToArray(), sink);

        string dir = Path.Combine(BackupsRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        BackupStore.SaveDump(dir, res);
        sink.Info($"Saved to {dir}");
        ProfileReader.CloseSession(link, activeSlot, sink);
        return new OpResult(true, $"Backed up all {ReadSession.Slots} profiles to {Path.GetFileName(dir)}.");
    });

    public Task<OpResult> SwitchProfileAsync(int slot, IOperationSink sink) => Run(() =>
    {
        RequireGgClosed();
        using KeyboardLink link = KeyboardLink.Open();
        foreach (PlanItem item in ProfileSwitch.BuildPlan(slot)) Runner.Execute(link, item, sink: sink);
        return new OpResult(true, $"Switched to Config {slot}.");
    });

    public Task<OpResult> RestoreSlotAsync(string backupDir, int slot, IOperationSink sink) => Run(() =>
    {
        if (!BackupStore.TryLoadSlot(backupDir, slot, out byte[] r2, out byte[] r3))
            return new OpResult(false, $"That backup has no slot {slot} files.");
        ProfileImage img = ProfileWriter.ImageFromDump(r2, r3);
        if (img.StoredCrc != img.ComputedCrc)
            return new OpResult(false, "That backup's checksum is not valid, so it will not be written.");

        RequireGgClosed();
        using KeyboardLink link = KeyboardLink.Open();
        Thread.Sleep(500);
        WriteResult wr = ProfileWriter.Execute(link, img, sink, slot);
        if (!wr.Success)
            return new OpResult(false, $"Write failed at step {wr.StepsDone}/{wr.TotalSteps}: {wr.Error}. Slot {slot} may be partially written - restore a backup again before using GG.");

        Thread.Sleep(500);
        VerifyResult v = ProfileWriter.VerifyReadBack(link, img, sink, slot);
        if (v.ReadFailed) return new OpResult(false, "Written, but the read-back check could not run.");
        return v.Ok
            ? new OpResult(true, $"Restored slot {slot} and verified it by reading it back.")
            : new OpResult(false, $"Read-back mismatch: {v.Region02Diffs} differing byte(s) in region 02, {v.Region03Diffs} in region 03.");
    });

    public Task<SlotReadResult> ReadSlotAsync(int slot, IOperationSink sink) => RunRead(() =>
    {
        RequireGgClosed();
        using KeyboardLink link = KeyboardLink.Open();
        DumpResult res = ProfileReader.ReadSlots(link, new[] { slot }, sink);
        ProfileReader.CloseSession(link, slot, sink);
        return new SlotReadResult(true, $"Read slot {slot} from the keyboard.", res.Data[(slot, 2)], res.Data[(slot, 3)]);
    });

    public Task<OpResult> ApplyProfilePlanAsync(MacroPlan plan, IOperationSink sink) => Run(() =>
    {
        if (!plan.Ok) return new OpResult(false, plan.Error ?? "That edit was refused.");
        int slot = plan.Slot;
        RequireGgClosed();
        using KeyboardLink link = KeyboardLink.Open();

        // 1. Read the slot again and make sure it is exactly what the plan was made from. (The close-out leaves this slot active,
        //    as it is in GG's own captures of saving that profile.)
        DumpResult now = ProfileReader.ReadSlots(link, new[] { slot }, sink);
        ProfileReader.CloseSession(link, slot, sink);
        byte[] r2 = now.Data[(slot, 2)], r3 = now.Data[(slot, 3)];
        ProfileImage current = ProfileWriter.ImageFromDump(r2, r3);
        bool same = current.Region02.AsSpan(0, ProfileWriter.SafeEnd).SequenceEqual(plan.Before.Region02.AsSpan(0, ProfileWriter.SafeEnd))
                    && current.Region03.AsSpan().SequenceEqual(plan.Before.Region03);
        if (!same)
            return new OpResult(false, $"Slot {slot} on the keyboard is not what you were shown (it changed since you loaded it). Nothing was written - load it again and redo the edit.");

        // 2. Pre-write backup of what is there right now.
        string backup = Path.Combine(BackupsRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + plan.BackupTag + (slot == 1 ? "" : $"-config{slot}"));
        BackupStore.SaveSlotSnapshot(backup, slot, r2, r3);
        sink.Info($"Pre-write backup saved: {backup}");

        // 3. A live command GG sends just before its save in some edits (the SOCD on/off switch), then write and read back.
        if (plan.PreWriteCommand is { } live)
        {
            sink.Info($"Sending GG's live command {live[0]:x2} {live[1]:x2} before the save...");
            Runner.Execute(link, new PlanItem(new Step(false, live), Reply.None), sink: sink);
            Thread.Sleep(400);
        }
        if (plan.PreWriteFeature is { } table)
        {
            sink.Info("Sending GG's live actuation table before the save...");
            Actuation.Send(link, table);
            Thread.Sleep(400);
        }
        Thread.Sleep(500);
        WriteResult wr = ProfileWriter.Execute(link, plan.After, sink, slot);
        if (!wr.Success)
            return new OpResult(false, $"Write failed at step {wr.StepsDone}/{wr.TotalSteps}: {wr.Error}. Slot {slot} may be partially written - use Restore... (bottom left) and pick \"{Path.GetFileName(backup)}\" before using GG.");

        Thread.Sleep(500);
        VerifyResult v = ProfileWriter.VerifyReadBack(link, plan.After, sink, slot);
        if (v.ReadFailed) return new OpResult(false, "Written, but the read-back check could not run. Load it again to see what the keyboard holds.");
        return v.Ok
            ? new OpResult(true, $"Saved to Config {slot}. Read back from the keyboard and it matches what was written.")
            : new OpResult(false, $"Read-back mismatch: {v.Region02Diffs} differing byte(s) in region 02, {v.Region03Diffs} in region 03. Use Restore... (bottom left) and pick \"{Path.GetFileName(backup)}\".");
    });
    public Task<OpResult> ApplyActuationAsync(double globalMm, IReadOnlyDictionary<byte, double> perKeyMm, IOperationSink sink, int slot = 1) => Run(() =>
    {
        byte[] frame;
        try { frame = Actuation.BuildFrameMm(globalMm, perKeyMm); }
        catch (ArgumentException ex) { return new OpResult(false, ex.Message + " Nothing was sent."); }

        RequireGgClosed();
        sink.Info($"Sending the actuation table: global {globalMm:0.0} mm, {perKeyMm.Count} key(s) different.");
        Actuation.Send(frame);
        SaveActuation(slot, new ActuationDraft(globalMm, new Dictionary<byte, double>(perKeyMm), DateTime.Now));
        return new OpResult(true, $"Sent to the keyboard for Config {slot}: {Actuation.Describe(globalMm, perKeyMm)}. Press a key to feel it.");
    });

    private sealed record ActuationFile(double GlobalMm, Dictionary<string, double> PerKey, DateTime SentAt);

    // What was last sent, per profile (slot number -> table). The keyboard cannot be asked for its actuation.
    private static string ActuationFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexControl", "actuation-profiles.json");

    private static Dictionary<string, ActuationFile> ReadActuationFile()
    {
        try
        {
            if (!File.Exists(ActuationFilePath)) return new();
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ActuationFile>>(File.ReadAllText(ActuationFilePath)) ?? new();
        }
        catch (Exception) { return new(); }
    }

    public void RememberActuation(int slot, ActuationDraft draft) => SaveActuation(slot, draft);

    private static void SaveActuation(int slot, ActuationDraft d)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ActuationFilePath)!);
            Dictionary<string, ActuationFile> all = ReadActuationFile();
            all[slot.ToString()] = new ActuationFile(d.GlobalMm, d.PerKeyMm.ToDictionary(kv => kv.Key.ToString("X2"), kv => kv.Value), d.SentAt);
            File.WriteAllText(ActuationFilePath, System.Text.Json.JsonSerializer.Serialize(all));
        }
        catch (Exception) { /* remembering the last values is a convenience; never fail a send over it */ }
    }
    public IReadOnlyList<SteelSeriesDevice> ScanDevices()
    {
        try { return Compatibility.Scan(); }
        catch (Exception) { return Array.Empty<SteelSeriesDevice>(); }
    }

    public async Task<ReadCheckResult> ReadCheckAsync(SteelSeriesDevice device, IOperationSink sink)
    {
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                RequireGgClosed();
                return Compatibility.ReadCheck(device, sink);
            });
        }
        catch (Exception ex) { return new ReadCheckResult(false, ex.Message, Array.Empty<string>(), null); }
        finally { _gate.Release(); }
    }

    private readonly ApexControl.App.Infrastructure.AutoStart _autoStart = new();

    public AutoStartState GetAutoStart() => new(_autoStart.IsEnabled, _autoStart.DisabledInWindows, _autoStart.Command);

    public void SetAutoStart(bool enabled)
    {
        if (!enabled) { _autoStart.Disable(); return; }
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Can't tell which program file to start. Run ApexControl.exe itself (for example the published one) and try again.");
        _autoStart.Enable(exe);
    }

    private static string ProfileNamesPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexControl", "profile-names.json");

    public IReadOnlyDictionary<int, string> LoadProfileNames()
    {
        try
        {
            if (!File.Exists(ProfileNamesPath)) return new Dictionary<int, string>();
            var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ProfileNamesPath));
            var names = new Dictionary<int, string>();
            foreach (var kv in raw ?? new()) if (int.TryParse(kv.Key, out int slot) && slot is >= 1 and <= 5 && !string.IsNullOrWhiteSpace(kv.Value)) names[slot] = kv.Value;
            return names;
        }
        catch (Exception) { return new Dictionary<int, string>(); }
    }

    public void SaveProfileNames(IReadOnlyDictionary<int, string> names)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProfileNamesPath)!);
            File.WriteAllText(ProfileNamesPath, System.Text.Json.JsonSerializer.Serialize(names.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)));
        }
        catch (Exception) { /* nicknames are a convenience; never fail an action over them */ }
    }

    public ActuationDraft? LoadLastActuation(int slot = 1)
    {
        try
        {
            if (!ReadActuationFile().TryGetValue(slot.ToString(), out ActuationFile? f)) return null;
            var perKey = new Dictionary<byte, double>();
            foreach (var kv in f.PerKey) perKey[Convert.ToByte(kv.Key, 16)] = kv.Value;
            return new ActuationDraft(f.GlobalMm, perKey, f.SentAt);
        }
        catch (Exception) { return null; }
    }
    private static void RequireGgClosed()
    {
        if (GgProcess.IsRunning(out string[] names))
            throw new KeyboardException("SteelSeries GG is running (" + string.Join(", ", names) + "). Quit it from its tray icon first.");
    }

    private async Task<SlotReadResult> RunRead(Func<SlotReadResult> work)
    {
        await _gate.WaitAsync();
        try { return await Task.Run(work); }
        catch (Exception ex) { return new SlotReadResult(false, ex.Message, null, null); }
        finally { _gate.Release(); }
    }

    private async Task<OpResult> Run(Func<OpResult> work)
    {
        await _gate.WaitAsync();
        try { return await Task.Run(work); }
        catch (Exception ex) { return new OpResult(false, ex.Message); }
        finally { _gate.Release(); }
    }
}
