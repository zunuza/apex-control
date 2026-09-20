namespace ApexControl.Core;

// SOCD (GG calls it "Rapid Tap"): when both keys of a pair are held at once, the keyboard reports just one of them.
// Layout in region 02 (from GG's saves, see docs/PROTOCOL_NOTES.md):
//   2887        on/off flag: 01 on, 00 off
//   2888 + 5*n  pair n (n = 0..4): [key 1 HID][key 2 HID][behaviour][00][00]; an unused pair is all zeros
//   behaviour   00 last input priority, 01 key 1 priority, 02 key 2 priority
// With no pairs GG switches the feature off and leaves a placeholder pair Q + W in the first entry; we write exactly that.

public enum SocdBehavior : byte { LastInputPriority = 0, Key1Priority = 1, Key2Priority = 2 }

public sealed record SocdPair(byte Key1, byte Key2, SocdBehavior Behavior);

public sealed record SocdConfig(bool Enabled, IReadOnlyList<SocdPair> Pairs);

public static class SocdEditor
{
    public const int FlagOffset = 2887;
    public const int EntriesStart = 2888;
    public const int EntrySize = 5;
    public const int MaxPairs = 5;
    public const int BlockEnd = EntriesStart + MaxPairs * EntrySize;   // 2913, exclusive

    private const byte PlaceholderKey1 = 0x14, PlaceholderKey2 = 0x1A;   // Q, W: what GG leaves when the last pair is deleted

    public static string BehaviorName(SocdBehavior b) => b switch
    {
        SocdBehavior.LastInputPriority => "Last input priority",
        SocdBehavior.Key1Priority => "Key 1 priority",
        SocdBehavior.Key2Priority => "Key 2 priority",
        _ => $"behaviour {(byte)b}",
    };

    // Reads the block. Throws InvalidOperationException if it holds something this code does not understand.
    public static SocdConfig Read(ProfileImage img)
    {
        byte[] r = img.Region02;
        if (r[FlagOffset] > 1) throw new InvalidOperationException($"The SOCD on/off flag holds an unexpected value ({r[FlagOffset]:X2}).");
        bool enabled = r[FlagOffset] == 1;

        var pairs = new List<SocdPair>();
        for (int i = 0; i < MaxPairs; i++)
        {
            int o = EntriesStart + i * EntrySize;
            byte k1 = r[o], k2 = r[o + 1], beh = r[o + 2];
            if (k1 == 0 && k2 == 0 && beh == 0 && r[o + 3] == 0 && r[o + 4] == 0) continue;   // unused
            if (k1 == 0 || k2 == 0) throw new InvalidOperationException($"SOCD pair {i + 1} has only one key ({Convert.ToHexString(r, o, EntrySize)}).");
            if (beh > 2) throw new InvalidOperationException($"SOCD pair {i + 1} uses a behaviour this app does not know ({beh:X2}).");
            if (r[o + 3] != 0 || r[o + 4] != 0) throw new InvalidOperationException($"SOCD pair {i + 1} has extra settings this app does not know ({Convert.ToHexString(r, o, EntrySize)}).");
            pairs.Add(new SocdPair(k1, k2, (SocdBehavior)beh));
        }

        // Feature off with GG's placeholder pair = "no pairs".
        if (!enabled && pairs.Count == 1 && pairs[0] == new SocdPair(PlaceholderKey1, PlaceholderKey2, SocdBehavior.LastInputPriority))
            pairs.Clear();
        return new SocdConfig(enabled, pairs);
    }

    // Null if the configuration can be written; otherwise what is wrong, in plain words.
    public static string? Validate(SocdConfig cfg)
    {
        if (cfg.Pairs.Count > MaxPairs) return $"At most {MaxPairs} pairs.";
        if (cfg.Enabled && cfg.Pairs.Count == 0) return "Add at least one pair, or turn SOCD off.";

        var used = new HashSet<byte>();
        for (int i = 0; i < cfg.Pairs.Count; i++)
        {
            SocdPair p = cfg.Pairs[i];
            if (p.Key1 == 0 || p.Key2 == 0 || KeyNames.Name(p.Key1).StartsWith("0x") || KeyNames.Name(p.Key2).StartsWith("0x")) return $"Pair {i + 1}: pick both keys.";
            if (p.Key1 == p.Key2) return $"Pair {i + 1}: the two keys must be different.";
            if (!Enum.IsDefined(p.Behavior)) return $"Pair {i + 1}: pick a behavior.";
            foreach (byte k in new[] { p.Key1, p.Key2 })
                if (!used.Add(k)) return $"{KeyNames.Name(k)} is in more than one pair; each key can only be in one.";
        }
        return null;
    }

    // Writes the configuration into the image (and refreshes the CRC). Throws ArgumentException if it is not valid.
    public static void Apply(ProfileImage img, SocdConfig cfg)
    {
        string? bad = Validate(cfg);
        if (bad is not null) throw new ArgumentException(bad);

        byte[] r = img.Region02;
        Array.Clear(r, EntriesStart, BlockEnd - EntriesStart);
        r[FlagOffset] = (byte)(cfg.Enabled ? 1 : 0);
        if (cfg.Pairs.Count == 0)
        {
            r[EntriesStart] = PlaceholderKey1;
            r[EntriesStart + 1] = PlaceholderKey2;
        }
        for (int i = 0; i < cfg.Pairs.Count; i++)
        {
            int o = EntriesStart + i * EntrySize;
            r[o] = cfg.Pairs[i].Key1;
            r[o + 1] = cfg.Pairs[i].Key2;
            r[o + 2] = (byte)cfg.Pairs[i].Behavior;
        }
        img.UpdateCrc();
    }

    public static List<string> Describe(SocdConfig cfg)
    {
        var lines = new List<string> { cfg.Enabled ? "SOCD is ON" : "SOCD is OFF" };
        foreach (SocdPair p in cfg.Pairs)
            lines.Add($"{KeyNames.Name(p.Key1)} + {KeyNames.Name(p.Key2)}: {BehaviorName(p.Behavior).ToLowerInvariant()}");
        if (cfg.Pairs.Count == 0) lines.Add("(no pairs)");
        return lines;
    }
}

// The same edit-and-check sequence as MacroPlanner, for the SOCD block: apply in memory, let ImageDiffer confirm that only
// the SOCD block and the CRC changed, and work out the live on/off command GG sends before its save when the flag changes.
public static class SocdPlanner
{
    public static MacroPlan Plan(byte[] region02Read, byte[] region03Read, SocdConfig wanted, int slot = 1)
    {
        ProfileImage before = ProfileWriter.ImageFromDump(region02Read, region03Read);
        MacroPlan Refuse(string why) => new(false, why, "", "", before, before, null, Array.Empty<string>(), Array.Empty<string>(), 0, null, slot);

        if (before.StoredCrc != before.ComputedCrc) return Refuse($"The checksum stored in slot {slot} is not valid, so I won't touch it.");

        SocdConfig current;
        try { current = SocdEditor.Read(before); }
        catch (InvalidOperationException ex) { return Refuse($"Slot {slot}'s SOCD settings aren't in a shape I understand: " + ex.Message); }

        string? bad = SocdEditor.Validate(wanted);
        if (bad is not null) return Refuse(bad);

        ProfileImage after = before.Clone();
        SocdEditor.Apply(after, wanted);
        if (after.Region02.AsSpan(0, ProfileWriter.SafeEnd).SequenceEqual(before.Region02.AsSpan(0, ProfileWriter.SafeEnd)))
            return Refuse($"That is already what slot {slot} holds, so there is nothing to write.");

        ImageDiff diff = ImageDiffer.Compute(before, after, region02Read);
        if (!diff.Ok) return Refuse("The change would touch something unexpected, so I refused it. " + string.Join(" ", diff.Problems));

        // GG sends "1a <flag>" (an Output command, no reply) just before saving when the on/off switch was flipped (same on Config 1 and 2).
        byte[]? live = null;
        if (current.Enabled != wanted.Enabled)
        {
            // Captured from GG on Config 1 and on Config 2 (profile2-socd-on / -off): the same `1a <flag>` goes out first, then the save.
            live = new byte[64];
            live[0] = 0x1A;
            live[1] = (byte)(wanted.Enabled ? 1 : 0);
        }

        string headline = "Set SOCD: " + string.Join("; ", SocdEditor.Describe(wanted));
        return new MacroPlan(true, null, headline, "pre-socd", before, after, diff, SocdEditor.Describe(current), SocdEditor.Describe(wanted),
            ProfileWriter.BuildWritePlan(after, slot).Count, live, slot);
    }
}
