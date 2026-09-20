using System.IO.Compression;
using System.Text;

namespace ApexControl.Core;

// Optional, opt-in: bundles what a read check saw into one .zip on this PC that the user can choose to send to the developer.
// Nothing is uploaded by the app. By default personal content is stripped (the profile's name, the macro area, and which keys
// have macros); the user has to ask for the full raw read.
public static class CompatibilityExport
{
    // The profile as read, with the personal parts removed: profile name blanked, macro area zeroed, macro-bound key-table
    // entries turned back into plain ones. Everything else (key table, SOCD block, actuation area, ...) is kept as read, and
    // the stored checksum is left as read (so it no longer validates - the report records the original result).
    public static byte[] SanitizeRegion02(byte[] region02)
    {
        byte[] r = (byte[])region02.Clone();
        Array.Clear(r, 2, 16);                                                           // profile name
        foreach (var (hid, off) in KeymapLayout.DefaultOffsets)
            if (r[off] == 0x71) { r[off] = 0x51; r[off + 1] = hid; r[off + 2] = 0; r[off + 3] = 0; r[off + 4] = 0; }   // macro-bound -> plain
        Array.Clear(r, MacroEditor.MacroAreaBase, MacroEditor.MacroAreaLimit - MacroEditor.MacroAreaBase);          // macro records
        return r;
    }

    public static string Contents(bool includePersonalData)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Apex Control compatibility export");
        sb.AppendLine();
        sb.AppendLine("This file was created on your PC by Apex Control and has NOT been sent anywhere. It is only useful if you choose to send it to the developer.");
        sb.AppendLine();
        sb.AppendLine("Inside:");
        sb.AppendLine("  report.txt                 the compatibility report (model IDs, interface sizes, profile-format checks; no serial numbers or device paths)");
        if (includePersonalData)
        {
            sb.AppendLine("  <product id>/slot1-region02.bin   Config 1's settings exactly as read from the keyboard, INCLUDING its name and any macros");
            sb.AppendLine("  <product id>/slot1-region03.bin   Config 1's second data block exactly as read (lighting and similar), which may include personal settings");
        }
        else
        {
            sb.AppendLine("  <product id>/slot1-region02.bin   Config 1's settings as read, with the profile's name blanked, the macro contents removed, and macro-bound keys shown as plain keys.");
            sb.AppendLine("                                    Key positions, the SOCD block and the other settings are kept. Because of those removals its checksum no longer");
            sb.AppendLine("                                    validates; the report records the checksum result of the original read.");
            sb.AppendLine("  (the second data block, region 03, is left out)");
        }
        sb.AppendLine();
        sb.AppendLine("Only send this if you are comfortable with what is listed above.");
        return sb.ToString();
    }

    // Writes one zip with a folder per device that was read successfully. Returns the path. Throws IOException on trouble.
    public static string WriteZip(string folder, IReadOnlyList<(SteelSeriesDevice Device, ReadCheckResult Read)> items, string reportText, bool includePersonalData, DateTime now)
    {
        var usable = items.Where(i => i.Read.Ok && i.Read.Region02 is not null).ToList();
        if (usable.Count == 0) throw new IOException("There is nothing to export: no keyboard has been read yet.");

        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"apex-compatibility-export-{now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        void Add(string name, byte[] data)
        {
            ZipArchiveEntry e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using Stream s = e.Open();
            s.Write(data, 0, data.Length);
        }

        Add("EXPORT-CONTENTS.txt", Encoding.UTF8.GetBytes(Contents(includePersonalData)));
        Add("report.txt", Encoding.UTF8.GetBytes(reportText));
        foreach (var (device, read) in usable)
        {
            string dir = $"{device.ProductId:x4}/";
            Add(dir + "slot1-region02.bin", includePersonalData ? read.Region02! : SanitizeRegion02(read.Region02!));
            if (includePersonalData && read.Region03 is not null) Add(dir + "slot1-region03.bin", read.Region03);
        }
        return path;
    }
}
