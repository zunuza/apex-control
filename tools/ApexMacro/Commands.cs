using ApexControl.Core;

namespace ApexMacro;

// The console front end: argument parsing, prompts and printing. All the real work is in ApexControl.Core.
internal static class Commands
{
    private static readonly ConsoleSink Sink = new();

    public static string BackupsRoot => Path.Combine("..", "..", "backups");

    private static bool Confirm(string word)
    {
        Console.Write($"Type {word} (exactly, capitals) to go ahead; anything else cancels: ");
        return Console.ReadLine()?.Trim() == word;
    }

    private static bool GgIsRunning(bool force)
    {
        if (!force && GgProcess.IsRunning(out string[] running))
        {
            Console.WriteLine("SteelSeries software is still running: " + string.Join(", ", running));
            Console.WriteLine("Fully quit it first (tray icon -> Quit), then re-run. (--force skips this check.)");
            return true;
        }
        return false;
    }

    // ---- dump ---------------------------------------------------------------------------------

    public static int Dump(string outDir, bool force, int activeSlot)
    {
        if (GgIsRunning(force)) return 1;

        using KeyboardLink link = KeyboardLink.Open();
        DumpResult result;
        try { result = ProfileReader.ReadSlots(link, Enumerable.Range(1, ReadSession.Slots).ToArray(), Sink); }
        catch (Exception) { return 1; }

        // All data is in hand: save it BEFORE the (less important) close-out, so a problem there can't lose it.
        string summary = BackupStore.SaveDump(outDir, result);
        Console.WriteLine();
        Console.WriteLine(summary);
        Console.WriteLine($"Saved to {Path.GetFullPath(outDir)}");
        ProfileReader.CloseSession(link, activeSlot, Sink);
        return 0;
    }

    // ---- bind / unbind ------------------------------------------------------------------------

    public static int Bind(string[] a) => Edit("bind", a);
    public static int Unbind(string[] a) => Edit("unbind", a);

    // bind   <KEY> <CHORD> [--hold ms] [--dry-run] [--from dir] [--force]   e.g. bind F11 Q --hold 100 / bind F9 W+Q
    //        (if KEY already has a macro, it is REPLACED)
    // unbind <KEY>         [--dry-run] [--from dir] [--force]               removes KEY's macro, restores its normal function
    private static int Edit(string verb, string[] a)
    {
        var pos = new List<string>();
        int hold = 100; bool dry = false, force = false; string? fromDir = null;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == "--dry-run") dry = true;
            else if (a[i] == "--force") force = true;
            else if (a[i] == "--hold" && i + 1 < a.Length) hold = int.Parse(a[++i]);
            else if (a[i] == "--from" && i + 1 < a.Length) fromDir = a[++i];
            else pos.Add(a[i]);
        }

        int want = verb == "bind" ? 2 : 1;
        if (pos.Count != want || !KeyNames.TryParse(pos[0], out byte key))
        {
            Console.WriteLine(verb == "bind"
                ? "Usage: ApexMacro bind <KEY> <CHORD> [--hold ms] [--dry-run] [--from dir] [--force]   e.g. bind F11 Q --hold 100"
                : "Usage: ApexMacro unbind <KEY> [--dry-run] [--from dir] [--force]   e.g. unbind F11");
            return 2;
        }
        string keyName = pos[0].ToUpperInvariant();
        var chord = new List<byte>();
        if (verb == "bind")
        {
            foreach (string part in pos[1].Split('+'))
            {
                if (!KeyNames.TryParse(part, out byte k)) { Console.WriteLine($"Unknown key name '{part}'."); return 2; }
                chord.Add(k);
            }
            if (hold is < 1 or > 5000) { Console.WriteLine("--hold must be 1-5000 ms."); return 2; }
        }

        // 1. Current slot 1: from the keyboard (real run) or from a backup folder (dry run).
        byte[] r2, r3;
        KeyboardLink? link = null;
        try
        {
            if (dry)
            {
                string? dir = fromDir ?? BackupStore.NewestWithSlot1(BackupsRoot);
                if (dir is null) { Console.WriteLine("Dry run needs a backup to work from (run `dump` first, or pass --from <dir>)."); return 1; }
                Console.WriteLine($"Dry run - nothing is sent. Working from backup: {Path.GetFullPath(dir)}");
                BackupStore.TryLoadSlot1(dir, out r2, out r3);
            }
            else
            {
                if (GgIsRunning(force)) return 1;
                link = KeyboardLink.Open();
                DumpResult res;
                try { res = ProfileReader.ReadSlots(link, new[] { 1 }, Sink); } catch (Exception) { return 1; }
                ProfileReader.CloseSession(link, 1, Sink);
                r2 = res.Data[(1, 2)]; r3 = res.Data[(1, 3)];
            }

            Console.WriteLine("Current slot 1: " + ProfileInfo.Summarize(1, r2));
            ProfileImage before = ProfileWriter.ImageFromDump(r2, r3);
            if (before.StoredCrc != before.ComputedCrc) { Console.WriteLine("The slot's stored CRC is not valid, so I won't touch it."); return 1; }

            // 2. Apply the edit in memory and show exactly what changes.
            ProfileImage after = before.Clone();
            string headline;
            try
            {
                Console.WriteLine("Macros now:");
                foreach (string l in MacroEditor.DescribeMacros(before)) Console.WriteLine("  " + l);
                if (verb == "unbind")
                {
                    MacroEditor.RemoveMacro(after, key);
                    headline = $"unbind {keyName}: remove its macro, restore its normal function";
                }
                else if (MacroEditor.IsBound(after, key))
                {
                    MacroEditor.ReplaceChordMacro(after, key, chord.ToArray(), (ushort)hold);
                    headline = $"REPLACE the macro on {keyName} -> press {pos[1].ToUpperInvariant()}, hold {hold} ms";
                }
                else
                {
                    MacroEditor.AddChordMacro(after, key, chord.ToArray(), (ushort)hold);
                    headline = $"bind {keyName} -> press {pos[1].ToUpperInvariant()}, hold {hold} ms";
                }
            }
            catch (InvalidOperationException ex) { Console.WriteLine("Can't apply that edit: " + ex.Message); return 1; }

            Console.WriteLine();
            Console.WriteLine("Planned change: " + headline);
            if (!PrintDiff(ImageDiffer.Compute(before, after, r2))) { Console.WriteLine("The change touches something unexpected - refusing."); return 1; }
            Console.WriteLine("Macros after:");
            foreach (string l in MacroEditor.DescribeMacros(after)) Console.WriteLine("  " + l);
            var plan = ProfileWriter.BuildWritePlan(after);
            Console.WriteLine($"Write plan: {plan.Count} steps ({plan.Count(p => p.Frame.IsFeature)} Feature frames, {plan.Count(p => !p.Frame.IsFeature)} Output commands), slot 1 only.");

            if (dry) { Console.WriteLine("Dry run complete."); return 0; }

            // 3. Back up what's there right now.
            string backup = Path.Combine(BackupsRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + $"-pre-{verb}-{keyName}");
            BackupStore.SaveSlot1Snapshot(backup, r2, r3);
            Console.WriteLine();
            Console.WriteLine($"Pre-write backup saved: {Path.GetFullPath(backup)}");

            // 4. Confirm, write, verify.
            Console.WriteLine("This WRITES slot 1 (\"Config 1\") of the keyboard. Slots 2-5 are not touched.");
            if (!Confirm("WRITE")) { Console.WriteLine("Cancelled. Nothing was written."); return 0; }

            Thread.Sleep(500);
            if (!WriteAndReport(link!, after)) return 1;
            Thread.Sleep(500);
            return VerifyAndReport(link!, after) ? 0 : 1;
        }
        finally { link?.Dispose(); }
    }

    // ---- restore ------------------------------------------------------------------------------

    // restore <backup dir> [--dry-run] [--force]: writes a saved slot-1 image back to the keyboard.
    public static int Restore(string[] a)
    {
        bool force = a.Contains("--force"), dry = a.Contains("--dry-run");
        string? dir = a.FirstOrDefault(x => !x.StartsWith("--"));
        if (dir is null) { Console.WriteLine("Usage: ApexMacro restore <backup dir containing slot1-region02.bin / slot1-region03.bin> [--dry-run]"); return 2; }
        if (!BackupStore.TryLoadSlot1(dir, out byte[] r2, out byte[] r3))
        {
            Console.WriteLine($"No slot-1 backup found in: {dir}");
            Console.WriteLine("Pass the real folder path (not a placeholder). Backups on disk:");
            foreach (BackupInfo b in BackupStore.List(BackupsRoot))
                Console.WriteLine("  " + Path.GetFullPath(b.Path) + (b.HasSlot1 ? "" : "   (no slot 1 files)"));
            return 1;
        }
        if (!dry && GgIsRunning(force)) return 1;

        Console.WriteLine("Backup: " + ProfileInfo.Summarize(1, r2));
        ProfileImage img = ProfileWriter.ImageFromDump(r2, r3);
        if (img.StoredCrc != img.ComputedCrc) { Console.WriteLine("That backup's CRC is not valid - refusing to write it."); return 1; }

        int tailNotFf = 0;
        for (int i = ProfileWriter.SafeEnd; i < ProfileWriter.ReadLen; i++) if (r2[i] != 0xFF) tailNotFf++;
        Console.WriteLine(tailNotFf == 0
            ? $"Unused tail after the CRC ({ProfileWriter.ReadLen - ProfileWriter.SafeEnd} bytes): already 0xFF in the backup."
            : $"Unused tail after the CRC ({ProfileWriter.ReadLen - ProfileWriter.SafeEnd} bytes): {tailNotFf} byte(s) in the backup are not 0xFF; they will be written as 0xFF.");
        Console.WriteLine($"Write plan: {ProfileWriter.BuildWritePlan(img).Count} steps, slot 1 only; profile data (CRC-covered part) is written exactly as in the backup.");
        if (dry) { Console.WriteLine("Dry run complete - nothing was sent."); return 0; }

        Console.WriteLine("This WRITES the backup above to slot 1 (\"Config 1\") of the keyboard.");
        if (!Confirm("RESTORE")) { Console.WriteLine("Cancelled. Nothing was written."); return 0; }

        using KeyboardLink link = KeyboardLink.Open();
        Thread.Sleep(500);
        if (!WriteAndReport(link, img)) return 1;
        Thread.Sleep(500);
        return VerifyAndReport(link, img) ? 0 : 1;
    }

    // ---- profile ------------------------------------------------------------------------------

    // profile <1-5> [--dry-run] [--force]: switch the keyboard's active profile (Config N), exactly as GG's
    // sidebar double-click does. No profile data is read or written.
    public static int Profile(string[] a)
    {
        bool dry = a.Contains("--dry-run"), force = a.Contains("--force");
        string? arg = a.FirstOrDefault(x => !x.StartsWith("--"));
        if (arg is null || !int.TryParse(arg, out int slot) || slot is < 1 or > ProfileSwitch.Slots)
        {
            Console.WriteLine("Usage: ApexMacro profile <1-5> [--dry-run] [--force]   e.g. profile 2");
            return 2;
        }

        List<PlanItem> plan = ProfileSwitch.BuildPlan(slot);
        Console.WriteLine($"Switch the active profile to Config {slot}. Commands: " +
            string.Join("  ", plan.Select(p => p.Frame.Data[0].ToString("x2") + (p.Frame.Data[1] != 0 ? " " + p.Frame.Data[1].ToString("x2") : ""))));
        if (dry) { Console.WriteLine("Dry run - nothing is sent."); return 0; }
        if (GgIsRunning(force)) return 1;

        using KeyboardLink link = KeyboardLink.Open();
        try
        {
            foreach (PlanItem item in plan) Runner.Execute(link, item, sink: Sink);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Switch did not complete: " + ex.Message);
            return 1;
        }

        Console.WriteLine($"Sent. The keyboard should now be using Config {slot}.");
        Console.WriteLine("There is no way to read the active profile back, so check by feel: in the current setup Config 1 has macros on F9 (types q) and F10 (W+Q together); Configs 2-5 don't.");
        Console.WriteLine("Note: GG remembers its own active profile and re-selects it when it starts.");
        return 0;
    }

    // ---- compat -------------------------------------------------------------------------------

    // compat [--read] [--force]: is this keyboard like the Apex Pro TKL? The plain form sends NOTHING to any device (it only
    // lists what Windows sees). --read also reads Config 1 from keyboards with the TKL's command interface, using the
    // same commands GG sends at startup; it writes nothing. --export (after --read) also writes a local .zip for the developer,
    // personal content stripped; --export --full keeps the raw read. Nothing is ever uploaded.
    public static int Compat(string[] a)
    {
        bool read = a.Contains("--read"), force = a.Contains("--force"), export = a.Contains("--export"), full = a.Contains("--full");
        List<SteelSeriesDevice> devices = Compatibility.Scan();
        var results = new Dictionary<int, ReadCheckResult>();

        if (read)
        {
            var candidates = devices.Where(d => Compatibility.Assess(d).Match is LayoutMatch.ExactModel or LayoutMatch.SameInterfaceLayout).ToList();
            if (candidates.Count == 0) Console.WriteLine("No SteelSeries device has the TKL's command interface, so there is nothing to read.");
            else if (!GgIsRunning(force))
            {
                Console.WriteLine("This READS Config 1 from: " + string.Join(", ", candidates.Select(d => $"{d.Product} ({d.VendorId:X4}:{d.ProductId:X4})")));
                Console.WriteLine("It uses the same read commands GG sends at startup on the TKL and writes nothing. Like GG's own read it ends by");
                Console.WriteLine("selecting Config 1 as the active profile. Unknown models have never been tested with these commands.");
                if (!Confirm("READ")) { Console.WriteLine("Cancelled. Nothing was sent."); read = false; }
                else
                    foreach (SteelSeriesDevice d in candidates)
                    {
                        Console.WriteLine($"Reading {d.Product} ({d.ProductId:X4})...");
                        results[d.ProductId] = Compatibility.ReadCheck(d, Sink);
                    }
            }
        }

        string report = Compatibility.BuildReport(devices, results);
        Console.WriteLine();
        Console.WriteLine(report);
        try
        {
            string dir = Compatibility.DefaultReportFolder();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"compatibility-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, report);
            Console.WriteLine($"Report saved to: {path}");
        }
        catch (Exception ex) { Console.WriteLine("(Could not save the report: " + ex.Message + ")"); }

        if (export)
        {
            try
            {
                var items = devices.Where(d => results.ContainsKey(d.ProductId)).Select(d => (d, results[d.ProductId])).ToList();
                string zip = CompatibilityExport.WriteZip(Compatibility.DefaultReportFolder(), items, report, full, DateTime.Now);
                Console.WriteLine($"Export saved to: {zip}  ({(full ? "raw read, including profile name and macros" : "personal content removed")}; nothing was sent anywhere)");
            }
            catch (Exception ex) { Console.WriteLine("(Could not create the export: " + ex.Message + ")"); }
        }
        return 0;
    }

    // ---- shared printing ----------------------------------------------------------------------

    private static string Spaced(string hex) => string.Join(" ", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));

    private static bool PrintDiff(ImageDiff d)
    {
        if (d.Problems.Any(p => p.StartsWith("UNEXPECTED: region 03")))
        {
            Console.WriteLine("  UNEXPECTED: region 03 changed.");
            return false;
        }
        if (d.TailNote is not null) Console.WriteLine(d.TailNote);
        foreach (string p in d.Problems) Console.WriteLine("  " + p);
        foreach (DiffEntry e in d.Entries)
        {
            if (e.Area == "keymap entry")
                Console.WriteLine($"  {e.Area,-17} @{e.Offset,-5} ({e.KeyName,-5}): {Spaced(e.BeforeHex)}  ->  {Spaced(e.AfterHex)}");
            else
            {
                string more = e.Truncated ? " ..." : "";
                Console.WriteLine($"  {e.Area,-17} @{e.Offset,-5} ({e.Length,2} bytes): {Spaced(e.BeforeHex)}{more}  ->  {Spaced(e.AfterHex)}{more}");
            }
        }
        Console.WriteLine($"  total bytes changed in the profile data: {d.TotalChanged}   region 03: unchanged   everything else: unchanged");
        return d.Ok;
    }

    private static bool WriteAndReport(KeyboardLink link, ProfileImage img)
    {
        WriteResult wr = ProfileWriter.Execute(link, img, Sink);
        if (wr.Success) return true;
        Console.WriteLine();
        Console.WriteLine($"WRITE FAILED at step {wr.StepsDone}/{wr.TotalSteps}: {wr.Error}");
        Console.WriteLine("Slot 1 may be partially written. Do not use GG yet. Restore the backup with:");
        Console.WriteLine("  dotnet run -- restore <the pre-write backup folder printed above>");
        return false;
    }

    private static bool VerifyAndReport(KeyboardLink link, ProfileImage expected)
    {
        VerifyResult v = ProfileWriter.VerifyReadBack(link, expected, Sink);
        if (v.ReadFailed) return false;
        Console.WriteLine();
        Console.WriteLine("  " + v.Summary);
        Console.WriteLine(v.Ok
            ? "READ-BACK VERIFIED: slot 1 on the keyboard matches what was written (all 13 KB of region 02 including the 0xFF tail, and all of region 03)."
            : $"READ-BACK MISMATCH: {v.Region02Diffs} differing byte(s) in region 02 (first at {v.FirstRegion02Diff}), {v.Region03Diffs} in region 03.");
        return v.Ok;
    }

    // ---- offline self-checks that need the backups folder ------------------------------------

    // Informational (not pass/fail): turn the newest real hardware dump into a write transaction and
    // compare with GG's captured macro-B transaction. Expected differences are only what changed on
    // the keyboard since that capture (actuation bytes, later macros) and the CRC.
    public static void InfoHardwareImage(string refDir, List<Step> txB)
    {
        string? dir = BackupStore.NewestWithSlot1(Path.Combine(refDir, "..", "..", "backups"));
        if (dir is null) { Console.WriteLine("  [INFO] no hardware backup found - skipped hardware-image comparison"); return; }

        BackupStore.TryLoadSlot1(dir, out byte[] r2, out byte[] r3);
        ProfileImage img = ProfileWriter.ImageFromDump(r2, r3);
        Array.Clear(img.Region02, ProfileWriter.SafeEnd, ProfileWriter.ReadLen - ProfileWriter.SafeEnd);   // GG's captured frames have zeros there; compare like with like
        List<Step> got = Transaction.Build(img);

        var diffs = new List<string>();
        for (int i = 0; i < Math.Min(got.Count, txB.Count); i++)
            for (int b = 0; b < got[i].Data.Length && b < txB[i].Data.Length; b++)
                if (got[i].Data[b] != txB[i].Data[b]) diffs.Add($"step {i} byte {b}");
        bool crcOk = img.StoredCrc == img.ComputedCrc;
        Console.WriteLine($"  [INFO] newest hardware dump ({Path.GetFileName(dir)}): CRC {(crcOk ? "valid" : "INVALID")}; as a write transaction it differs from GG's captured macro-B in {diffs.Count} byte(s) (expected: whatever changed on the keyboard since that capture)");
    }

    // Real-hardware invariant: the dump taken right after our F11 write, with F11's macro removed,
    // must equal the dump taken right before it (CRC-covered data and region 03).
    public static int CheckHardwareUnbindInverse()
    {
        string root = BackupsRoot;
        if (!Directory.Exists(root)) { Console.WriteLine("  [INFO] no backups folder - skipped hardware inverse check"); return 0; }
        string? pre = Directory.GetDirectories(root).Where(d => d.EndsWith("-pre-bind-F11")).OrderBy(d => d).FirstOrDefault();
        if (pre is null) { Console.WriteLine("  [INFO] no pre-bind-F11 backup - skipped hardware inverse check"); return 0; }

        string preStamp = Path.GetFileName(pre)[..15];
        string? post = Directory.GetDirectories(root)
            .Where(d => Path.GetFileName(d).Length == 15 && string.CompareOrdinal(Path.GetFileName(d), preStamp) > 0 && File.Exists(Path.Combine(d, "slot1-region02.bin")))
            .OrderBy(d => d).FirstOrDefault();
        if (post is null) { Console.WriteLine("  [INFO] no post-write dump found - skipped hardware inverse check"); return 0; }

        BackupStore.TryLoadSlot1(pre, out byte[] p2, out byte[] p3);
        BackupStore.TryLoadSlot1(post, out byte[] q2, out byte[] q3);
        var before = ProfileWriter.ImageFromDump(p2, p3);
        var after = ProfileWriter.ImageFromDump(q2, q3);
        try { MacroEditor.RemoveMacro(after, 0x44); }
        catch (InvalidOperationException ex) { Console.WriteLine($"  [INFO] hardware inverse check skipped: {ex.Message}"); return 0; }

        bool same = before.Region02.AsSpan(0, ProfileWriter.ReadLen).SequenceEqual(after.Region02.AsSpan(0, ProfileWriter.ReadLen)) && before.Region03.AsSpan().SequenceEqual(after.Region03);
        Console.WriteLine($"  [{(same ? "PASS" : "FAIL")}] real keyboard: dump after F11 write ({Path.GetFileName(post)}) minus F11's macro == dump before it ({Path.GetFileName(pre)}), CRC included");
        return same ? 0 : 1;
    }
}
