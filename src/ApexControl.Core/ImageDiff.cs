namespace ApexControl.Core;

// One changed area of the profile image. Area is "keymap entry", "macro area" or "CRC".
// Hex fields are uppercase without separators, at most 64 bytes (Truncated says if there was more).
public sealed record DiffEntry(string Area, string? KeyName, int Offset, int Length, string BeforeHex, string AfterHex, bool Truncated);

public sealed record ImageDiff(bool Ok, IReadOnlyList<DiffEntry> Entries, string? TailNote, int TotalChanged, IReadOnlyList<string> Problems);

public static class ImageDiffer
{
    // Compares two images over the whole readable part of region 02 and all of region 03, and checks that
    // ONLY the expected areas changed (keymap entries, the SOCD block, the actuation table, the macro area, the CRC). rawRegion02Read is what was
    // actually read from the keyboard, used to report whether the unused tail needs normalizing to 0xFF.
    public static ImageDiff Compute(ProfileImage before, ProfileImage after, byte[] rawRegion02Read)
    {
        var problems = new List<string>();
        var changed = new List<int>();
        for (int i = 0; i < ProfileWriter.ReadLen; i++)
            if (before.Region02[i] != after.Region02[i]) changed.Add(i);

        if (!before.Region03.AsSpan().SequenceEqual(after.Region03))
            return new ImageDiff(false, Array.Empty<DiffEntry>(), null, changed.Count, new[] { "UNEXPECTED: region 03 changed." });

        var areas = new (string Label, int From, int To)[]
        {
            ("keymap entry", MacroEditor.KeymapStart, MacroEditor.KeymapEnd),
            ("SOCD block", SocdEditor.FlagOffset, SocdEditor.BlockEnd),
            ("actuation table", ActuationImage.LowOffset, ActuationImage.HighOffset + ActuationImage.EntryCount),
            ("macro area", MacroEditor.MacroAreaBase, MacroEditor.MacroAreaLimit),
            ("CRC", ProfileImage.CrcOffset, ProfileWriter.SafeEnd),
        };

        int tailNotFf = 0;
        for (int i = ProfileWriter.SafeEnd; i < ProfileWriter.ReadLen; i++) if (rawRegion02Read[i] != 0xFF) tailNotFf++;
        int tailLen = ProfileWriter.ReadLen - ProfileWriter.SafeEnd;
        string tailNote = tailNotFf == 0
            ? $"  unused tail       @{ProfileWriter.SafeEnd} ({tailLen} bytes): 0xFF as read  ->  0xFF (unchanged)"
            : $"  unused tail       @{ProfileWriter.SafeEnd} ({tailLen} bytes): {tailNotFf} byte(s) are not 0xFF as read  ->  all written as 0xFF (normalized)";

        foreach (int i in changed.Where(i => !areas.Any(a => i >= a.From && i < a.To)))
            problems.Add($"UNEXPECTED change at offset {i}.");

        var entries = new List<DiffEntry>();
        foreach (var (label, from, to) in areas)
        {
            var idx = changed.Where(i => i >= from && i < to).ToList();
            if (idx.Count == 0) continue;

            if (label == "keymap entry")
            {
                foreach (int start in idx.Select(i => from + (i - from) / 5 * 5).Distinct().OrderBy(s => s))
                {
                    string who = KeymapLayout.KeyAt(start) is byte k ? KeyNames.Name(k) : "?";
                    entries.Add(new DiffEntry(label, who, start, 5, Convert.ToHexString(before.Region02, start, 5), Convert.ToHexString(after.Region02, start, 5), false));
                }
                continue;
            }

            int s0 = idx.Min(), e0 = idx.Max() + 1, shown = Math.Min(e0 - s0, 64);
            entries.Add(new DiffEntry(label, null, s0, e0 - s0, Convert.ToHexString(before.Region02, s0, shown), Convert.ToHexString(after.Region02, s0, shown), e0 - s0 > shown));
        }

        return new ImageDiff(problems.Count == 0, entries, tailNote, changed.Count, problems);
    }
}