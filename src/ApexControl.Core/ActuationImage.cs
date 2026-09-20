namespace ApexControl.Core;

// The actuation table as GG stores it inside a profile's region 02 (what a GG "Save" writes; docs/PROTOCOL_NOTES.md):
// two arrays of 70 bytes, the LOW bytes of each key's raw value (the same u16 the live 31 47 frame carries) at 2375 + i and the
// HIGH bytes at 2445 + i. Entry i belongs to the i-th of the 68 adjustable keys when they are ordered by the position of their
// entry in the keymap table (KeymapLayout offsets), e.g. Q = 15, W = 16, E = 17, A = 29, S = 30, LShift = 42, V = 47, Space = 60.
// Confirmed against GG's saves of Config 2 (profile2-actuation-socd, profile2-actuation-multikey: eight keys with eight different
// values all land where this order says). Entries 68 and 69 are zero. Entry 57 (the left Windows key, E3) always holds 0x3A3F in
// GG's saves whatever the live table says for that key, so it is never touched here.
public static class ActuationImage
{
    public const int LowOffset = 2375, HighOffset = 2445, EntryCount = 70;
    public const byte UnstoredKey = 0xE3;

    private static readonly byte[] Order = Actuation.KeyCodes.OrderBy(k => KeymapLayout.DefaultOffsets[k]).ToArray();

    public static int IndexOf(byte hid) => Array.IndexOf(Order, hid);

    // Whether the profile can hold this key's value (the live frame can always carry it).
    public static bool IsStorable(byte hid) => hid != UnstoredKey && Actuation.KeyCodes.Contains(hid);

    public static ushort RawAt(ProfileImage img, byte hid)
    {
        int i = IndexOf(hid);
        return (ushort)(img.Region02[LowOffset + i] | (img.Region02[HighOffset + i] << 8));
    }

    // What the profile holds: the most common raw value among the storable keys is the "all keys" value, and every storable key
    // that differs from it is listed.
    public static (ushort Global, Dictionary<byte, ushort> PerKey) Read(ProfileImage img)
    {
        var values = Order.Where(IsStorable).ToDictionary(k => k, k => RawAt(img, k));
        ushort global = values.Values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
        return (global, values.Where(kv => kv.Value != global).ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    // Writes the table into the image (every storable key gets its own value or the global one) and refreshes the CRC.
    // Throws ArgumentException for a key that cannot be stored.
    public static void Apply(ProfileImage img, ushort globalRaw, IReadOnlyDictionary<byte, ushort> perKey)
    {
        foreach (byte k in perKey.Keys)
            if (!IsStorable(k)) throw new ArgumentException($"Key 0x{k:X2} cannot be stored in the profile's actuation table.");
        foreach (byte k in Order.Where(IsStorable))
        {
            ushort raw = perKey.TryGetValue(k, out ushort v) ? v : globalRaw;
            int i = IndexOf(k);
            img.Region02[LowOffset + i] = (byte)(raw & 0xFF);
            img.Region02[HighOffset + i] = (byte)(raw >> 8);
        }
        img.UpdateCrc();
    }
}

// The same edit-and-check sequence as the macro and SOCD planners, for the actuation table: apply in memory, let ImageDiffer confirm
// that only the actuation table and the CRC changed, and hand back GG's live 31 47 frame to send just before the save (GG's order in
// profile2-actuation-socd / -multikey).
public static class ActuationPlanner
{
    public static MacroPlan Plan(byte[] region02Read, byte[] region03Read, double globalMm, IReadOnlyDictionary<byte, double> perKeyMm, int slot)
    {
        ProfileImage before = ProfileWriter.ImageFromDump(region02Read, region03Read);
        MacroPlan Refuse(string why) => new(false, why, "", "", before, before, null, Array.Empty<string>(), Array.Empty<string>(), 0, null, slot);

        if (before.StoredCrc != before.ComputedCrc) return Refuse($"The checksum stored in slot {slot} is not valid, so I won't touch it.");

        byte[] frame;
        try { frame = Actuation.BuildFrameMm(globalMm, perKeyMm); }
        catch (ArgumentException ex) { return Refuse(ex.Message); }

        Actuation.TryExactRaw(globalMm, out ushort globalRaw);
        var perKeyRaw = new Dictionary<byte, ushort>();
        var notStored = new List<string>();
        foreach (var (key, mm) in perKeyMm)
        {
            Actuation.TryExactRaw(mm, out ushort raw);
            if (raw == globalRaw) continue;
            if (ActuationImage.IsStorable(key)) perKeyRaw[key] = raw;
            else notStored.Add(KeyNames.Name(key));
        }

        var (oldGlobal, oldPerKey) = ActuationImage.Read(before);
        ProfileImage after = before.Clone();
        ActuationImage.Apply(after, globalRaw, perKeyRaw);
        if (after.Region02.AsSpan(0, ProfileWriter.SafeEnd).SequenceEqual(before.Region02.AsSpan(0, ProfileWriter.SafeEnd)))
            return Refuse($"That is already what slot {slot} holds, so there is nothing to write." + (notStored.Count > 0 ? $" ({string.Join(", ", notStored)} is sent live only.)" : ""));

        ImageDiff diff = ImageDiffer.Compute(before, after, region02Read);
        if (!diff.Ok) return Refuse("The change would touch something unexpected, so I refused it. " + string.Join(" ", diff.Problems));

        static string Mm(ushort raw) => Actuation.KnownGoodValues.Where(v => v.Raw == raw).Select(v => $"{v.Mm:0.0}").DefaultIfEmpty($"raw {raw}").First();
        List<string> Describe(ushort g, Dictionary<byte, ushort> p) => new()
        {
            p.Count == 0 ? $"every key at {Mm(g)} mm" : $"{Mm(g)} mm for every key, except " + string.Join("; ", p.GroupBy(kv => kv.Value).OrderBy(x => x.Key)
                .Select(x => $"{string.Join(", ", x.Select(kv => KeyNames.Name(kv.Key)).OrderBy(n => n))} at {Mm(x.Key)} mm")),
        };

        string headline = "Save actuation: " + Describe(globalRaw, perKeyRaw)[0]
            + (notStored.Count > 0 ? $" ({string.Join(", ", notStored)} can only be sent live: the profile has no place for it.)" : "");
        return new MacroPlan(true, null, headline, "pre-actuation", before, after, diff, Describe(oldGlobal, oldPerKey), Describe(globalRaw, perKeyRaw),
            ProfileWriter.BuildWritePlan(after, slot).Count, null, slot, frame);
    }
}
