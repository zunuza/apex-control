using System.Text;

namespace ApexControl.Core;

public sealed record SlotSummary(int Slot, string Name, uint CrcStored, uint CrcComputed, int MacroCount, IReadOnlyList<(int EntryOffset, int Ptr)> BoundEntries)
{
    public bool CrcOk => CrcStored == CrcComputed;

    // One-line text form, used by the CLI and in summary.txt files.
    public override string ToString()
    {
        string bound = BoundEntries.Count == 0 ? "none" : string.Join(" ", BoundEntries.Select(b => $"@{b.EntryOffset}(ptr {b.Ptr})"));
        return $"slot {Slot}: '{Name}'  CRC stored {CrcStored:x8} / computed {CrcComputed:x8} {(CrcOk ? "OK" : "MISMATCH")}  macros: {MacroCount}  macro-bound keymap entries: {bound}";
    }
}

public static class ProfileInfo
{
    public static SlotSummary Summarize(int slot, byte[] region02)
    {
        string name = Encoding.ASCII.GetString(region02, 2, 16).Split('\0')[0];
        uint stored = BitConverter.ToUInt32(region02, ProfileImage.CrcOffset);
        uint computed = Crc32W.Compute(region02, ProfileImage.CrcOffset);

        int macros = 0, pos = MacroEditor.MacroAreaBase;
        while (pos + 4 <= ProfileImage.CrcOffset && region02[pos] == 0x77) { macros++; pos += 4 * (2 + region02[pos + 2]); }

        var bound = new List<(int, int)>();
        for (int p = MacroEditor.KeymapStart; p + 5 <= MacroEditor.KeymapEnd; p += 5)
            if (region02[p] == 0x71) bound.Add((p, region02[p + 3]));

        return new SlotSummary(slot, name, stored, computed, macros, bound);
    }
}