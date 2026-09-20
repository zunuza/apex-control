// ApexActuation
//
// Console front end for actuation on the wired Apex Pro TKL (64734). The protocol, the table of
// known-good values and the safety rule (only raw values GG itself was seen sending are ever written)
// live in ApexControl.Core's Actuation class; see docs/PROTOCOL_NOTES.md.
//
// Usage:
//   ApexActuation <mm> [KEY=mm ...]     e.g. ApexActuation 2.0 S=0.1 W=1.5
//   ApexActuation --list
//   add --dry-run to print the report instead of sending it

using ApexControl.Core;

namespace ApexActuation;

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
        bool dryRun = args.Contains("--dry-run");
        string[] positional = args.Where(a => a != "--dry-run").ToArray();

        if (positional.Length == 1 && positional[0] is "--list" or "-l")
        {
            Console.WriteLine("Known-good actuation values (mm -> raw):");
            foreach ((double mm, ushort raw) in Actuation.KnownGoodValues)
                Console.WriteLine($"  {mm,4:0.0}mm  ->  0x{raw:X4} ({raw})");
            Console.WriteLine("(3.2, 3.7, 3.8, 3.9 unavailable — 3.2 misbehaved on hardware, 3.7-3.9 never captured)");
            return 0;
        }

        if (positional.Length < 1 || !double.TryParse(positional[0], out double globalMm))
        {
            Console.WriteLine("Usage: ApexActuation <mm> [KEY=mm ...] [--dry-run]   e.g. ApexActuation 2.0 S=0.1");
            Console.WriteLine("       ApexActuation --list");
            return 0;
        }

        ushort globalRaw = Snap(globalMm, "global");
        var perKey = new Dictionary<byte, ushort>();
        foreach (string arg in positional.Skip(1))
        {
            string[] parts = arg.Split('=');
            if (parts.Length != 2 || !double.TryParse(parts[1], out double keyMm) || !KeyNames.TryParse(parts[0], out byte code))
            {
                Console.WriteLine($"Couldn't parse '{arg}'. Expected KEY=mm, e.g. S=0.1 (letters, digits, and common named keys).");
                return 0;
            }
            if (!Actuation.IsAdjustable(code))
            {
                Console.WriteLine($"Key {parts[0]} isn't addressable in GG's actuation frame.");
                return 0;
            }
            perKey[code] = Snap(keyMm, parts[0]);
        }

        byte[] frame = Actuation.BuildFrame(globalRaw, perKey);

        if (dryRun)
        {
            Console.WriteLine("Dry run — not sending. Report bytes (after report-ID byte):");
            Console.WriteLine(string.Join(" ", frame.Take(Actuation.FrameContentLength).Select(b => b.ToString("X2"))));
            return 0;
        }

        Actuation.Send(frame);
        Console.WriteLine("Sent. Test the keyboard's feel to confirm it took effect.");
        return 0;
    }

    private static ushort Snap(double mm, string label)
    {
        (double snapped, ushort raw) = Actuation.Snap(mm);
        string note = Math.Abs(snapped - mm) > 0.001 ? $" (nearest known-good to {mm}mm)" : "";
        Console.WriteLine($"{label}: {snapped:0.0}mm -> 0x{raw:X4} ({raw}){note}");
        return raw;
    }
}
