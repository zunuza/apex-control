using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HidSharp;

namespace ApexControl.Core;

// A read-only "will this work on my keyboard?" check for SteelSeries keyboards other than the Apex Pro TKL this app was
// built and tested on. Two levels:
//   1. Scan: asks Windows what HID interfaces the device has (nothing is sent to it) and compares them with the TKL's.
//   2. Read check (opt-in): reads Config 1 with the same commands GG sends at startup, and tests whether the profile
//      image has the TKL's format (checksum, key table, macro area, SOCD block). Nothing is written.

public sealed record HidInterfaceInfo(string Name, int Input, int Output, int Feature, IReadOnlyList<uint> Usages);

public sealed record SteelSeriesDevice(int VendorId, int ProductId, int ReleaseBcd, string Product, string Manufacturer, IReadOnlyList<HidInterfaceInfo> Interfaces);

public enum LayoutMatch { ExactModel, SameInterfaceLayout, DifferentCommandInterface, NoCommandInterface }

public sealed record PassiveAssessment(LayoutMatch Match, string Headline, IReadOnlyList<string> Facts);

public enum ProfileFormat { Match, PartialMatch, NoMatch }

public sealed record ImageChecks(
    bool CrcOk, uint CrcStored, uint CrcComputed, string ProfileName,
    int KeymapRecognised, int KeymapUnused, int KeymapTotal, string MacroArea, string Socd,
    ProfileFormat Format, string Verdict);

public sealed record ReadCheckResult(bool Ok, string Message, IReadOnlyList<string> Firmware, ImageChecks? Checks, byte[]? Region02 = null, byte[]? Region03 = null);

public static class Compatibility
{
    public const int VendorId = 0x1038;
    public const int TklProductId = 0x1614;
    public const int TklReleaseBcd = 0x0416;

    // ---- level 1: what Windows sees --------------------------------------------------------------

    // Every SteelSeries HID device, grouped by product. Opens nothing and sends nothing.
    public static List<SteelSeriesDevice> Scan()
    {
        var result = new List<SteelSeriesDevice>();
        foreach (var group in DeviceList.Local.GetHidDevices(VendorId).GroupBy(d => d.ProductID).OrderBy(g => g.Key))
        {
            HidDevice first = group.First();
            var interfaces = new List<HidInterfaceInfo>();
            foreach (HidDevice d in group)
            {
                var usages = new List<uint>();
                try { foreach (var item in d.GetReportDescriptor().DeviceItems) usages.AddRange(item.Usages.GetAllValues()); }
                catch (Exception) { /* some descriptors can't be parsed; the sizes below are still useful */ }
                interfaces.Add(new HidInterfaceInfo(InterfaceName(d.DevicePath), Safe(d.GetMaxInputReportLength), Safe(d.GetMaxOutputReportLength), Safe(d.GetMaxFeatureReportLength), usages));
            }
            result.Add(new SteelSeriesDevice(VendorId, group.Key, first.ReleaseNumberBcd, Text(first.GetProductName), Text(first.GetManufacturer),
                interfaces.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList()));
        }
        return result;
    }

    private static string InterfaceName(string path)
    {
        Match mi = Regex.Match(path, @"mi_([0-9a-f]{2})", RegexOptions.IgnoreCase), col = Regex.Match(path, @"col([0-9a-f]{2})", RegexOptions.IgnoreCase);
        string name = mi.Success ? "mi_" + mi.Groups[1].Value : "interface";
        return col.Success ? name + " col" + col.Groups[1].Value : name;
    }

    private static int Safe(Func<int> f) { try { return f(); } catch (Exception) { return -1; } }
    private static string Text(Func<string> f) { try { return f() ?? ""; } catch (Exception) { return "(unavailable)"; } }

    // Compares a device's interfaces with the TKL's: mi_01 = vendor 0xFFC0 usage page, 65-byte input/output, 643-byte feature;
    // mi_04 = vendor 0xFFC1 usage page, 65-byte input. Pure (no I/O), so it is testable with made-up devices.
    public static PassiveAssessment Assess(SteelSeriesDevice dev)
    {
        static bool UsesPage(HidInterfaceInfo i, uint page) => i.Usages.Any(u => u >> 16 == page);
        HidInterfaceInfo? command = dev.Interfaces.FirstOrDefault(i => UsesPage(i, 0xFFC0) && i.Feature > 0);
        HidInterfaceInfo? watcher = dev.Interfaces.FirstOrDefault(i => UsesPage(i, 0xFFC1));
        bool sameSizes = command is { Input: 65, Output: 65, Feature: 643 } && watcher is { Input: 65 };

        var facts = new List<string>
        {
            $"USB ID {dev.VendorId:X4}:{dev.ProductId:X4}, product \"{dev.Product}\", release {dev.ReleaseBcd:X4}" +
                (dev.ProductId == TklProductId ? (dev.ReleaseBcd == TklReleaseBcd ? " (matches the TKL's 0416 = firmware 4.16)" : " (the TKL usually reports 0416)") : ""),
            $"{dev.Interfaces.Count} HID interface(s): " + string.Join("; ", dev.Interfaces.Select(i =>
                $"{i.Name} [{(i.Usages.Count == 0 ? "?" : string.Join(",", i.Usages.Distinct().Select(u => $"{u:X8}")))}] in {i.Input} / out {i.Output} / feature {i.Feature}")),
        };

        if (dev.ProductId == TklProductId)
            return new PassiveAssessment(LayoutMatch.ExactModel, "This is the Apex Pro TKL (1038:1614) that Apex Control was built and tested on.", facts);
        if (sameSizes)
            return new PassiveAssessment(LayoutMatch.SameInterfaceLayout,
                "Not the model Apex Control was built for, but it has the same command interface as the TKL (a vendor 0xFFC0 interface with 64-byte reports and a 642-byte feature report, plus a vendor 0xFFC1 input interface). That is encouraging but proves nothing about its profile format - run the read check.", facts);
        if (command is not null)
            return new PassiveAssessment(LayoutMatch.DifferentCommandInterface,
                $"It has a vendor command interface, but its report sizes differ from the TKL's (in {command.Input} / out {command.Output} / feature {command.Feature}), so its command format is probably different.", facts);
        return new PassiveAssessment(LayoutMatch.NoCommandInterface, "No vendor command interface (0xFFC0): this is not a keyboard Apex Control can talk to.", facts);
    }

    // ---- level 2: read Config 1 and test its format ------------------------------------------------

    // Sends the SAME read commands GG sends at startup on the TKL (session start, read slot 1, close-out). Writes nothing.
    // The close-out selects Config 1 as the active profile, as it does on the TKL.
    public static ReadCheckResult ReadCheck(SteelSeriesDevice dev, IOperationSink? sink = null)
    {
        sink ??= NullSink.Instance;
        try
        {
            using KeyboardLink link = KeyboardLink.Open(dev.ProductId);
            DumpResult res = ProfileReader.ReadSlots(link, new[] { 1 }, sink);
            ProfileReader.CloseSession(link, 1, sink);
            ImageChecks checks = Analyze(res.Data[(1, 2)], res.Data[(1, 3)]);
            return new ReadCheckResult(true, "Read Config 1.", res.Firmware.OrderBy(f => f).ToList(), checks, res.Data[(1, 2)], res.Data[(1, 3)]);
        }
        catch (Exception ex) when (ex is KeyboardException or TimeoutException or IOException)
        {
            return new ReadCheckResult(false, "The keyboard did not answer the read commands: " + ex.Message +
                " (this model may use a different command set, or GG / another program still has it open).", Array.Empty<string>(), null);
        }
    }

    // Does this profile image have the TKL's format? Pure, so it is tested against real dumps and mutated copies.
    public static ImageChecks Analyze(byte[] region02, byte[] region03)
    {
        if (region02.Length < ProfileWriter.ReadLen || region03.Length < ReadSession.Region03Pages * ProfileImage.PageSize)
            return new ImageChecks(false, 0, 0, "", 0, 0, KeymapLayout.DefaultOffsets.Count, "not read", "not read", ProfileFormat.NoMatch, "Too little data came back to check.");

        uint stored = BitConverter.ToUInt32(region02, ProfileImage.CrcOffset);
        uint computed = Crc32W.Compute(region02, ProfileImage.CrcOffset);
        bool crcOk = stored == computed;

        string raw = Encoding.ASCII.GetString(region02, 2, 16).Split('\0')[0];
        string name = raw.Length > 0 && raw.All(c => c >= 32 && c < 127) ? raw : (raw.Length == 0 ? "(empty)" : "(not text)");

        // A key-table entry is either the plain form (51 <key> 00 00 00), a macro entry (71 00 00 <ptr> 00), or all zeros
        // (unused: on the TKL's own Config 1 the numpad and PrtSc/ScrLk/Pause entries are zero).
        int recognised = 0, unused = 0;
        foreach (var (hid, off) in KeymapLayout.DefaultOffsets)
        {
            bool normal = region02[off] == 0x51 && region02[off + 1] == hid && region02[off + 2] == 0 && region02[off + 3] == 0 && region02[off + 4] == 0;
            bool macro = region02[off] == 0x71 && region02[off + 1] == 0 && region02[off + 2] == 0 && region02[off + 4] == 0;
            bool zero = region02.AsSpan(off, 5).ToArray().All(b => b == 0);
            if (normal || macro) recognised++;
            else if (zero) unused++;
        }
        int total = KeymapLayout.DefaultOffsets.Count;

        ProfileImage img = ProfileWriter.ImageFromDump(region02, region03);
        string macros, socd;
        try { int n = MacroEditor.ReadMacros(img, out _).Count; macros = $"understood ({n} macro{(n == 1 ? "" : "s")})"; }
        catch (InvalidOperationException ex) { macros = "NOT understood: " + ex.Message; }
        try { SocdConfig c = SocdEditor.Read(img); socd = "understood (" + string.Join("; ", SocdEditor.Describe(c)) + ")"; }
        catch (InvalidOperationException ex) { socd = "NOT understood: " + ex.Message; }

        // Most entries must positively match (a blank profile is all zeros and proves nothing), and almost none may be something else.
        bool keymapOk = recognised >= total * 60 / 100 && recognised + unused >= total * 95 / 100;
        bool macrosOk = macros.StartsWith("understood"), socdOk = socd.StartsWith("understood");

        ProfileFormat format;
        string verdict;
        if (!crcOk)
        {
            format = ProfileFormat.NoMatch;
            verdict = "The checksum does not match, so this keyboard's profile is not laid out like the TKL's (or the read was corrupted - try again). Apex Control would not write to it.";
        }
        else if (keymapOk && macrosOk && socdOk)
        {
            format = ProfileFormat.Match;
            verdict = "The profile format matches the Apex Pro TKL: same checksum, same key table, macro area and SOCD block all readable. Macros, SOCD and restore would very likely work the same way. The app still only knows this model by its USB ID, so it would need to be told to accept this one - and writing has not been tried on it.";
        }
        else
        {
            format = ProfileFormat.PartialMatch;
            var diffs = new List<string>();
            if (!keymapOk) diffs.Add($"the key table differs ({recognised} of {total} known entries recognised, {unused} unused - probably a different key layout)");
            if (!macrosOk) diffs.Add("the macro area is not laid out as expected");
            if (!socdOk) diffs.Add("the SOCD block is not laid out as expected");
            verdict = "Same profile container and checksum as the TKL, but " + string.Join("; ", diffs) + ". It would need captures of GG to support safely.";
        }
        return new ImageChecks(crcOk, stored, computed, name, recognised, unused, total, macros, socd, format, verdict);
    }

    // ---- the report --------------------------------------------------------------------------------

    public static string BuildReport(IReadOnlyList<SteelSeriesDevice> devices, IReadOnlyDictionary<int, ReadCheckResult>? readChecks = null)
    {
        var sb = new StringBuilder();
        string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?";
        sb.AppendLine($"Apex Control compatibility report ({DateTime.Now:yyyy-MM-dd HH:mm}, app {version}, {Environment.OSVersion.VersionString})");
        sb.AppendLine("Built and tested on: Apex Pro TKL, USB 1038:1614, firmware 4.16.8. Nothing was written to any keyboard.");
        sb.AppendLine();

        if (devices.Count == 0)
        {
            sb.AppendLine("No SteelSeries (USB vendor 1038) devices were found. Is the keyboard plugged in?");
            return sb.ToString();
        }

        foreach (SteelSeriesDevice dev in devices)
        {
            PassiveAssessment a = Assess(dev);
            sb.AppendLine($"== {dev.Product} ({dev.VendorId:X4}:{dev.ProductId:X4}) ==");
            sb.AppendLine(a.Headline);
            foreach (string f in a.Facts) sb.AppendLine("  - " + f);
            if (readChecks is not null && readChecks.TryGetValue(dev.ProductId, out ReadCheckResult? r))
            {
                sb.AppendLine("  Read check (Config 1, read-only):");
                if (!r.Ok || r.Checks is null) sb.AppendLine("    " + r.Message);
                else
                {
                    ImageChecks c = r.Checks;
                    if (r.Firmware.Count > 0) sb.AppendLine($"    firmware reply: {string.Join(", ", r.Firmware)}");
                    sb.AppendLine($"    profile name: {c.ProfileName}");
                    sb.AppendLine($"    checksum: stored {c.CrcStored:x8}, computed {c.CrcComputed:x8} - {(c.CrcOk ? "MATCH" : "MISMATCH")}");
                    sb.AppendLine($"    key table: {c.KeymapRecognised} of {c.KeymapTotal} known entries recognised, {c.KeymapUnused} unused (all zero)");
                    sb.AppendLine($"    macro area: {c.MacroArea}");
                    sb.AppendLine($"    SOCD block: {c.Socd}");
                    sb.AppendLine($"    verdict: {c.Verdict}");
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string DefaultReportFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexControl", "compatibility");
}
