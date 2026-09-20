using System.Text;

namespace ApexControl.Core;

// Slots: every slot that has both of its files in the folder (an all-profiles backup has 1-5; a pre-write snapshot has one).
public sealed record BackupInfo(string Path, string Name, bool HasSlot1, bool HasAllSlots, string Summary, IReadOnlyList<int>? Slots = null)
{
    public bool HasSlot(int slot) => Slots?.Contains(slot) ?? (slot == 1 && HasSlot1);
}

// Keyboard profile dumps and pre-write snapshots, as folders of slotN-region0M.bin files.
public static class BackupStore
{
    public static string DefaultRoot(string projectRoot) => System.IO.Path.Combine(projectRoot, "backups");

    // Writes every slot/region in the dump plus a summary.txt; returns the summary text.
    public static string SaveDump(string dir, DumpResult res)
    {
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.AppendLine($"ApexMacro dump {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Firmware: {string.Join(", ", res.Firmware)}");
        sb.AppendLine($"Header word (2-byte read): {(res.Header is null ? "n/a" : Convert.ToHexString(res.Header))}");
        sb.AppendLine();

        foreach (var kv in res.Data.OrderBy(k => k.Key.Region).ThenBy(k => k.Key.Slot))
            File.WriteAllBytes(System.IO.Path.Combine(dir, $"slot{kv.Key.Slot}-region0{kv.Key.Region}.bin"), kv.Value);

        foreach (int slot in res.Data.Keys.Select(k => k.Slot).Distinct().OrderBy(s => s))
            sb.AppendLine(ProfileInfo.Summarize(slot, res.Data[(slot, 2)]).ToString());

        File.WriteAllText(System.IO.Path.Combine(dir, "summary.txt"), sb.ToString());
        return sb.ToString();
    }

    // A pre-write snapshot of one slot exactly as read from the keyboard.
    public static void SaveSlotSnapshot(string dir, int slot, byte[] region02, byte[] region03)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(System.IO.Path.Combine(dir, $"slot{slot}-region02.bin"), region02);
        File.WriteAllBytes(System.IO.Path.Combine(dir, $"slot{slot}-region03.bin"), region03);
        File.WriteAllText(System.IO.Path.Combine(dir, "summary.txt"),
            $"Pre-write backup of slot {slot}, taken " + DateTime.Now + Environment.NewLine + ProfileInfo.Summarize(slot, region02) + Environment.NewLine);
    }

    public static void SaveSlot1Snapshot(string dir, byte[] region02, byte[] region03) => SaveSlotSnapshot(dir, 1, region02, region03);

    public static bool TryLoadSlot(string dir, int slot, out byte[] region02, out byte[] region03)
    {
        string f2 = System.IO.Path.Combine(dir, $"slot{slot}-region02.bin"), f3 = System.IO.Path.Combine(dir, $"slot{slot}-region03.bin");
        if (!File.Exists(f2) || !File.Exists(f3)) { region02 = region03 = Array.Empty<byte>(); return false; }
        region02 = File.ReadAllBytes(f2);
        region03 = File.ReadAllBytes(f3);
        return true;
    }

    public static bool TryLoadSlot1(string dir, out byte[] region02, out byte[] region03) => TryLoadSlot(dir, 1, out region02, out region03);

    public static byte[]? TryLoad(string dir, int slot, int region)
    {
        string f = System.IO.Path.Combine(dir, $"slot{slot}-region0{region}.bin");
        return File.Exists(f) ? File.ReadAllBytes(f) : null;
    }

    // Oldest first (folder names start with a timestamp).
    public static List<BackupInfo> List(string root)
    {
        var result = new List<BackupInfo>();
        if (!Directory.Exists(root)) return result;
        foreach (string d in Directory.GetDirectories(root).OrderBy(x => x))
        {
            bool slot1 = File.Exists(System.IO.Path.Combine(d, "slot1-region02.bin")) && File.Exists(System.IO.Path.Combine(d, "slot1-region03.bin"));
            bool all = Enumerable.Range(1, ReadSession.Slots).All(s => File.Exists(System.IO.Path.Combine(d, $"slot{s}-region02.bin")));
            string summary = File.Exists(System.IO.Path.Combine(d, "summary.txt")) ? File.ReadAllText(System.IO.Path.Combine(d, "summary.txt")) : "";
            var slots = Enumerable.Range(1, ReadSession.Slots).Where(s => File.Exists(System.IO.Path.Combine(d, $"slot{s}-region02.bin")) && File.Exists(System.IO.Path.Combine(d, $"slot{s}-region03.bin"))).ToList();
            result.Add(new BackupInfo(d, System.IO.Path.GetFileName(d), slot1, all, summary, slots));
        }
        return result;
    }

    public static string? NewestWithSlot1(string root) => List(root).LastOrDefault(b => b.HasSlot1)?.Path;
}