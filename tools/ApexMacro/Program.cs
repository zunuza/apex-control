// ApexMacro
//
// Macro tooling for the Apex Pro TKL.
//   verify        offline: rebuilds captured GG write transactions and macro edits, byte-for-byte
//   verify-dump   offline: proves the read plan equals GG's captured startup read sequence
//   dump          READ-ONLY against the keyboard: saves all 5 profile slots as a backup
//   dump --dry-run   prints the read plan without touching hardware
//   bind <KEY> <CHORD>   WRITES a macro to slot 1 (fresh read, pre-write backup, diff, typed confirmation, read-back check)
//   bind ... --dry-run   same analysis from a backup, sends nothing (binding a key that already has a macro REPLACES it)
//   unbind <KEY>         WRITES slot 1: removes KEY's macro and restores its normal function (same safeguards)
//   profile <1-5>        switches the ACTIVE profile (Config N), like GG's sidebar double-click; no data read or written
//   restore <dir>        WRITES a saved slot-1 backup back to the keyboard
//   compat [--read]      read-only: is this keyboard like the Apex Pro TKL? (--read also reads Config 1; writes nothing)

using ApexControl.Core;

namespace ApexMacro;

internal static class Program
{
    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (KeyboardException ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        string refDir = Path.Combine("..", "..", "docs", "reference");
        string cmd = args.Length > 0 ? args[0] : "";

        switch (cmd)
        {
            case "verify":
                return Verify(args.Length > 1 ? args[1] : refDir);
            case "verify-dump":
                return VerifyDump(args.Length > 1 ? args[1] : refDir);
            case "dump":
                return DumpCommand(args.Skip(1).ToArray());
            case "bind":
                return Commands.Bind(args.Skip(1).ToArray());
            case "unbind":
                return Commands.Unbind(args.Skip(1).ToArray());
            case "profile":
                return Commands.Profile(args.Skip(1).ToArray());
            case "compat":
                return Commands.Compat(args.Skip(1).ToArray());
            case "restore":
                return Commands.Restore(args.Skip(1).ToArray());
        }

        Console.WriteLine("Usage: ApexMacro verify | verify-dump | dump [--dry-run] [--force] [--out <dir>] [--activate N] | bind <KEY> <CHORD> [--hold ms] [--dry-run] | unbind <KEY> [--dry-run] | profile <1-5> [--dry-run] | restore <backup dir> [--dry-run] | compat [--read]");
        return 2;
    }

    private static int DumpCommand(string[] a)
    {
        bool dry = a.Contains("--dry-run"), force = a.Contains("--force");
        int ai = Array.IndexOf(a, "--activate");
        int activeSlot = ai >= 0 && ai + 1 < a.Length ? int.Parse(a[ai + 1]) : 1;
        if (activeSlot is < 1 or > 5) { Console.WriteLine("--activate must be 1-5."); return 2; }
        int oi = Array.IndexOf(a, "--out");
        string outDir = oi >= 0 && oi + 1 < a.Length ? a[oi + 1]
            : Path.Combine("..", "..", "backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        if (dry)
        {
            var plan = ReadSession.BuildPlan();
            int outputs = plan.Count(p => !p.Frame.IsFeature), feats = plan.Count(p => p.Frame.IsFeature);
            Console.WriteLine($"Dry run - nothing is sent. Plan: {plan.Count} steps ({outputs} Output commands, {feats} Feature read-requests).");
            Console.WriteLine("Opcodes used: " + string.Join(" ", plan.Select(p => p.Frame.Data[0]).Distinct().OrderBy(b => b).Select(b => $"{b:x2}")) + "  (write opcodes 03/05/88/eb/74 are never sent)");
            return 0;
        }

        Console.WriteLine("This READS all 5 profile slots from the keyboard (same as GG at startup). It writes nothing.");
        Console.WriteLine($"Output folder: {Path.GetFullPath(outDir)}");
        Console.Write("Is SteelSeries GG fully quit? Press Enter to continue, Ctrl+C to cancel... ");
        Console.ReadLine();
        Console.WriteLine($"The session ends by selecting Config {activeSlot} as the active profile (use --activate N to change).");
        return Commands.Dump(outDir, force, activeSlot);
    }

    // The read plan must match GG's captured startup sequence exactly.
    private static int VerifyDump(string dir)
    {
        var want = Transaction.Load(Path.Combine(dir, "rx-startup.txt"));
        var got = ReadSession.BuildPlan().Select(p => p.Frame).ToList();
        int failures = 0;

        Console.WriteLine($"  captured GG read session: {want.Count} host frames; our plan: {got.Count}");
        if (want.Count != got.Count) { Console.WriteLine("  [FAIL] frame count differs"); failures++; }
        for (int i = 0; i < Math.Min(want.Count, got.Count); i++)
        {
            int at = FirstDiff(got[i].Data, want[i].Data);
            if (got[i].IsFeature != want[i].IsFeature || at >= 0)
            {
                Console.WriteLine($"  [FAIL] frame {i}: {want[i].Describe()} - first differing byte {at}");
                if (++failures >= 5) break;
            }
        }

        Console.WriteLine(failures == 0
            ? "  [PASS] every frame of the read plan is byte-identical to GG's captured startup read session"
            : $"  {failures} mismatch(es)");
        return failures == 0 ? 0 : 1;
    }

    private const byte Q = 0x14, W = 0x1A, F9 = 0x42, F10 = 0x43;

    private static int Verify(string dir)
    {
        var baseline = Transaction.Load(Path.Combine(dir, "tx-baseline.txt"));
        var txA = Transaction.Load(Path.Combine(dir, "tx-macro-A.txt"));
        var txB = Transaction.Load(Path.Combine(dir, "tx-macro-B.txt"));
        var imgBase = ProfileImage.FromSteps(baseline);
        var imgA = ProfileImage.FromSteps(txA);
        var imgB = ProfileImage.FromSteps(txB);
        int failures = 0;

        // 1. Checksum algorithm vs. the CRC stored in each reference image.
        foreach ((string n, ProfileImage i) in new[] { ("baseline", imgBase), ("macro-A", imgA), ("macro-B", imgB) })
            failures += Check($"CRC of {n}: computed {i.ComputedCrc:x8} == stored {i.StoredCrc:x8}", i.ComputedCrc == i.StoredCrc);

        // 2. Transaction builder alone: image -> frames must equal the capture.
        failures += Compare("Rebuild macro-A transaction from its own image", Transaction.Build(imgA), txA, strict: true);
        failures += Compare("Rebuild macro-B transaction from its own image", Transaction.Build(imgB), txB, strict: true);
        failures += Compare("Rebuild baseline transaction from its own image", Transaction.Build(imgBase), baseline, strict: false);

        // 3. Macro edit: baseline + "F9 -> Q, hold 172ms" must equal macro-A. The
        //    baseline capture was taken with different actuation settings than A,
        //    so first sync that region (and only that region) from A, and report it.
        var editA = imgBase.Clone();
        failures += SyncActuation(editA, imgA, new[] { (210, 215) }, "baseline -> macro-A");
        MacroEditor.AddChordMacro(editA, F9, new[] { Q }, 0xAC);
        failures += Compare("baseline + [F9 = Q, 172ms] == captured macro-A", Transaction.Build(editA), txA, strict: true);

        // 4. Second macro appended: A + "F10 -> W+Q chord, hold 141ms" must equal macro-B.
        var editB = imgA.Clone();
        MacroEditor.AddChordMacro(editB, F10, new[] { W, Q }, 0x8D);
        failures += Compare("macro-A + [F10 = W+Q, 141ms] == captured macro-B", Transaction.Build(editB), txB, strict: true);

        // 5. Both edits chained from baseline.
        var chain = imgBase.Clone();
        failures += SyncActuation(chain, imgB, new[] { (210, 215), (250, 255) }, "baseline -> macro-B");
        MacroEditor.AddChordMacro(chain, F9, new[] { Q }, 0xAC);
        MacroEditor.AddChordMacro(chain, F10, new[] { W, Q }, 0x8D);
        failures += Compare("baseline + both edits == captured macro-B", Transaction.Build(chain), txB, strict: true);

        // 6. Removal is the exact inverse of adding (proved against GG's own captures).
        var minusF10 = imgB.Clone();
        MacroEditor.RemoveMacro(minusF10, F10);
        failures += SameProfile("macro-B minus F10's macro == captured macro-A (CRC included)", minusF10, imgA);

        var roundTrip = imgB.Clone();
        MacroEditor.AddChordMacro(roundTrip, 0x44, new[] { Q }, 100);
        MacroEditor.RemoveMacro(roundTrip, 0x44);
        failures += SameProfile("macro-B + F11 macro, then - F11 macro == macro-B", roundTrip, imgB);

        // 7. Removing the FIRST macro must re-pack the area and fix the other entries' pointers; then re-adding works.
        failures += CheckRemoveFirst(imgB);

        // 8. Replacing an existing macro.
        failures += CheckReplace(imgB);

        // 9. Refusals.
        failures += CheckRefuses("remove a macro from a key that has none", () => MacroEditor.RemoveMacro(imgB.Clone(), 0x44));
        failures += CheckRefuses("add a macro to a key that already has one", () => MacroEditor.AddChordMacro(imgB.Clone(), F9, new[] { Q }, 100));
        failures += CheckRefuses("edit a HID code that is not in the keymap", () => MacroEditor.RemoveMacro(imgB.Clone(), 0xFE));

        // 9b. The planner the desktop app uses must agree with the editor calls verified above.
        failures += CheckPlanner(imgA, imgB);

        // 10. Real keyboard: after-F11-write dump minus F11's macro == before-write dump.
        failures += Commands.CheckHardwareUnbindInverse();
        // 11. Profile switching: our plan must equal every GG sequence we captured, and the "69 iff slot 1 is
        //     involved" rule must hold across the whole switch chains.
        failures += CheckProfileSwitch(dir);
        // 12. GG's own save of a macro with waits between keys (F11: Q down, 300, Q up, 300, W down, 300, W up).
        failures += CheckSequenceCapture(dir);
        failures += CheckSocdCaptures(dir);
        failures += CheckSocdEditor(dir);
        failures += CheckCompatibility(dir);
        failures += CheckCompatibilityExport(dir);
        failures += CheckProfile2Captures(dir);
        failures += CheckActuationImage(dir);
        Commands.InfoHardwareImage(dir, txB);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED - the builder reproduces GG's captured transactions byte-for-byte." : $"{failures} CHECK(S) FAILED.");
        return failures == 0 ? 0 : 1;
    }

    // Copies every byte that differs between `target` and `reference` inside the CRC-covered
    // region, except the macro footprint (keymap entries in `skipRanges`, the macro area, the
    // CRC itself). Those leftovers are the actuation-table differences between two captures
    // taken at different times. Fails if they stray outside page 2's first half, where the actuation table lives.
    private static int SyncActuation(ProfileImage target, ProfileImage reference, (int From, int To)[] skipRanges, string label)
    {
        int count = 0, min = int.MaxValue, max = -1;
        for (int i = 0; i < ProfileImage.CrcOffset; i++)
        {
            if (i >= MacroEditor.MacroAreaBase && i < MacroEditor.MacroAreaLimit) continue;
            if (skipRanges.Any(r => i >= r.From && i < r.To)) continue;
            if (target.Region02[i] == reference.Region02[i]) continue;
            target.Region02[i] = reference.Region02[i];
            count++; min = Math.Min(min, i); max = Math.Max(max, i);
        }
        bool inside = count == 0 || (min >= 2048 && max < 2560);
        Console.WriteLine($"  [{(inside ? "PASS" : "FAIL")}] non-macro differences {label}: {count} bytes, payload offsets {(count == 0 ? "none" : $"{min}..{max}")} (expected inside page 2, first half: 2048..2559, where the actuation table lives)");
        return inside ? 0 : 1;
    }

    private static int CheckProfileSwitch(string dir)
    {
        int fails = 0;
        var observed = ProfileSwitch.LoadObserved(Path.Combine(dir, "profile-switch-observed.txt"));
        var prevByCapture = new Dictionary<string, int>();

        foreach ((string label, List<Step> steps) in observed)
        {
            Step? sel = steps.FirstOrDefault(s => s.Data[0] == 0x89);
            if (sel is null) { fails += Check($"observed block has an 89 command: {label}", false); continue; }
            int slot = sel.Data[1];
            bool has69 = steps[0].Data[0] == 0x69;

            var plan = ProfileSwitch.BuildPlan(slot, has69).Select(p => p.Frame).ToList();
            bool same = plan.Count == steps.Count && plan.Select((s, i) => s.Data.AsSpan().SequenceEqual(steps[i].Data)).All(x => x);
            fails += Check($"switch plan for Config {slot} ({(has69 ? "with" : "without")} leading 69) == GG's {label}", same);

            // rule: in the pure switch captures (each starts on Config 1), 69 is sent iff slot 1 is source or target
            string cap = label.Split(' ')[0];
            if (cap.StartsWith("profile-switch"))
            {
                int prev = prevByCapture.TryGetValue(cap, out int pv) ? pv : 1;
                fails += Check($"  rule: Config {prev} -> {slot}: 69 {(has69 ? "present" : "absent")} == (slot 1 involved: {(prev == 1 || slot == 1)})", has69 == (prev == 1 || slot == 1));
                prevByCapture[cap] = slot;
            }
        }

        var end3 = ReadSession.BuildEndSequence(3).Select(p => p.Frame).ToList();
        var obs3 = observed.First(o => o.Label.Contains("Config 3 active")).Steps;
        fails += Check("read-session close-out with Config 3 active == GG's captured close-out", end3.Count == obs3.Count && end3.Select((s, i) => s.Data.AsSpan().SequenceEqual(obs3[i].Data)).All(x => x));
        return fails;
    }
    private static int SameProfile(string label, ProfileImage got, ProfileImage want)
    {
        bool same = got.Region02.AsSpan(0, ProfileWriter.SafeEnd).SequenceEqual(want.Region02.AsSpan(0, ProfileWriter.SafeEnd))
                    && got.Region03.AsSpan().SequenceEqual(want.Region03);
        return Check(label, same);
    }

    private static int CheckRefuses(string label, Action act)
    {
        try { act(); }
        catch (InvalidOperationException) { return Check($"refuses to {label}", true); }
        return Check($"refuses to {label}", false);
    }

    private static int CheckRemoveFirst(ProfileImage imgB)
    {
        int fails = 0;
        var before = MacroEditor.ReadMacros(imgB, out _);
        byte[] f10Record = before.First(m => m.EntryOffset == MacroEditor.EntryOffsetFor(F10)).Bytes;

        var m = imgB.Clone();
        MacroEditor.RemoveMacro(m, F9);
        byte[] r = m.Region02;
        var after = MacroEditor.ReadMacros(m, out int end);
        fails += Check("remove F9 (the FIRST macro): F9 entry back to '51 42 00 00 00'", Convert.ToHexString(r, 210, 5) == "5142000000");
        fails += Check("  F10's entry now points at ptr 0 and its record moved to the start of the area", r[250] == 0x71 && r[253] == 0 && r.AsSpan(MacroEditor.MacroAreaBase, f10Record.Length).SequenceEqual(f10Record));
        fails += Check("  exactly 1 macro left, area re-packed (used end = base + F10's record length)", after.Count == 1 && end == MacroEditor.MacroAreaBase + f10Record.Length);
        fails += Check("  every byte freed at the end of the area is zero", r.AsSpan(end, before.Sum(x => x.Length) - f10Record.Length).ToArray().All(b => b == 0));
        fails += Check("  CRC valid", m.StoredCrc == m.ComputedCrc);

        MacroEditor.AddChordMacro(m, F9, new[] { Q }, 0xAC);
        var again = MacroEditor.ReadMacros(m, out _);
        fails += Check("  re-adding F9 appends it after F10 (ptr 7), 2 macros, CRC valid", again.Count == 2 && Convert.ToHexString(m.Region02, 210, 5) == "7100000700" && m.StoredCrc == m.ComputedCrc);
        return fails;
    }

    private static int CheckReplace(ProfileImage imgB)
    {
        int fails = 0;
        var m = imgB.Clone();
        MacroEditor.ReplaceChordMacro(m, F9, new[] { W }, 50);
        var macros = MacroEditor.ReadMacros(m, out _);
        var descr = MacroEditor.DescribeMacros(m);
        fails += Check("replace F9's macro with 'W, 50 ms': still 2 macros, CRC valid", macros.Count == 2 && m.StoredCrc == m.ComputedCrc);
        fails += Check("  F9 now reads 'W down, wait 50 ms, W up'", descr.Any(d => d.StartsWith("F9") && d.EndsWith("W down, wait 50 ms, W up")));
        fails += Check("  F10's macro is unchanged", descr.Any(d => d.StartsWith("F10") && d.EndsWith("W down, Q down, wait 141 ms, W up, Q up")));
        return fails;
    }

    // The image as it would come back from the keyboard: 13312 bytes of region 02 (as read), all of region 03.
    private static (byte[] R2, byte[] R3) AsRead(ProfileImage img) => (img.Region02.AsSpan(0, ProfileWriter.ReadLen).ToArray(), img.Region03.ToArray());

    private static bool SameCovered(ProfileImage a, ProfileImage b) =>
        a.Region02.AsSpan(0, ProfileWriter.SafeEnd).SequenceEqual(b.Region02.AsSpan(0, ProfileWriter.SafeEnd)) && a.Region03.AsSpan().SequenceEqual(b.Region03);

    private static int CheckPlanner(ProfileImage imgA, ProfileImage imgB)
    {
        int fails = 0;
        var (b2, b3) = AsRead(imgB);

        MacroPlan add = MacroPlanner.Plan(b2, b3, MacroEditRequest.ForChord(0x44, new[] { Q }, 100));
        var direct = imgB.Clone();
        MacroEditor.AddChordMacro(direct, 0x44, new[] { Q }, 100);
        fails += Check("planner: bind F11 -> Q 100 ms equals MacroEditor.AddChordMacro, CRC valid, only expected areas changed", add.Ok && SameCovered(add.After, direct) && add.After.StoredCrc == add.After.ComputedCrc && add.Diff!.Ok);
        fails += Check("  headline and backup tag", add.Headline == "Bind F11: press Q, hold 100 ms" && add.BackupTag == "pre-bind-F11");
        fails += Check("  before/after macro lists: 2 -> 3", add.MacrosBefore.Count == 2 && add.MacrosAfter.Count == 3);

        MacroPlan rep = MacroPlanner.Plan(b2, b3, MacroEditRequest.ForChord(F9, new[] { W }, 50));
        var directRep = imgB.Clone();
        MacroEditor.ReplaceChordMacro(directRep, F9, new[] { W }, 50);
        fails += Check("planner: replacing F9's macro equals MacroEditor.ReplaceChordMacro", rep.Ok && SameCovered(rep.After, directRep) && rep.Headline.StartsWith("Replace the macro on F9"));

        var (a2, a3) = AsRead(imgA);
        MacroPlan rem = MacroPlanner.Plan(b2, b3, MacroEditRequest.Removal(F10));
        fails += Check("planner: removing F10 from macro-B equals captured macro-A (CRC included)", rem.Ok && SameCovered(rem.After, imgA) && rem.BackupTag == "pre-unbind-F10");

        fails += Check("planner: removing a macro from a key that has none is refused", !MacroPlanner.Plan(b2, b3, MacroEditRequest.Removal(0x44)).Ok);
        fails += Check("planner: hold 0 ms and hold 5001 ms are refused", !MacroPlanner.Plan(b2, b3, MacroEditRequest.ForChord(0x44, new[] { Q }, 0)).Ok && !MacroPlanner.Plan(b2, b3, MacroEditRequest.ForChord(0x44, new[] { Q }, 5001)).Ok);
        fails += Check("planner: empty chord and 9-key chord are refused", !MacroPlanner.Plan(b2, b3, MacroEditRequest.ForChord(0x44, Array.Empty<byte>(), 100)).Ok && !MacroPlanner.Plan(b2, b3, MacroEditRequest.ForChord(0x44, new byte[9], 100)).Ok);

        var corrupt = (byte[])b2.Clone();
        corrupt[MacroEditor.KeymapStart + 3] ^= 0x01;
        fails += Check("planner: a slot whose CRC does not match is refused", !MacroPlanner.Plan(corrupt, b3, MacroEditRequest.ForChord(0x44, new[] { Q }, 100)).Ok);

        // Keep adding 8-key macros until the area is full: it must stop cleanly, never write past the limit.
        var cur = (b2, b3);
        int added = 0; MacroPlan last = add;
        // (The captured images are sparse: use only keys whose keymap entry is in the ordinary "51 <key> 00 00 00" state.)
        IEnumerable<byte> plainKeys = KeymapLayout.DefaultOffsets.Keys.Where(k =>
            Convert.ToHexString(imgB.Region02, KeymapLayout.DefaultOffsets[k], 5) == $"51{k:X2}000000").Take(10);
        foreach (byte key in plainKeys)
        {
            last = MacroPlanner.Plan(cur.Item1, cur.Item2, MacroEditRequest.ForChord(key, new byte[] { 4, 5, 6, 7, 8, 9, 10, 11 }, 100));
            if (!last.Ok) break;
            added++;
            cur = AsRead(last.After);
        }
        fails += Check($"planner: the macro area fills up and the next macro is refused cleanly (accepted {added} big macros first; refusal: {last.Error})", !last.Ok && added >= 1 && last.Error!.Contains("limit"));

        fails += CheckSequences(imgB, b2, b3);

        // Chord recognition.
        var recs = MacroEditor.ReadMacros(imgB, out _);
        bool f9 = MacroEditor.TryParseChord(recs.First(r => r.EntryOffset == 210).Bytes, out byte[] c9, out ushort h9) && c9.SequenceEqual(new[] { Q }) && h9 == 0xAC;
        bool f10 = MacroEditor.TryParseChord(recs.First(r => r.EntryOffset == 250).Bytes, out byte[] c10, out ushort h10) && c10.SequenceEqual(new[] { W, Q }) && h10 == 0x8D;
        var odd = MacroEditor.BuildRecord(new[] { Q }, 100); odd[8] = 0x03;
        fails += Check("chord recognition: F9 = Q/172 ms, F10 = W+Q/141 ms, and a tampered record is NOT called a chord", f9 && f10 && !MacroEditor.TryParseChord(odd, out _, out _));
        return fails;
    }

    // GG's saves while configuring SOCD (Rapid Tap): docs/reference/tx-socd-*.txt. The whole setting sits in region 02
    // at 2887..2911, so each capture must equal the baseline image plus that handful of edited bytes, CRC included.
    private static int CheckSocdCaptures(string dir)
    {
        string P(string n) => Path.Combine(dir, $"tx-socd-{n}.txt");
        if (!new[] { "baseline", "last-input", "priority-key-1", "pair" }.All(n => File.Exists(P(n)))) { Console.WriteLine("  [INFO] SOCD captures not found - skipped"); return 0; }
        int fails = 0;
        ProfileImage Load(string n) => ProfileImage.FromSteps(Transaction.Load(P(n)));
        ProfileImage baseline = Load("baseline"), lastInput = Load("last-input"), key1 = Load("priority-key-1"), pair = Load("pair");

        foreach (var (n, img) in new[] { ("baseline", baseline), ("last-input", lastInput), ("priority-key-1", key1), ("pair", pair) })
            fails += Check($"SOCD capture '{n}': CRC computed {img.ComputedCrc:x8} == stored {img.StoredCrc:x8}", img.ComputedCrc == img.StoredCrc);

        fails += SameProfile("last-input image == baseline image (the pair A/D + Last Input Priority was already what the baseline held)", lastInput, baseline);
        Console.WriteLine($"  [INFO] SOCD block in the baseline (2887..2911): {Convert.ToHexString(baseline.Region02, 2887, 25)}");

        var edit = baseline.Clone();
        edit.Region02[2890] = 0x01;
        edit.UpdateCrc();
        fails += SameProfile("baseline + byte 2890 = 01 (Priority Key 1) == GG's priority-key-1 save, CRC included", edit, key1);

        var pairEdit = baseline.Clone();
        pairEdit.Region02[2893] = 0x1A; pairEdit.Region02[2894] = 0x16;
        pairEdit.UpdateCrc();
        fails += SameProfile("baseline + bytes 2893..2894 = 1A 16 (second pair W, S) == GG's pair save, CRC included", pairEdit, pair);

        bool r03 = baseline.Region03.AsSpan().SequenceEqual(key1.Region03) && baseline.Region03.AsSpan().SequenceEqual(pair.Region03);
        fails += Check("region 03 is identical in every SOCD capture (the setting is entirely in region 02)", r03);

        var extra = Transaction.Load(P("last-input")).Where(s => !s.IsFeature && s.Data[0] == 0x1A).Select(s => Convert.ToHexString(s.Data, 0, 2)).ToList();
        Console.WriteLine($"  [INFO] the last-input capture also sent live command(s) {string.Join(", ", extra)} before its save; the other three saves have none");
        return fails;
    }

    // The SOCD editor against all eight of GG's saves: reading each image gives the configuration that was set, and applying
    // any configuration to any other capture's image reproduces that capture exactly (bytes and CRC).
    private static int CheckSocdEditor(string dir)
    {
        string[] names = { "baseline", "last-input", "priority-key-1", "pair", "priority-key-2", "off", "no-pair", "pair-w-s-priority" };
        string P(string n) => Path.Combine(dir, $"tx-socd-{n}.txt");
        if (!names.All(n => File.Exists(P(n)))) { Console.WriteLine("  [INFO] not all SOCD captures found - skipped the editor check"); return 0; }
        int fails = 0;

        const byte A = 0x04, D = 0x07, S = 0x16;   // W is already a constant above
        SocdPair Ad(SocdBehavior b) => new(A, D, b);
        var want = new Dictionary<string, SocdConfig>
        {
            ["baseline"] = new(true, new[] { Ad(SocdBehavior.LastInputPriority) }),
            ["last-input"] = new(true, new[] { Ad(SocdBehavior.LastInputPriority) }),
            ["priority-key-1"] = new(true, new[] { Ad(SocdBehavior.Key1Priority) }),
            ["pair"] = new(true, new[] { Ad(SocdBehavior.LastInputPriority), new SocdPair(W, S, SocdBehavior.LastInputPriority) }),
            ["priority-key-2"] = new(true, new[] { Ad(SocdBehavior.Key2Priority) }),
            ["off"] = new(false, new[] { Ad(SocdBehavior.Key2Priority) }),
            ["no-pair"] = new(false, Array.Empty<SocdPair>()),
            ["pair-w-s-priority"] = new(true, new[] { Ad(SocdBehavior.LastInputPriority), new SocdPair(W, S, SocdBehavior.Key1Priority) }),
        };
        var img = names.ToDictionary(n => n, n => ProfileImage.FromSteps(Transaction.Load(P(n))));

        bool SameConfig(SocdConfig a, SocdConfig b) => a.Enabled == b.Enabled && a.Pairs.SequenceEqual(b.Pairs);
        var readWrong = names.Where(n => !SameConfig(SocdEditor.Read(img[n]), want[n])).ToList();
        fails += Check("SOCD editor: reading each of the 8 captured images gives the configuration that was set in GG" + (readWrong.Count == 0 ? "" : " (wrong: " + string.Join(", ", readWrong) + ")"), readWrong.Count == 0);

        int combos = 0, wrong = 0;
        foreach (string from in names)
            foreach (string to in names)
            {
                combos++;
                var start = img[from].Clone();
                SocdEditor.Apply(start, want[to]);
                if (!SameCovered(start, img[to])) { wrong++; Console.WriteLine($"         mismatch: {from} + config of {to}"); }
            }
        fails += Check($"SOCD editor: applying each configuration to each captured image reproduces GG's save byte-for-byte, CRC included ({combos - wrong}/{combos} combinations)", wrong == 0);

        // Planning: what it would write, and the live on/off command GG sends when the switch flips.
        (byte[] r2, byte[] r3) Dump(string n) => AsRead(img[n]);
        var (b2, b3) = Dump("baseline");
        MacroPlan toKey1 = SocdPlanner.Plan(b2, b3, want["priority-key-1"]);
        fails += Check("planner: baseline -> priority key 1 changes only the SOCD block + CRC and sends no live command", toKey1.Ok && toKey1.PreWriteCommand is null && toKey1.Diff!.Ok && SameCovered(toKey1.After, img["priority-key-1"]));
        MacroPlan toOff = SocdPlanner.Plan(b2, b3, want["off"]);
        fails += Check("planner: turning SOCD off adds GG's live command 1a 00 before the save", toOff.Ok && toOff.PreWriteCommand is { Length: 64 } c0 && c0[0] == 0x1A && c0[1] == 0x00 && SameCovered(toOff.After, img["off"]));
        var (o2, o3) = Dump("off");
        MacroPlan toOn = SocdPlanner.Plan(o2, o3, want["baseline"]);
        fails += Check("planner: turning it back on adds live command 1a 01", toOn.Ok && toOn.PreWriteCommand is { } c1 && c1[0] == 0x1A && c1[1] == 0x01 && SameCovered(toOn.After, img["baseline"]));
        MacroPlan noPair = SocdPlanner.Plan(b2, b3, want["no-pair"]);
        fails += Check("planner: deleting the only pair gives GG's off + placeholder-pair image", noPair.Ok && SameCovered(noPair.After, img["no-pair"]) && noPair.PreWriteCommand is { } c2 && c2[1] == 0);
        fails += Check("planner: writing what is already there is refused (nothing to do)", !SocdPlanner.Plan(b2, b3, want["baseline"]).Ok);

        SocdConfig Bad(params SocdPair[] pairs) => new(true, pairs);
        fails += Check("planner refuses: the same key twice in a pair", !SocdPlanner.Plan(b2, b3, Bad(new SocdPair(A, A, SocdBehavior.LastInputPriority))).Ok);
        fails += Check("planner refuses: a key used in two pairs", !SocdPlanner.Plan(b2, b3, Bad(Ad(SocdBehavior.LastInputPriority), new SocdPair(D, S, SocdBehavior.LastInputPriority))).Ok);
        fails += Check("planner refuses: six pairs", !SocdPlanner.Plan(b2, b3, Bad(Enumerable.Range(0, 6).Select(i => new SocdPair((byte)(0x04 + 2 * i), (byte)(0x05 + 2 * i), SocdBehavior.LastInputPriority)).ToArray())).Ok);
        fails += Check("planner refuses: SOCD on with no pairs", !SocdPlanner.Plan(b2, b3, Bad()).Ok);
        fails += Check("planner refuses: an unknown behaviour value", !SocdPlanner.Plan(b2, b3, Bad(new SocdPair(A, D, (SocdBehavior)9))).Ok);

        var five = Enumerable.Range(0, 5).Select(i => new SocdPair((byte)(0x04 + 2 * i), (byte)(0x05 + 2 * i), (SocdBehavior)(i % 3))).ToArray();
        MacroPlan fivePlan = SocdPlanner.Plan(b2, b3, new SocdConfig(true, five));
        fails += Check("planner: five pairs (the maximum) fit and read back the same", fivePlan.Ok && SameConfig(SocdEditor.Read(fivePlan.After), new SocdConfig(true, five)));

        // A macro edit must leave the SOCD block alone, and a SOCD edit must leave the macros alone.
        var withMacro = img["pair"].Clone();
        MacroEditor.AddChordMacro(withMacro, 0x44, new[] { Q }, 100);
        fails += Check("a macro edit leaves the SOCD block untouched", SameConfig(SocdEditor.Read(withMacro), want["pair"]));
        var socdOnMacros = img["baseline"].Clone();
        MacroEditor.AddChordMacro(socdOnMacros, 0x44, new[] { Q }, 100);
        var (m2, m3) = AsRead(socdOnMacros);
        MacroPlan keepMacros = SocdPlanner.Plan(m2, m3, want["priority-key-1"]);
        fails += Check("a SOCD edit leaves the macros untouched", keepMacros.Ok && MacroEditor.DescribeMacros(keepMacros.After).SequenceEqual(MacroEditor.DescribeMacros(socdOnMacros)));
        return fails;
    }

    // The compatibility check's decisions, tested with made-up devices and with real dumps (plus damaged copies of them).
    private static int CheckCompatibility(string dir)
    {
        int fails = 0;

        HidInterfaceInfo Kbd() => new("mi_00 col02", 34, 2, 0, new uint[] { 0x00010006 });
        HidInterfaceInfo Cmd(int input = 65, int output = 65, int feature = 643) => new("mi_01", input, output, feature, new uint[] { 0xFFC00001 });
        HidInterfaceInfo Watch() => new("mi_04", 65, 0, 0, new uint[] { 0xFFC10001 });
        SteelSeriesDevice Dev(int pid, params HidInterfaceInfo[] ifs) => new(0x1038, pid, 0x0416, "Test device", "SteelSeries", ifs);

        fails += Check("compat: the TKL's own interface set (1038:1614) is recognised as the exact model", Compatibility.Assess(Dev(0x1614, Kbd(), Cmd(), Watch())).Match == LayoutMatch.ExactModel);
        fails += Check("compat: another product ID with the TKL's command interface sizes is 'same interface layout' (promising, not proven)", Compatibility.Assess(Dev(0x1610, Kbd(), Cmd(), Watch())).Match == LayoutMatch.SameInterfaceLayout);
        fails += Check("compat: a vendor command interface with different sizes is 'different command interface'", Compatibility.Assess(Dev(0x1630, Kbd(), Cmd(65, 65, 1041), Watch())).Match == LayoutMatch.DifferentCommandInterface);
        fails += Check("compat: a device with no vendor command interface (a mouse) is 'no command interface'", Compatibility.Assess(Dev(0x1830, new HidInterfaceInfo("interface", 8, 0, 0, new uint[] { 0x00010002 }))).Match == LayoutMatch.NoCommandInterface);

        // Profile format tests need a real slot-1 dump; the backups folder is local (not in git), so skip if there is none.
        string? backup = BackupStore.NewestWithSlot1(Commands.BackupsRoot);
        if (backup is null || !BackupStore.TryLoadSlot1(backup, out byte[] r2, out byte[] r3) || r2.Length != ProfileWriter.ReadLen)
        {
            Console.WriteLine("  [INFO] no slot-1 backup found - skipped the profile-format checks of the compatibility check");
            return fails;
        }

        ImageChecks ok = Compatibility.Analyze(r2, r3);
        fails += Check($"compat: a real TKL slot-1 dump ({Path.GetFileName(backup)}) is a full match: checksum OK, {ok.KeymapRecognised} key entries recognised + {ok.KeymapUnused} unused of {ok.KeymapTotal}, macro area and SOCD understood",
            ok.Format == ProfileFormat.Match && ok.CrcOk && ok.KeymapRecognised + ok.KeymapUnused == ok.KeymapTotal);
        Console.WriteLine($"  [INFO] its report line: name '{ok.ProfileName}', macro area {ok.MacroArea}, SOCD {ok.Socd}");

        byte[] flipped = (byte[])r2.Clone();
        flipped[MacroEditor.KeymapStart + 3] ^= 0x10;
        fails += Check("compat: one flipped byte breaks the checksum and the verdict is 'no match'", Compatibility.Analyze(flipped, r3) is { Format: ProfileFormat.NoMatch, CrcOk: false });

        var otherKeys = ProfileWriter.ImageFromDump(r2, r3);
        for (int p = MacroEditor.KeymapStart; p < MacroEditor.KeymapEnd; p++) otherKeys.Region02[p] = 0;   // a keyboard whose key table is laid out differently
        otherKeys.UpdateCrc();
        ImageChecks partial = Compatibility.Analyze(otherKeys.Region02.AsSpan(0, ProfileWriter.ReadLen).ToArray(), r3);
        fails += Check("compat: a valid checksum but a different key table is 'partial match' and names the key table", partial.Format == ProfileFormat.PartialMatch && partial.CrcOk && partial.Verdict.Contains("key table"));

        var noise = new byte[ProfileWriter.ReadLen];
        new Random(7).NextBytes(noise);
        fails += Check("compat: random data is 'no match'", Compatibility.Analyze(noise, r3).Format == ProfileFormat.NoMatch);
        fails += Check("compat: too little data is 'no match' rather than a crash", Compatibility.Analyze(new byte[100], new byte[100]).Format == ProfileFormat.NoMatch);

        string report = Compatibility.BuildReport(new[] { Dev(0x1610, Kbd(), Cmd(), Watch()) }, new Dictionary<int, ReadCheckResult> { [0x1610] = new(true, "ok", new[] { "4.16.8" }, ok) });
        fails += Check("compat: the report names the device, its verdict and the checksum result, and contains no serial number or device path",
            report.Contains("1038:1610") && report.Contains("Read check") && report.Contains("MATCH") && !report.Contains("hid#") && !report.Contains("Serial"));
        return fails;
    }

    // Writing a profile other than Config 1: GG's own saves of Config 2 (profile2-macro-save: bind a macro on V;
    // profile2-actuation-socd: unbind it and change V's actuation). The builder given slot 2 must reproduce both, frame for frame.
    private static int CheckProfile2Captures(string dir)
    {
        int fails = 0;
        List<Step> Load(string name, out Step? live)
        {
            var all = Transaction.Load(Path.Combine(dir, name));
            live = all.FirstOrDefault(s => s.IsFeature && s.Data[0] == 0x31);        // the live actuation frame GG sends first
            return all.Where(s => !(s.IsFeature && s.Data[0] == 0x31)).ToList();
        }
        List<Step> tx1 = Load("tx-profile2-macro-save.txt", out _), tx2 = Load("tx-profile2-actuation-socd.txt", out Step? live2);
        var img1 = ProfileImage.FromSteps(tx1);
        var img2 = ProfileImage.FromSteps(tx2);

        fails += Check("slot 2: the captured images' checksums match ours", img1.ComputedCrc == img1.StoredCrc && img2.ComputedCrc == img2.StoredCrc);
        fails += Compare("slot 2: rebuild GG's macro-save transaction from its image (slot 2)", Transaction.Build(img1, 2), tx1, strict: true);
        fails += Compare("slot 2: rebuild GG's actuation save transaction from its image (slot 2)", Transaction.Build(img2, 2), tx2, strict: true);

        var t1 = Transaction.Build(img1, 2);
        bool slotBytes = t1.All(s => s.IsFeature || s.Data[0] switch
        {
            0x88 => s.Data[2] == 2, 0x05 => s.Data[2] == 2, 0xEB => s.Data[1] == 2, 0x89 => s.Data[1] == 2, _ => true,
        });
        fails += Check("slot 2: every begin, page commit, eb and 89 command names slot 2 (and none names slot 1)", slotBytes && t1.Count(s => !s.IsFeature && s.Data[0] == 0x05) == 52);
        bool slot1Differs = !Transaction.Build(img1, 1).Zip(t1, (a, b) => a.Data.AsSpan().SequenceEqual(b.Data)).All(x => x);
        fails += Check("slot 2: the same image built for slot 1 differs (the slot number is really in the frames)", slot1Differs);

        // Our editor applied to GG's slot 2 image gives GG's next slot 2 image. The captures differ in two things: V's macro and
        // V's actuation (a GG-side change), so the actuation bytes are copied across before comparing.
        const byte V = 0x19;
        void CopyActuation(ProfileImage from, ProfileImage to) { Array.Copy(from.Region02, 2375, to.Region02, 2375, 2517 - 2375); to.UpdateCrc(); }

        var unbound = img1.Clone();
        MacroEditor.RemoveMacro(unbound, V);
        CopyActuation(img2, unbound);
        fails += Check("slot 2: GG's image with the macro on V removed by our editor == GG's next image (CRC included)", SameCovered(unbound, img2));

        var rebound = img2.Clone();
        MacroEditor.AddEventMacro(rebound, V, new[] { MacroEvent.Down(Q), MacroEvent.Wait(141), MacroEvent.Up(Q) });
        CopyActuation(img1, rebound);
        fails += Check("slot 2: GG's image with 'V = Q down, wait 141 ms, Q up' added by our editor == GG's macro-save image (CRC included)", SameCovered(rebound, img1));

        // The planners carry the slot through, and the write plan is the captured length.
        var (r2, r3) = AsRead(img2);
        MacroPlan plan = MacroPlanner.Plan(r2, r3, MacroEditRequest.ForEvents(V, new[] { MacroEvent.Down(Q), MacroEvent.Wait(141), MacroEvent.Up(Q) }), slot: 2);
        fails += Check("slot 2: MacroPlanner plans the same edit for slot 2 (plan says slot 2, 164 steps = GG's transaction length)", plan.Ok && plan.Slot == 2 && plan.WriteSteps == tx2.Count);
        MacroPlan plan1 = MacroPlanner.Plan(r2, r3, MacroEditRequest.ForEvents(V, new[] { MacroEvent.Down(Q), MacroEvent.Wait(141), MacroEvent.Up(Q) }));
        fails += Check("slot 2: the default is still slot 1", plan1.Ok && plan1.Slot == 1);

        // SOCD: the live on/off command was only ever captured on Config 1, so flipping the switch elsewhere is refused;
        // changing pairs while it stays on is an ordinary image edit.
        // SOCD on and off on Config 2 (profile2-socd-on / -off): GG sends "1a <flag>" first, then the slot 2 save. The images differ
        // only in the flag byte and the CRC.
        SocdConfig on = new(true, new[] { new SocdPair(0x04, 0x07, SocdBehavior.LastInputPriority) });
        SocdConfig off = new(false, new[] { new SocdPair(0x04, 0x07, SocdBehavior.LastInputPriority) });
        List<Step> Load2(string name, out Step live) { var all = Transaction.Load(Path.Combine(dir, name)); live = all[0]; return all.Skip(1).ToList(); }
        List<Step> txOn = Load2("tx-profile2-socd-on.txt", out Step liveOn), txOff = Load2("tx-profile2-socd-off.txt", out Step liveOff);
        var imgOn = ProfileImage.FromSteps(txOn); var imgOff = ProfileImage.FromSteps(txOff);
        fails += Check("slot 2 SOCD: GG's first frame is 1a 01 (on) / 1a 00 (off), exactly as on Config 1",
            !liveOn.IsFeature && liveOn.Data[0] == 0x1A && liveOn.Data[1] == 1 && !liveOff.IsFeature && liveOff.Data[0] == 0x1A && liveOff.Data[1] == 0 && liveOn.Data.Skip(2).All(x => x == 0));
        fails += Check("slot 2 SOCD: the captured images' checksums match and they read back as SOCD on / off with the A + D pair",
            imgOn.ComputedCrc == imgOn.StoredCrc && imgOff.ComputedCrc == imgOff.StoredCrc && SocdEditor.Read(imgOn).Enabled && !SocdEditor.Read(imgOff).Enabled && SocdEditor.Read(imgOn).Pairs.Count == 1);
        fails += Compare("slot 2 SOCD: rebuild GG's SOCD-on save from its image (slot 2)", Transaction.Build(imgOn, 2), txOn, strict: true);
        fails += Compare("slot 2 SOCD: rebuild GG's SOCD-off save from its image (slot 2)", Transaction.Build(imgOff, 2), txOff, strict: true);
        var (n2, n3) = AsRead(imgOff);
        MacroPlan turnOn = SocdPlanner.Plan(n2, n3, on, slot: 2);
        fails += Check("slot 2 SOCD: turning it on from GG's 'off' image plans GG's exact 'on' image (flag + CRC) with live command 1a 01 and 164 steps",
            turnOn.Ok && turnOn.Slot == 2 && SameCovered(turnOn.After, imgOn) && turnOn.PreWriteCommand is { } c1 && c1.AsSpan().SequenceEqual(liveOn.Data) && turnOn.WriteSteps == 164);
        var (o2, o3) = AsRead(imgOn);
        MacroPlan turnOff = SocdPlanner.Plan(o2, o3, off, slot: 2);
        fails += Check("slot 2 SOCD: turning it off from GG's 'on' image plans GG's exact 'off' image with live command 1a 00",
            turnOff.Ok && SameCovered(turnOff.After, imgOff) && turnOff.PreWriteCommand is { } c0 && c0.AsSpan().SequenceEqual(liveOff.Data));
        var withSocd = ProfileWriter.ImageFromDump(r2, r3);
        SocdEditor.Apply(withSocd, on); withSocd.UpdateCrc();
        var (s2, s3) = AsRead(withSocd);
        MacroPlan tweak = SocdPlanner.Plan(s2, s3, new SocdConfig(true, new[] { new SocdPair(0x04, 0x07, SocdBehavior.Key1Priority) }), slot: 2);
        fails += Check("slot 2: changing a pair's behaviour while SOCD stays on is planned, with no live command", tweak.Ok && tweak.PreWriteCommand is null && tweak.Slot == 2);
        MacroPlan flip1 = SocdPlanner.Plan(r2, r3, on, slot: 1);
        fails += Check("slot 1: turning SOCD on still works and still carries GG's live command", flip1.Ok && flip1.PreWriteCommand is { } c && c[0] == 0x1A && c[1] == 1);

        // Pre-write snapshots and restores of other slots.
        string tmp = Path.Combine(Path.GetTempPath(), "apex-slot-snap-" + Guid.NewGuid().ToString("N"));
        try
        {
            BackupStore.SaveSlotSnapshot(tmp, 2, r2, r3);
            var info = BackupStore.List(Path.GetDirectoryName(tmp)!).FirstOrDefault(b => b.Path == tmp);
            fails += Check("slot 2: a pre-write snapshot of slot 2 is saved as slot2-*.bin and listed as holding slot 2 only",
                BackupStore.TryLoadSlot(tmp, 2, out var l2, out var l3) && l2.SequenceEqual(r2) && l3.SequenceEqual(r3) && !BackupStore.TryLoadSlot1(tmp, out _, out _)
                && info is not null && info.HasSlot(2) && !info.HasSlot(1));
        }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } }
        return fails;
    }

    // Actuation stored in a profile (GG's saves of Config 2: profile2-actuation-multikey sets eight keys to eight values).
    private static int CheckActuationImage(string dir)
    {
        int fails = 0;
        var multi = Transaction.Load(Path.Combine(dir, "tx-profile2-actuation-multikey.txt"));
        var lives = multi.Where(s => s.IsFeature && s.Data[0] == 0x31).ToList();
        var after = ProfileImage.FromSteps(multi.Where(s => !(s.IsFeature && s.Data[0] == 0x31)).ToList());
        var before = ProfileImage.FromSteps(Transaction.Load(Path.Combine(dir, "tx-profile2-socd-off.txt")));      // the same profile, saved just before
        fails += Check($"actuation: the capture has {lives.Count} live frames (one per change) and both images' checksums match", lives.Count == 7 && after.ComputedCrc == after.StoredCrc && before.ComputedCrc == before.StoredCrc);

        // The final live table: key -> raw value.
        byte[] f = lives[^1].Data;
        var raw = new Dictionary<byte, ushort>();
        for (int i = 0; i < Actuation.KeyCodes.Count; i++) raw[f[2 + i * 3]] = (ushort)(f[3 + i * 3] | (f[4 + i * 3] << 8));
        ushort global = raw.Values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
        var perKey = raw.Where(kv => kv.Value != global && ActuationImage.IsStorable(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

        fails += Check("actuation: the eight keys set in GG (Q W E A S V LShift Space) sit at image entries 15 16 17 29 30 47 42 60, the order the keymap table gives",
            new (byte, int)[] { (0x14, 15), (0x1A, 16), (0x08, 17), (0x04, 29), (0x16, 30), (0x19, 47), (0xE1, 42), (0x2C, 60) }.All(p => ActuationImage.IndexOf(p.Item1) == p.Item2)
            && new (byte, int)[] { (0x14, 15), (0x1A, 16), (0x08, 17), (0x04, 29), (0x16, 30), (0x19, 47), (0xE1, 42), (0x2C, 60) }.All(p => ActuationImage.RawAt(after, p.Item1) == raw[p.Item1] && raw[p.Item1] != global));

        var storable = Actuation.KeyCodes.Where(ActuationImage.IsStorable).ToList();
        int matching = storable.Count(k => ActuationImage.RawAt(after, k) == raw[k]);
        fails += Check($"actuation: every storable key's stored value equals the live table GG sent ({matching} of {storable.Count}); only the left Windows key (E3) is left out",
            matching == storable.Count && storable.Count == 67 && !ActuationImage.IsStorable(0xE3));
        Console.WriteLine($"  [INFO] the left Windows key: live table says raw {raw[0xE3]}, GG's saved entry 57 holds 0x{ActuationImage.RawAt(after, 0xE3):X4} (also in every earlier save)");

        // Our editor applied to the image GG saved before reproduces the actuation table GG saved.
        var applied = before.Clone();
        ActuationImage.Apply(applied, global, perKey);
        bool arraysEqual = applied.Region02.AsSpan(ActuationImage.LowOffset, ActuationImage.EntryCount).SequenceEqual(after.Region02.AsSpan(ActuationImage.LowOffset, ActuationImage.EntryCount))
            && applied.Region02.AsSpan(ActuationImage.HighOffset, ActuationImage.EntryCount).SequenceEqual(after.Region02.AsSpan(ActuationImage.HighOffset, ActuationImage.EntryCount));
        fails += Check("actuation: GG's previous image + the table GG sent, applied by our editor, gives GG's saved actuation arrays byte-for-byte", arraysEqual);
        int otherDiffs = Enumerable.Range(0, ProfileImage.CrcOffset).Count(i => (i < ActuationImage.LowOffset || i >= ActuationImage.HighOffset + ActuationImage.EntryCount) && applied.Region02[i] != after.Region02[i]);
        Console.WriteLine($"  [INFO] outside the actuation table, GG's two saves differ in {otherDiffs} byte(s) before the CRC");
        if (otherDiffs == 0)
            fails += Check("actuation: ... and the whole image, CRC included, equals GG's save", SameCovered(applied, after));

        var (rg, rp) = ActuationImage.Read(after);
        fails += Check("actuation: reading GG's saved image gives back the global value and exactly the seven per-key values", rg == global && rp.Count == perKey.Count && perKey.All(kv => rp.TryGetValue(kv.Key, out ushort v) && v == kv.Value));

        // The planner: mm in, plan out (GG's live frame first, then the slot 2 save).
        double Mm(ushort r) => Actuation.KnownGoodValues.First(v => v.Raw == r).Mm;
        var perKeyMm = perKey.ToDictionary(kv => kv.Key, kv => Mm(kv.Value));
        var (b2, b3) = AsRead(before);
        MacroPlan plan = ActuationPlanner.Plan(b2, b3, Mm(global), perKeyMm, slot: 2);
        fails += Check("actuation planner: plans GG's table for slot 2 (only the actuation table + CRC change, 164 steps, live frame 31 47 first)",
            plan.Ok && plan.Slot == 2 && plan.WriteSteps == 164 && plan.PreWriteFeature is { Length: 642 } pf && pf[0] == 0x31 && pf[1] == 0x47 && plan.BackupTag == "pre-actuation"
            && plan.Diff!.Entries.All(e => e.Area is "actuation table" or "CRC") && plan.After.Region02.AsSpan(ActuationImage.LowOffset, ActuationImage.EntryCount).SequenceEqual(after.Region02.AsSpan(ActuationImage.LowOffset, ActuationImage.EntryCount)));
        bool liveMatches = plan.PreWriteFeature is { } pfr && storable.Where(k => !Actuation.SentinelKeys.Contains(k)).All(k =>
        {
            int at = 2 + Actuation.KeyCodes.ToList().IndexOf(k) * 3;
            return pfr[at] == k && (ushort)(pfr[at + 1] | (pfr[at + 2] << 8)) == raw[k];
        });
        fails += Check("actuation planner: the live frame it sends carries GG's value for every key (the ISO keys carry our fixed sentinel)", liveMatches);

        var withWin = new Dictionary<byte, double>(perKeyMm) { [0xE3] = 2.1 };
        MacroPlan win = ActuationPlanner.Plan(b2, b3, Mm(global), withWin, slot: 2);
        fails += Check("actuation planner: a value for the left Windows key is sent live only, and the headline says so (the image is the same)",
            win.Ok && win.Headline.Contains("can only be sent live") && win.After.Region02.AsSpan(0, ProfileWriter.SafeEnd).SequenceEqual(plan.After.Region02.AsSpan(0, ProfileWriter.SafeEnd)));
        fails += Check("actuation planner: writing what the profile already holds is refused", !ActuationPlanner.Plan(AsRead(after).R2, AsRead(after).R3, Mm(global), perKeyMm, slot: 2).Ok);
        fails += Check("actuation planner: a value GG was never seen sending (3.2 mm) is refused", !ActuationPlanner.Plan(b2, b3, 3.2, new Dictionary<byte, double>(), slot: 2).Ok);
        return fails;
    }

    // The opt-in export: personal content is stripped unless asked for, and the zip holds exactly what it says it holds.
    private static int CheckCompatibilityExport(string dir)
    {
        int fails = 0;
        string? backup = BackupStore.NewestWithSlot1(Commands.BackupsRoot);
        if (backup is null || !BackupStore.TryLoadSlot1(backup, out byte[] r2, out byte[] r3) || r2.Length != ProfileWriter.ReadLen)
        {
            Console.WriteLine("  [INFO] no slot-1 backup found - skipped the export checks");
            return 0;
        }

        // A dump with a profile name and a macro in it, as a personal profile would be.
        var img = ProfileWriter.ImageFromDump(r2, r3);
        MacroEditor.AddChordMacro(img, 0x44, new[] { Q }, 100);
        var (p2, p3) = AsRead(img);
        byte[] clean = CompatibilityExport.SanitizeRegion02(p2);

        bool nameBlank = clean.AsSpan(2, 16).ToArray().All(b => b == 0);
        bool macroAreaEmpty = clean.AsSpan(MacroEditor.MacroAreaBase, MacroEditor.MacroAreaLimit - MacroEditor.MacroAreaBase).ToArray().All(b => b == 0);
        bool noMacroEntries = KeymapLayout.DefaultOffsets.Values.All(off => clean[off] != 0x71);
        fails += Check("export: sanitising blanks the profile name, empties the macro area and turns macro-bound keys back into plain keys", nameBlank && macroAreaEmpty && noMacroEntries && clean[95] == 0x51 && clean[96] == 0x44);
        Console.WriteLine($"  [INFO] the original profile name was '{System.Text.Encoding.ASCII.GetString(p2, 2, 16).Split('\0')[0]}' and it had 1 macro; the sanitised copy has neither");

        var changed = Enumerable.Range(0, p2.Length).Where(i => p2[i] != clean[i]).ToList();
        bool onlyExpected = changed.All(i => i is >= 2 and < 18 || (i >= 45 && i < 45 + 5 * 112) || (i >= MacroEditor.MacroAreaBase && i < MacroEditor.MacroAreaLimit));
        fails += Check($"export: sanitising changes nothing else ({changed.Count} bytes differ, all in the name, the macro-bound key entry or the macro area)", onlyExpected);

        ImageChecks after = Compatibility.Analyze(clean, p3);
        fails += Check("export: the sanitised copy no longer has a valid checksum (as documented) but keeps the whole key table", !after.CrcOk && after.KeymapRecognised + after.KeymapUnused == after.KeymapTotal);

        // The zip.
        var device = new SteelSeriesDevice(0x1038, 0x1610, 0x0416, "Test keyboard", "SteelSeries", new[] { new HidInterfaceInfo("mi_01", 65, 65, 643, new uint[] { 0xFFC00001 }) });
        var read = new ReadCheckResult(true, "ok", new[] { "4.16.8" }, after, p2, p3);
        string folder = Path.Combine(Path.GetTempPath(), "apex-export-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            string report = Compatibility.BuildReport(new[] { device }, new Dictionary<int, ReadCheckResult> { [0x1610] = read });
            string layoutZip = CompatibilityExport.WriteZip(folder, new[] { (device, read) }, report, includePersonalData: false, new DateTime(2026, 9, 20, 12, 0, 0));
            string fullZip = CompatibilityExport.WriteZip(folder, new[] { (device, read) }, report, includePersonalData: true, new DateTime(2026, 9, 20, 12, 0, 1));

            byte[] Entry(string zipPath, string name)
            {
                using var z = System.IO.Compression.ZipFile.OpenRead(zipPath);
                var e = z.GetEntry(name);
                if (e is null) return Array.Empty<byte>();
                using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
            }
            string[] Names(string zipPath) { using var z = System.IO.Compression.ZipFile.OpenRead(zipPath); return z.Entries.Select(e => e.FullName).OrderBy(n => n).ToArray(); }

            fails += Check("export: the default zip holds the contents note, the report and the SANITISED region 02 - no region 03",
                Names(layoutZip).SequenceEqual(new[] { "1610/slot1-region02.bin", "EXPORT-CONTENTS.txt", "report.txt" }) && Entry(layoutZip, "1610/slot1-region02.bin").SequenceEqual(clean));
            fails += Check("export: asking for everything adds region 03 and the RAW region 02 (name and macros included)",
                Names(fullZip).SequenceEqual(new[] { "1610/slot1-region02.bin", "1610/slot1-region03.bin", "EXPORT-CONTENTS.txt", "report.txt" })
                && Entry(fullZip, "1610/slot1-region02.bin").SequenceEqual(p2) && Entry(fullZip, "1610/slot1-region03.bin").SequenceEqual(p3));
            fails += Check("export: the contents note says what is (and is not) inside for each mode",
                System.Text.Encoding.UTF8.GetString(Entry(layoutZip, "EXPORT-CONTENTS.txt")).Contains("macro contents removed") && System.Text.Encoding.UTF8.GetString(Entry(fullZip, "EXPORT-CONTENTS.txt")).Contains("INCLUDING its name and any macros")
                && System.Text.Encoding.UTF8.GetString(Entry(layoutZip, "EXPORT-CONTENTS.txt")).Contains("NOT been sent anywhere"));
            fails += Check("export: the report inside contains no serial number or device path", System.Text.Encoding.UTF8.GetString(Entry(layoutZip, "report.txt")) is var rep && !rep.Contains("hid#") && !rep.ToLowerInvariant().Contains("serial"));

            bool refused = false;
            try { CompatibilityExport.WriteZip(folder, new[] { (device, new ReadCheckResult(false, "no", Array.Empty<string>(), null)) }, report, false, DateTime.Now); }
            catch (IOException) { refused = true; }
            fails += Check("export: with nothing read there is nothing to export (refused)", refused);
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        return fails;
    }

    // GG's captured save of a macro with waits between keys (docs/reference/tx-macro-seq-q300w.txt): our encoder must
    // produce the very same record bytes, and the same image when the edit is applied on top of the keyboard's state.
    private static int CheckSequenceCapture(string dir)
    {
        string file = Path.Combine(dir, "tx-macro-seq-q300w.txt");
        if (!File.Exists(file)) { Console.WriteLine("  [INFO] no tx-macro-seq-q300w.txt - skipped"); return 0; }
        int fails = 0;
        var tx = Transaction.Load(file);
        ProfileImage gg = ProfileImage.FromSteps(tx);
        fails += Check($"GG's sequence capture: CRC computed {gg.ComputedCrc:x8} == stored {gg.StoredCrc:x8}", gg.ComputedCrc == gg.StoredCrc);
        fails += Compare("Rebuild the sequence-capture transaction from its own image", Transaction.Build(gg), tx, strict: false);

        var recs = MacroEditor.ReadMacros(gg, out _);
        Console.WriteLine("  [INFO] macros in GG's saved image: " + string.Join(" | ", MacroEditor.DescribeMacros(gg)));

        var timeline = new[] { MacroEvent.Down(Q), MacroEvent.Wait(300), MacroEvent.Up(Q), MacroEvent.Wait(300), MacroEvent.Down(W), MacroEvent.Wait(300), MacroEvent.Up(W) };
        byte[] ours = MacroEditor.BuildEventRecord(timeline);
        MacroRecord? f11 = recs.FirstOrDefault(r => r.EntryOffset == 95);
        Console.WriteLine($"  [INFO] GG's F11 record: {(f11 is null ? "(none)" : Convert.ToHexString(f11.Bytes))}");
        Console.WriteLine($"  [INFO] our encoding    : {Convert.ToHexString(ours)}");
        fails += Check("GG's F11 record == our encoding of Q down, 300, Q up, 300, W down, 300, W up (byte-for-byte)", f11 is not null && f11.Bytes.SequenceEqual(ours));
        fails += Check("  and the app reads GG's record back as the same timeline", f11 is not null && MacroEditor.TryParseEvents(f11.Bytes, out var back) && back.SequenceEqual(timeline));

        // Remove F11 from GG's image and add it back with our editor: must give GG's image exactly (placement, pointer, CRC).
        var rt = gg.Clone();
        try
        {
            MacroEditor.RemoveMacro(rt, 0x44);
            MacroEditor.AddEventMacro(rt, 0x44, timeline);
            fails += SameProfile("GG's image minus F11 then plus our F11 == GG's image (record placement, pointer and CRC included)", rt, gg);
        }
        catch (InvalidOperationException ex) { fails += Check("remove/re-add F11 on GG's image: " + ex.Message, false); }

        // The keyboard's state before the capture (newest hardware dump) + our F11 edit, against GG's saved image.
        string? backup = BackupStore.NewestWithSlot1(Commands.BackupsRoot);
        if (backup is not null && BackupStore.TryLoadSlot1(backup, out byte[] r2, out byte[] r3))
        {
            var plan = MacroPlanner.Plan(r2, r3, MacroEditRequest.ForEvents(0x44, timeline));
            if (plan.Ok)
            {
                var diffs = Enumerable.Range(0, ProfileWriter.SafeEnd).Where(i => plan.After.Region02[i] != gg.Region02[i]).ToList();
                var runs = new List<string>();
                for (int i = 0; i < diffs.Count;)
                {
                    int j = i;
                    while (j + 1 < diffs.Count && diffs[j + 1] - diffs[j] <= 3) j++;
                    runs.Add(diffs[i] == diffs[j] ? $"{diffs[i]}" : $"{diffs[i]}-{diffs[j]}");
                    i = j + 1;
                }
                Console.WriteLine($"  [INFO] {Path.GetFileName(backup)} + our F11 edit vs GG's saved image: {diffs.Count} differing byte(s) in the CRC-covered part, in runs: {string.Join(", ", runs)}; region 03 identical: {plan.After.Region03.AsSpan().SequenceEqual(gg.Region03)}");
                Console.WriteLine("  [INFO] macros in that keyboard state before the edit: " + string.Join(" | ", MacroEditor.DescribeMacros(plan.Before)));
            }
        }
        return fails;
    }

    // Multi-step macros ("Q, wait 300 ms, W"). NOTE: unlike everything above, GG's own write of a pause BETWEEN keys
    // has not been captured; these checks prove the encoder is self-consistent and reduces to the captured chord form
    // for one step, not that the keyboard runs the multi-step form as intended.
    private static int CheckSequences(ProfileImage imgB, byte[] b2, byte[] b3)
    {
        int fails = 0;
        var qw = new[] { new MacroStep(new[] { Q }, 50, 300), new MacroStep(new[] { W }, 50, 0) };
        string hex = Convert.ToHexString(MacroEditor.BuildSequenceRecord(qw));
        const string want = "77010700" + "0101" + "0F00" + "02140101" + "04003200" + "02140100" + "04002C01" + "021A0101" + "04003200" + "021A0100";
        fails += Check("sequence Q(50 ms) wait 300 ms W(50 ms) encodes as 7 events: down, hold, up, gap, down, hold, up", hex == want);
        fails += Check("a one-step sequence is byte-identical to the captured chord form (F9 = Q/172, F10 = W+Q/141)",
            MacroEditor.BuildSequenceRecord(new[] { new MacroStep(new[] { Q }, 0xAC, 0) }).SequenceEqual(MacroEditor.BuildRecord(new[] { Q }, 0xAC))
            && MacroEditor.ReadMacros(imgB, out _).All(r => MacroEditor.TryParseSequence(r.Bytes, out var s) && s.Count == 1 && MacroEditor.BuildSequenceRecord(s).SequenceEqual(r.Bytes)));

        fails += Check("parse: the two-step record reads back as the same two steps (gap on the last step is 0)",
            MacroEditor.TryParseSequence(MacroEditor.BuildSequenceRecord(qw), out var back) && back.Count == 2 && back[0].Keys.SequenceEqual(new[] { Q }) && back[0].HoldMs == 50 && back[0].GapAfterMs == 300 && back[1].Keys.SequenceEqual(new[] { W }) && back[1].GapAfterMs == 0);

        var mixed = new[] { new MacroStep(new[] { Q, W }, 20, 1), new MacroStep(new byte[] { 0x2C }, 5000, 5000), new MacroStep(new[] { W }, 1, 0) };
        fails += Check("parse: a three-step macro with a chord step round-trips exactly",
            MacroEditor.TryParseSequence(MacroEditor.BuildSequenceRecord(mixed), out var mixedBack) && mixedBack.Count == 3 && mixedBack[0].Keys.Length == 2
            && MacroEditor.BuildSequenceRecord(mixedBack).SequenceEqual(MacroEditor.BuildSequenceRecord(mixed)));

        byte[] odd = MacroEditor.BuildSequenceRecord(qw);
        byte[] noGap = odd.Take(8 + 4 * 3).Concat(odd.Skip(8 + 4 * 4)).ToArray(); noGap[2] = 6;      // remove the gap event
        fails += Check("parse: a recording with no wait between steps is not treated as ours (stays 'custom')", !MacroEditor.TryParseSequence(noGap, out _));

        var (before, plan) = (imgB, MacroPlanner.Plan(b2, b3, MacroEditRequest.ForSteps(0x44, qw)));
        var direct = before.Clone();
        MacroEditor.AddSequenceMacro(direct, 0x44, qw);
        fails += Check("planner: bind F11 to Q, wait 300 ms, W equals MacroEditor.AddSequenceMacro; diff limited to expected areas; CRC valid",
            plan.Ok && SameCovered(plan.After, direct) && plan.Diff!.Ok && plan.After.StoredCrc == plan.After.ComputedCrc);
        fails += Check("  headline: " + plan.Headline, plan.Headline == "Bind F11: press Q (hold 50 ms), wait 300 ms, press W (hold 50 ms)");
        fails += Check("  the macro list after shows the pause", plan.MacrosAfter.Any(l => l.StartsWith("F11") && l.EndsWith("Q down, wait 50 ms, Q up, wait 300 ms, W down, wait 50 ms, W up")));

        var replaceSeq = MacroPlanner.Plan(b2, b3, MacroEditRequest.ForSteps(F9, qw));
        var directRep = before.Clone();
        MacroEditor.ReplaceSequenceMacro(directRep, F9, qw);
        fails += Check("planner: replacing F9's one-step macro with the two-step one equals ReplaceSequenceMacro", replaceSeq.Ok && SameCovered(replaceSeq.After, directRep) && replaceSeq.Headline.StartsWith("Replace the macro on F9"));

        fails += Check("planner: a 0 ms wait between steps is refused",
            !MacroPlanner.Plan(b2, b3, MacroEditRequest.ForSteps(0x44, new[] { new MacroStep(new[] { Q }, 50, 0), new MacroStep(new[] { W }, 50, 0) })).Ok);
        fails += Check("planner: a 5001 ms wait is refused",
            !MacroPlanner.Plan(b2, b3, MacroEditRequest.ForSteps(0x44, new[] { new MacroStep(new[] { Q }, 50, 5001), new MacroStep(new[] { W }, 50, 0) })).Ok);
        fails += Check("planner: 16 steps (63 events, over the 60-event limit) is refused; 12 steps (47 events) is accepted",
            !MacroPlanner.Plan(b2, b3, MacroEditRequest.ForSteps(0x44, Enumerable.Repeat(new MacroStep(new[] { Q }, 10, 10), 16).ToArray())).Ok
            && MacroPlanner.Plan(b2, b3, MacroEditRequest.ForSteps(0x44, Enumerable.Repeat(new MacroStep(new[] { Q }, 10, 10), 12).ToArray())).Ok);
        fails += CheckEvents(imgB, b2, b3);
        return fails;
    }

    // The timeline model (what GG's macro editor shows): key down / key up / wait blocks in any valid order.
    private static int CheckEvents(ProfileImage imgB, byte[] b2, byte[] b3)
    {
        int fails = 0;
        MacroEvent D(byte k) => MacroEvent.Down(k);
        MacroEvent U(byte k) => MacroEvent.Up(k);
        MacroEvent Wt(ushort ms) => MacroEvent.Wait(ms);

        // The macro from GG's own editor screenshot: Q down, 300 ms, Q up, 300 ms, W down, 300 ms, W up.
        var gg = new[] { D(Q), Wt(300), U(Q), Wt(300), D(W), Wt(300), U(W) };
        fails += Check("events: GG-editor macro (Q down, 300, Q up, 300, W down, 300, W up) encodes exactly like the two-step form",
            MacroEditor.BuildEventRecord(gg).SequenceEqual(MacroEditor.BuildSequenceRecord(new[] { new MacroStep(new[] { Q }, 300, 300), new MacroStep(new[] { W }, 300, 0) })));
        fails += Check("  its friendly reading: " + MacroEditor.DescribeEvents(gg), MacroEditor.DescribeEvents(gg) == "press Q (hold 300 ms), wait 300 ms, press W (hold 300 ms)");

        var overlap = new[] { D(Q), D(W), Wt(100), U(Q), Wt(50), U(W) };     // W is still down when Q comes up
        fails += Check("events: an interleaved timeline is valid, round-trips, and reads as raw events",
            MacroEditor.ValidateEvents(overlap) is null && MacroEditor.TryParseEvents(MacroEditor.BuildEventRecord(overlap), out var ov) && ov.SequenceEqual(overlap)
            && MacroEditor.DescribeEvents(overlap) == "Q down, W down, wait 100 ms, Q up, wait 50 ms, W up");

        fails += Check("events: both captured GG macros (F9, F10) parse as timelines and rebuild byte-for-byte",
            MacroEditor.ReadMacros(imgB, out _).All(r => MacroEditor.TryParseEvents(r.Bytes, out var e) && MacroEditor.BuildEventRecord(e).SequenceEqual(r.Bytes)));

        var bad = new (string Why, MacroEvent[] Events)[]
        {
            ("a key pressed and never released", new[] { D(Q), Wt(100) }),
            ("a key released that was never pressed", new[] { D(W), U(Q), U(W) }),
            ("a key pressed twice without a release", new[] { D(Q), D(Q), U(Q) }),
            ("a 0 ms wait", new[] { D(Q), Wt(0), U(Q) }),
            ("a 5001 ms wait", new[] { D(Q), Wt(5001), U(Q) }),
            ("no events at all", Array.Empty<MacroEvent>()),
            ("waits only, no key press", new[] { Wt(100) }),
            ("more than 60 events", Enumerable.Range(0, 31).SelectMany(_ => new[] { D(Q), U(Q) }).ToArray()),
        };
        foreach (var (why, events) in bad)
            fails += Check($"events: refused - {why}", MacroEditor.ValidateEvents(events) is not null && !MacroPlanner.Plan(b2, b3, MacroEditRequest.ForEvents(0x44, events)).Ok);

        var noWait = new[] { D(Q), U(Q) };
        fails += Check("events: a bare tap with no wait (Q down, Q up) is valid", MacroEditor.ValidateEvents(noWait) is null);

        MacroPlan plan = MacroPlanner.Plan(b2, b3, MacroEditRequest.ForEvents(0x44, overlap));
        var direct = imgB.Clone();
        MacroEditor.AddEventMacro(direct, 0x44, overlap);
        fails += Check("planner: an interleaved timeline on F11 equals MacroEditor.AddEventMacro, CRC valid, only expected areas changed",
            plan.Ok && SameCovered(plan.After, direct) && plan.Diff!.Ok && plan.After.StoredCrc == plan.After.ComputedCrc);
        return fails;
    }

    private static int Check(string label, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }

    // strict: every step in order. non-strict: the lone 74 00 01 00 command is
    // allowed to sit elsewhere in the tail (GG's baseline capture put it after
    // the two 0x90 commands; both macro captures put it before them).
    private static int Compare(string label, List<Step> got, List<Step> want, bool strict)
    {
        List<Step> g = got, w = want;
        if (!strict)
        {
            g = got.Where(s => !IsCmd74(s)).ToList();
            w = want.Where(s => !IsCmd74(s)).ToList();
        }

        string? problem = null;
        if (g.Count != w.Count) problem = $"step count {g.Count} != {w.Count}";
        else
        {
            for (int i = 0; i < g.Count && problem is null; i++)
            {
                if (g[i].IsFeature != w[i].IsFeature) { problem = $"step {i}: kind differs"; break; }
                int at = FirstDiff(g[i].Data, w[i].Data);
                if (at >= 0) problem = $"step {i} ({w[i].Describe()}): first differing byte at {at} (got {g[i].Data[Math.Min(at, g[i].Data.Length - 1)]:x2}, want {w[i].Data[Math.Min(at, w[i].Data.Length - 1)]:x2})";
            }
        }

        string note = strict ? "" : " (74-command position ignored)";
        Console.WriteLine($"  [{(problem is null ? "PASS" : "FAIL")}] {label}{note}: {g.Count} steps{(problem is null ? "" : " - " + problem)}");
        return problem is null ? 0 : 1;
    }

    private static bool IsCmd74(Step s) => !s.IsFeature && s.Data[0] == 0x74;

    private static int FirstDiff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return Math.Min(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return i;
        return -1;
    }
}

