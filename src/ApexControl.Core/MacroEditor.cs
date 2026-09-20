namespace ApexControl.Core;

// Edits the in-memory profile image the way GG does when a key is bound to a "key press"
// macro (layout: docs/PROTOCOL_NOTES.md).
//   keymap:  5-byte entries from payload offset 45. A normal key is 51 <HID> 00 00 00; a
//            macro key is 71 00 00 <ptr> 00 where ptr is the record's distance from
//            MacroAreaBase in 4-byte units.
//   macros:  contiguous records from MacroAreaBase: 77 01 <n> 00, settings 01 01 0f 00,
//            then n events of 4 bytes: 02 <HID> 01 01 = key down, 02 <HID> 01 00 = key up,
//            04 00 <lo> <hi> = delay ms.
// Adding was verified against GG's own captures. Removal/replacement (which re-packs the
// area and fixes pointers) is verified offline as the exact inverse of adding, but GG's own
// behavior when deleting a macro has not been captured yet.
public sealed record MacroRecord(int EntryOffset, byte[] Bytes)
{
    public int Length => Bytes.Length;
}

// One step of a key-press macro: press all `Keys` together, hold `HoldMs`, release them in the same order, then
// (unless it is the last step) wait `GapAfterMs` before the next step. GapAfterMs is ignored on the last step.
public sealed record MacroStep(byte[] Keys, ushort HoldMs, ushort GapAfterMs);

// One block on GG's macro timeline: a key going down, a key coming up, or a wait. `Key` is used by Down/Up, `Ms` by Wait.
public enum MacroEventKind { Down, Up, Wait }

public sealed record MacroEvent(MacroEventKind Kind, byte Key, ushort Ms)
{
    public static MacroEvent Down(byte key) => new(MacroEventKind.Down, key, 0);
    public static MacroEvent Up(byte key) => new(MacroEventKind.Up, key, 0);
    public static MacroEvent Wait(ushort ms) => new(MacroEventKind.Wait, 0, ms);
}

public static class MacroEditor
{
    public const int KeymapStart = 45;
    public const int KeymapEnd = 700;          // scan window for entries
    public const int MacroAreaBase = 2938;
    public const int MacroAreaLimit = MacroAreaBase + 512;   // conservative: real capacity not yet known

    // ---- lookup ---------------------------------------------------------------------------

    public static int EntryOffsetFor(byte hid)
    {
        if (!KeymapLayout.DefaultOffsets.TryGetValue(hid, out int off))
            throw new InvalidOperationException($"HID 0x{hid:X2} is not a key in the keymap table.");
        return off;
    }

    public static bool IsBound(ProfileImage img, byte hid)
    {
        int e = EntryOffsetFor(hid);
        byte[] r = img.Region02;
        if (r[e] == 0x71 && r[e + 1] == 0 && r[e + 2] == 0 && r[e + 4] == 0) return true;
        if (r[e] == 0x51 && r[e + 1] == hid && r[e + 2] == 0 && r[e + 3] == 0 && r[e + 4] == 0) return false;
        throw new InvalidOperationException($"Keymap entry for {KeyNames.Name(hid)} is in an unexpected state ({Convert.ToHexString(r, e, 5)}); refusing to edit it.");
    }

    // ---- parsing --------------------------------------------------------------------------

    // All macro records, in area order, each tied to the keymap entry that points at it.
    // Throws if the area is not exactly what we understand (orphans, shared records, ...).
    public static List<MacroRecord> ReadMacros(ProfileImage img, out int usedEnd)
    {
        byte[] r = img.Region02;
        var walked = new List<(int Start, int Len)>();
        int pos = MacroAreaBase;
        while (r[pos] == 0x77)
        {
            int n = r[pos + 2];
            int len = 4 * (2 + n);
            if (r[pos + 1] != 0x01 || n < 1 || n > 60 || pos + len > MacroAreaLimit)
                throw new InvalidOperationException($"Macro record at {pos} looks malformed ({Convert.ToHexString(r, pos, 8)}).");
            walked.Add((pos, len));
            pos += len;
        }
        usedEnd = pos;

        var result = new List<MacroRecord>();
        var referenced = new HashSet<int>();
        for (int p = KeymapStart; p + 5 <= KeymapEnd; p += 5)
        {
            if (r[p] != 0x71) continue;
            if (r[p + 1] != 0 || r[p + 2] != 0 || r[p + 4] != 0 || KeymapLayout.KeyAt(p) is null)
                throw new InvalidOperationException($"Keymap entry at {p} ({Convert.ToHexString(r, p, 5)}) is a macro entry I don't understand.");
            int start = MacroAreaBase + 4 * r[p + 3];
            var rec = walked.FirstOrDefault(w => w.Start == start);
            if (rec.Len == 0) throw new InvalidOperationException($"Keymap entry at {p} points to {start}, which is not the start of a macro record.");
            if (!referenced.Add(start)) throw new InvalidOperationException($"Two keymap entries share the macro record at {start}.");
            result.Add(new MacroRecord(p, r.AsSpan(start, rec.Len).ToArray()));
        }
        if (referenced.Count != walked.Count)
            throw new InvalidOperationException("The macro area has records that no keymap entry points to; refusing to edit it.");

        return result.OrderBy(m => PtrOf(img, m.EntryOffset)).ToList();
    }

    private static int PtrOf(ProfileImage img, int entryOffset) => img.Region02[entryOffset + 3];

    // ---- editing --------------------------------------------------------------------------

    public const int MaxEvents = 60;     // ReadMacros' sanity limit on a record's event count
    public const int MaxSteps = 16;
    public const int MaxWaitMs = 5000;   // the longest wait this app writes (GG's own captures were 141-172 ms)

    // A single "press these keys together, hold, release" macro (the form GG's captures showed).
    public static byte[] BuildRecord(byte[] chord, ushort holdMs) => BuildSequenceRecord(new[] { new MacroStep(chord, holdMs, 0) });

    // The timeline for a list of steps: per step, key-downs, a wait (the hold), key-ups; between steps a wait (the gap).
    // With one step this is exactly the captured chord layout.
    public static List<MacroEvent> EventsFromSteps(IReadOnlyList<MacroStep> steps)
    {
        if (steps.Count is < 1 or > MaxSteps) throw new ArgumentException($"A macro needs 1-{MaxSteps} steps.");
        var events = new List<MacroEvent>();
        for (int i = 0; i < steps.Count; i++)
        {
            MacroStep s = steps[i];
            if (s.Keys.Length is < 1 or > 8) throw new ArgumentException("Each step needs 1-8 keys.");
            if (s.Keys.Distinct().Count() != s.Keys.Length) throw new ArgumentException("A key appears twice in one step.");
            if (s.HoldMs < 1) throw new ArgumentException("Hold time must be at least 1 ms.");
            events.AddRange(s.Keys.Select(MacroEvent.Down));
            events.Add(MacroEvent.Wait(s.HoldMs));
            events.AddRange(s.Keys.Select(MacroEvent.Up));
            if (i < steps.Count - 1)
            {
                if (s.GapAfterMs < 1) throw new ArgumentException("The wait between steps must be at least 1 ms.");
                events.Add(MacroEvent.Wait(s.GapAfterMs));
            }
        }
        return events;
    }

    public static byte[] BuildSequenceRecord(IReadOnlyList<MacroStep> steps) => BuildEventRecord(EventsFromSteps(steps));

    // Null if the timeline is one we are willing to write; otherwise what is wrong, in plain words. A key that is
    // pressed and never released would stay held down, so every key-down needs a later key-up of the same key.
    public static string? ValidateEvents(IReadOnlyList<MacroEvent> events)
    {
        if (events.Count < 1) return "A macro needs at least one event.";
        if (events.Count > MaxEvents) return $"That is too long for one macro ({events.Count} events; the limit is {MaxEvents}).";
        if (!events.Any(e => e.Kind == MacroEventKind.Down)) return "A macro needs at least one key press.";

        var held = new List<byte>();
        for (int i = 0; i < events.Count; i++)
        {
            MacroEvent e = events[i];
            switch (e.Kind)
            {
                case MacroEventKind.Down:
                    if (held.Contains(e.Key)) return $"Event {i + 1}: {KeyNames.Name(e.Key)} is pressed again before it was released.";
                    held.Add(e.Key);
                    break;
                case MacroEventKind.Up:
                    if (!held.Remove(e.Key)) return $"Event {i + 1}: {KeyNames.Name(e.Key)} is released but was not pressed.";
                    break;
                default:
                    if (e.Ms is < 1 or > MaxWaitMs) return $"Event {i + 1}: a wait must be a whole number from 1 to {MaxWaitMs} ms.";
                    break;
            }
        }
        if (held.Count > 0) return $"{string.Join(", ", held.Select(KeyNames.Name))} would be left held down: add a \"Key up\" for it.";
        return null;
    }

    // Header 77 01 <event count> 00, settings 01 01 0f 00, then one 4-byte unit per event:
    // 02 <key> 01 01 = down, 02 <key> 01 00 = up, 04 00 <lo> <hi> = wait.
    public static byte[] BuildEventRecord(IReadOnlyList<MacroEvent> events)
    {
        string? bad = ValidateEvents(events);
        if (bad is not null) throw new ArgumentException(bad);

        var rec = new List<byte> { 0x77, 0x01, (byte)events.Count, 0x00, 0x01, 0x01, 0x0F, 0x00 };
        foreach (MacroEvent e in events)
        {
            if (e.Kind == MacroEventKind.Wait) rec.AddRange(new byte[] { 0x04, 0x00, (byte)(e.Ms & 0xFF), (byte)(e.Ms >> 8) });
            else rec.AddRange(new byte[] { 0x02, e.Key, 0x01, (byte)(e.Kind == MacroEventKind.Down ? 1 : 0) });
        }
        return rec.ToArray();
    }

    // True if `rec` is a record this app could have written (all events are downs, ups and waits, in a valid
    // order), so a UI can show it as a timeline and edit it. Anything else stays opaque.
    public static bool TryParseEvents(byte[] rec, out List<MacroEvent> events)
    {
        events = new List<MacroEvent>();
        try
        {
            if (rec.Length < 12 || rec[0] != 0x77 || rec[1] != 0x01 || rec[3] != 0x00 || rec.Length != 4 * (2 + rec[2])) return false;
            for (int pos = 8; pos < rec.Length; pos += 4)
            {
                if (rec[pos] == 0x02 && rec[pos + 2] == 0x01 && rec[pos + 3] <= 1)
                    events.Add(rec[pos + 3] == 1 ? MacroEvent.Down(rec[pos + 1]) : MacroEvent.Up(rec[pos + 1]));
                else if (rec[pos] == 0x04 && rec[pos + 1] == 0x00)
                    events.Add(MacroEvent.Wait((ushort)(rec[pos + 2] | rec[pos + 3] << 8)));
                else return false;
            }
            return rec.AsSpan().SequenceEqual(BuildEventRecord(events));
        }
        catch (ArgumentException) { return false; }
    }

    // A friendly one-line reading of a timeline: "press Q (hold 50 ms), wait 300 ms, press W (hold 50 ms)" when it has
    // that shape, otherwise the raw list ("Q down, wait 50 ms, ...").
    public static string DescribeEvents(IReadOnlyList<MacroEvent> events)
    {
        byte[] rec = BuildEventRecord(events);
        if (TryParseSequence(rec, out List<MacroStep> steps))
        {
            string Keys(MacroStep s) => string.Join("+", s.Keys.Select(KeyNames.Name));
            if (steps.Count == 1) return $"press {Keys(steps[0])}, hold {steps[0].HoldMs} ms";
            var parts = new List<string>();
            for (int i = 0; i < steps.Count; i++)
            {
                parts.Add($"press {Keys(steps[i])} (hold {steps[i].HoldMs} ms)");
                if (i < steps.Count - 1) parts.Add($"wait {steps[i].GapAfterMs} ms");
            }
            return string.Join(", ", parts);
        }
        return DescribeRecord(rec);
    }

    // True if `rec` is exactly what BuildSequenceRecord produces, so a UI can show and edit it as steps.
    // Anything else (a recording with a different shape) stays opaque.
    public static bool TryParseSequence(byte[] rec, out List<MacroStep> steps)
    {
        steps = new List<MacroStep>();
        try
        {
            if (rec.Length < 20 || rec[0] != 0x77 || rec.Length != 4 * (2 + rec[2])) return false;
            int pos = 8;
            while (pos < rec.Length)
            {
                var keys = new List<byte>();
                while (pos < rec.Length && IsKey(rec, pos, down: true)) { keys.Add(rec[pos + 1]); pos += 4; }
                if (keys.Count == 0 || !IsDelay(rec, pos)) return false;
                ushort hold = DelayAt(rec, pos); pos += 4;
                foreach (byte k in keys)
                {
                    if (!IsKey(rec, pos, down: false) || rec[pos + 1] != k) return false;
                    pos += 4;
                }
                ushort gap = 0;
                if (pos < rec.Length)
                {
                    if (!IsDelay(rec, pos)) return false;
                    gap = DelayAt(rec, pos); pos += 4;
                    if (pos >= rec.Length) return false;   // a wait after the last step is not something we write
                }
                steps.Add(new MacroStep(keys.ToArray(), hold, gap));
            }
            return rec.AsSpan().SequenceEqual(BuildSequenceRecord(steps));
        }
        catch (ArgumentException) { return false; }
    }

    private static bool IsKey(byte[] r, int p, bool down) => p + 4 <= r.Length && r[p] == 0x02 && r[p + 2] == 0x01 && r[p + 3] == (down ? 1 : 0);
    private static bool IsDelay(byte[] r, int p) => p + 4 <= r.Length && r[p] == 0x04 && r[p + 1] == 0x00;
    private static ushort DelayAt(byte[] r, int p) => (ushort)(r[p + 2] | r[p + 3] << 8);

    // True if `rec` is a single-step macro (press these keys together, hold, release).
    public static bool TryParseChord(byte[] rec, out byte[] chord, out ushort holdMs)
    {
        chord = Array.Empty<byte>();
        holdMs = 0;
        if (!TryParseSequence(rec, out List<MacroStep> steps) || steps.Count != 1) return false;
        chord = steps[0].Keys;
        holdMs = steps[0].HoldMs;
        return true;
    }

    // Binds `bindKey` to: press all `chord` keys together, hold `holdMs`, release in the same order.
    // The key must not already be macro-bound (use ReplaceChordMacro).
    public static void AddChordMacro(ProfileImage img, byte bindKey, byte[] chord, ushort holdMs) =>
        AddMacro(img, bindKey, BuildRecord(chord, holdMs));

    // Binds `bindKey` to a multi-step macro (see BuildSequenceRecord). The key must not already be macro-bound.
    public static void AddSequenceMacro(ProfileImage img, byte bindKey, IReadOnlyList<MacroStep> steps) =>
        AddMacro(img, bindKey, BuildSequenceRecord(steps));

    public static void ReplaceSequenceMacro(ProfileImage img, byte bindKey, IReadOnlyList<MacroStep> steps) =>
        ReplaceEventMacro(img, bindKey, EventsFromSteps(steps));

    // Binds `bindKey` to an arbitrary valid timeline (see ValidateEvents). The key must not already be macro-bound.
    public static void AddEventMacro(ProfileImage img, byte bindKey, IReadOnlyList<MacroEvent> events) =>
        AddMacro(img, bindKey, BuildEventRecord(events));

    public static void ReplaceEventMacro(ProfileImage img, byte bindKey, IReadOnlyList<MacroEvent> events)
    {
        byte[] rec = BuildEventRecord(events);       // validate before touching anything
        RemoveMacro(img, bindKey);
        AddMacro(img, bindKey, rec);
    }

    private static void AddMacro(ProfileImage img, byte bindKey, byte[] record)
    {
        if (IsBound(img, bindKey)) throw new InvalidOperationException($"{KeyNames.Name(bindKey)} already has a macro (use replace).");
        List<MacroRecord> macros = ReadMacros(img, out _);
        macros.Add(new MacroRecord(EntryOffsetFor(bindKey), record));
        Relayout(img, macros);
        img.UpdateCrc();
    }

    // Removes the macro on `bindKey`, restores the key's normal binding, re-packs the macro area.
    public static void RemoveMacro(ProfileImage img, byte bindKey)
    {
        if (!IsBound(img, bindKey)) throw new InvalidOperationException($"{KeyNames.Name(bindKey)} has no macro to remove.");
        int entry = EntryOffsetFor(bindKey);
        List<MacroRecord> macros = ReadMacros(img, out _);
        macros.RemoveAll(m => m.EntryOffset == entry);
        Relayout(img, macros, removedKey: bindKey);
        img.UpdateCrc();
    }

    // Replace = remove then append (the replaced macro moves to the end of the area).
    public static void ReplaceChordMacro(ProfileImage img, byte bindKey, byte[] chord, ushort holdMs)
    {
        RemoveMacro(img, bindKey);
        AddChordMacro(img, bindKey, chord, holdMs);
    }

    // Rewrites the macro area from `macros` (in the given order), fixes every macro entry's pointer,
    // and restores `removedKey`'s default entry. Only bytes inside [MacroAreaBase, old end) are cleared.
    private static void Relayout(ProfileImage img, List<MacroRecord> macros, byte? removedKey = null)
    {
        byte[] r = img.Region02;
        ReadMacrosEnd(img, out int oldEnd);

        int total = macros.Sum(m => m.Length);
        if (MacroAreaBase + total > MacroAreaLimit) throw new InvalidOperationException("Macros would exceed the safe macro-area limit.");
        int clearTo = Math.Max(oldEnd, MacroAreaBase + total);
        for (int i = oldEnd; i < clearTo; i++)          // growing into space past the old end: it must be empty
            if (r[i] != 0) throw new InvalidOperationException("Destination in the macro area is not empty.");

        Array.Clear(r, MacroAreaBase, clearTo - MacroAreaBase);
        int pos = MacroAreaBase;
        foreach (MacroRecord m in macros)
        {
            if ((pos - MacroAreaBase) % 4 != 0 || (pos - MacroAreaBase) / 4 > 255) throw new InvalidOperationException("Macro record position not addressable.");
            m.Bytes.CopyTo(r, pos);
            r[m.EntryOffset] = 0x71; r[m.EntryOffset + 1] = 0; r[m.EntryOffset + 2] = 0;
            r[m.EntryOffset + 3] = (byte)((pos - MacroAreaBase) / 4); r[m.EntryOffset + 4] = 0;
            pos += m.Length;
        }

        if (removedKey is byte k)
        {
            int e = EntryOffsetFor(k);
            r[e] = 0x51; r[e + 1] = k; r[e + 2] = 0; r[e + 3] = 0; r[e + 4] = 0;
        }
    }

    private static void ReadMacrosEnd(ProfileImage img, out int end)
    {
        byte[] r = img.Region02;
        int pos = MacroAreaBase;
        while (r[pos] == 0x77) pos += 4 * (2 + r[pos + 2]);
        end = pos;
    }

    // ---- display --------------------------------------------------------------------------

    public static string DescribeRecord(byte[] rec)
    {
        var parts = new List<string>();
        for (int o = 8; o + 4 <= rec.Length; o += 4)
        {
            if (rec[o] == 0x02) parts.Add($"{KeyNames.Name(rec[o + 1])} {(rec[o + 3] == 1 ? "down" : "up")}");
            else if (rec[o] == 0x04) parts.Add($"wait {rec[o + 2] + 256 * rec[o + 3]} ms");
            else parts.Add("?? " + Convert.ToHexString(rec, o, 4));
        }
        return string.Join(", ", parts);
    }

    public static List<string> DescribeMacros(ProfileImage img)
    {
        var lines = new List<string>();
        foreach (MacroRecord m in ReadMacros(img, out _))
        {
            byte? key = KeymapLayout.KeyAt(m.EntryOffset);
            lines.Add($"{(key is byte k ? KeyNames.Name(k) : "?"),-6} -> {DescribeRecord(m.Bytes)}");
        }
        return lines;
    }
}
