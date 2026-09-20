using HidSharp;

namespace ApexControl.Core;

// Actuation on the wired Apex Pro TKL: replays the HID Feature report SteelSeries GG itself sends.
// Wire format: opcode 0x31 0x47, then one [key][value-lo][value-hi] triplet per key, key = USB HID keyboard
// usage code, value = little-endian u16. GG always sends the full key table, so a per-key change is the same
// frame with one triplet holding a different value.
//
// Safety rule: only raw values GG itself has been observed sending are ever written - never interpolated or
// synthesized. Requesting an in-between mm snaps to the nearest known value.
public static class Actuation
{
    // (mm, raw u16 LE). Every raw value was captured from GG's own traffic. Firm anchors confirmed by independent
    // captures: 0.1 / 2.0 / 3.6 / 4.0. The other labels come from the labeled sweep in GG's 0.1mm slider steps.
    // 3.2 is excluded: it caused key-repeat spam on real hardware. 3.7-3.9 were never captured (skipped on purpose).
    public static readonly IReadOnlyList<(double Mm, ushort Raw)> KnownGoodValues = new (double, ushort)[]
    {
        (0.1, 1542), (0.2, 1286), (0.3, 1544), (0.4, 2058), (0.5, 2571), (0.6, 2829), (0.7, 3343), (0.8, 3858),
        (0.9, 4628), (1.0, 5143), (1.1, 5913), (1.2, 6428), (1.3, 7199), (1.4, 7971), (1.5, 8998), (1.6, 9770),
        (1.7, 10798), (1.8, 11826), (1.9, 12855), (2.0, 14139), (2.1, 15168), (2.2, 16454), (2.3, 18124),
        (2.4, 19538), (2.5, 21081), (2.6, 22880), (2.7, 24680), (2.8, 26737), (2.9, 29051), (3.0, 31621),
        (3.1, 34192), (3.3, 40106), (3.4, 43704), (3.5, 47296), (3.6, 49352), (4.0, 54489),
    };

    // HID usage codes GG's frame addresses, in wire order.
    public static readonly IReadOnlyList<byte> KeyCodes = BuildKeyCodes();

    // Keys that always carry GG's fixed sentinel (raw 0x1F23, wire bytes 23 1F): the ISO/international keys
    // (0x32, 0x64, 0x87-0x8B) that this ANSI board doesn't physically have.
    public static readonly IReadOnlySet<byte> SentinelKeys = new HashSet<byte> { 0x32, 0x64, 0x87, 0x88, 0x89, 0x8A, 0x8B };
    public const ushort SentinelRaw = 0x1F23;

    public static bool IsAdjustable(byte hid) => KeyCodes.Contains(hid) && !SentinelKeys.Contains(hid);

    private static byte[] BuildKeyCodes()
    {
        var codes = new List<byte>();
        for (byte k = 0x04; k <= 0x28; k++) codes.Add(k);
        for (byte k = 0x2A; k <= 0x39; k++) codes.Add(k);
        codes.Add(0x64);
        for (byte k = 0x87; k <= 0x8B; k++) codes.Add(k);
        for (byte k = 0xE0; k <= 0xE7; k++) codes.Add(k);
        codes.Add(0xF0);
        return codes.ToArray();
    }

    public static (double Mm, ushort Raw) Snap(double mm) => KnownGoodValues.OrderBy(v => Math.Abs(v.Mm - mm)).First();

    // The raw value for a mm that is exactly one of the known-good values (no snapping); false otherwise.
    public static bool TryExactRaw(double mm, out ushort raw)
    {
        foreach ((double m, ushort r) in KnownGoodValues)
            if (Math.Abs(m - mm) < 0.0005) { raw = r; return true; }
        raw = 0;
        return false;
    }

    // BuildFrame from mm values, which must each be exactly a known-good value and every per-key entry an adjustable key:
    // anything else throws ArgumentException instead of sending something GG was never seen sending.
    public static byte[] BuildFrameMm(double globalMm, IReadOnlyDictionary<byte, double>? perKeyMm = null)
    {
        if (!TryExactRaw(globalMm, out ushort globalRaw)) throw new ArgumentException($"{globalMm} mm is not one of the known-good actuation values.");
        var perKey = new Dictionary<byte, ushort>();
        foreach (var (key, mm) in perKeyMm ?? new Dictionary<byte, double>())
        {
            if (!IsAdjustable(key)) throw new ArgumentException($"Key 0x{key:X2} is not adjustable with GG's actuation command.");
            if (!TryExactRaw(mm, out ushort raw)) throw new ArgumentException($"{mm} mm (key 0x{key:X2}) is not one of the known-good actuation values.");
            perKey[key] = raw;
        }
        return BuildFrame(globalRaw, perKey);
    }

    // The 642-byte Feature payload (without the report-ID byte).
    public static byte[] BuildFrame(ushort globalRaw, IReadOnlyDictionary<byte, ushort>? perKey = null)
    {
        var frame = new byte[642];
        frame[0] = 0x31;
        frame[1] = 0x47;

        int offset = 2;
        foreach (byte key in KeyCodes)
        {
            ushort raw = SentinelKeys.Contains(key) ? SentinelRaw
                : perKey is not null && perKey.TryGetValue(key, out ushort custom) ? custom
                : globalRaw;
            frame[offset] = key;
            frame[offset + 1] = (byte)(raw & 0xFF);
            frame[offset + 2] = (byte)(raw >> 8);
            offset += 3;
        }
        return frame;
    }

    public static int FrameContentLength => 2 + KeyCodes.Count * 3;

    // "every key at 0.8 mm" or "0.8 mm for every key, except S, A at 0.1 mm; D at 1.5 mm".
    public static string Describe(double globalMm, IReadOnlyDictionary<byte, double> perKeyMm)
    {
        var differing = perKeyMm.Where(kv => Math.Abs(kv.Value - globalMm) > 0.0005).ToList();
        if (differing.Count == 0) return $"every key at {globalMm:0.0} mm";
        var groups = differing.GroupBy(kv => kv.Value).OrderBy(g => g.Key)
            .Select(g => $"{string.Join(", ", g.Select(kv => KeyNames.Name(kv.Key)).OrderBy(n => n))} at {g.Key:0.0} mm");
        return $"{globalMm:0.0} mm for every key, except {string.Join("; ", groups)}";
    }

    // Opens mi_01 just long enough to send the frame.
    public static void Send(byte[] frame642)
    {
        HidDevice? device = DeviceList.Local.GetHidDevices(0x1038, 0x1614)
            .FirstOrDefault(d => d.DevicePath.Contains("mi_01", StringComparison.OrdinalIgnoreCase));
        if (device is null)
            throw new KeyboardException("Could not find the Apex Pro TKL's command interface (mi_01). Is it plugged in?");
        if (!device.TryOpen(out HidStream? stream) || stream is null)
            throw new KeyboardException("Found the device but couldn't open it. Fully quit SteelSeries GG from the tray and retry.");

        using (stream)
        {
            var buf = new byte[643];                 // report ID 0 + 642-byte payload
            Array.Copy(frame642, 0, buf, 1, 642);
            stream.SetFeature(buf);
        }
    }

    // Same, over an already-open link.
    public static void Send(KeyboardLink link, byte[] frame642) => link.SetFeature(frame642);
}