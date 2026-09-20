// ApexDeviceScan
//
// Read-only HID diagnostic tool. Does NOT send anything to any device.
// It just asks Windows what HID devices exist and dumps everything useful
// for reverse-engineering: VID/PID, interface topology, usage page/usage,
// report lengths, and raw + parsed report descriptors.
//
// Purpose: find the exact PID(s) and interface layout for the wired
// Apex Pro TKL (64734) on this specific PC, since public sources disagree
// on the PID across Apex Pro variants (original wired vs. wireless vs. Gen 3).
//
// Output goes to the console AND to a timestamped .log file next to the
// exe, so it can be copy-pasted or shared for the next step.

using System.Text;
using HidSharp;
using HidSharp.Reports;

namespace ApexDeviceScan;

internal static class Program
{
    private const int SteelSeriesVendorId = 0x1038;

    private static readonly StringBuilder Log = new();

    private static void Main()
    {
        Write("ApexDeviceScan - read-only HID enumeration");
        Write($"Run at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Write(new string('=', 70));
        Write("");

        List<HidDevice> allDevices;
        try
        {
            allDevices = DeviceList.Local.GetHidDevices().ToList();
        }
        catch (Exception ex)
        {
            Write($"FATAL: could not enumerate HID devices: {ex}");
            FlushLogAndWait();
            return;
        }

        Write($"Total HID devices found on this system: {allDevices.Count}");
        Write("");

        var steelSeriesDevices = allDevices
            .Where(d => d.VendorID == SteelSeriesVendorId)
            .ToList();

        if (steelSeriesDevices.Count == 0)
        {
            Write("!! No devices with VendorID 0x1038 (SteelSeries) were found. !!");
            Write("   Possible causes:");
            Write("   - The keyboard is unplugged, or plugged into a hub with issues.");
            Write("   - SteelSeries GG's driver layer is holding the interface exclusively");
            Write("     (try closing/exiting SteelSeries GG from the tray, not just the window).");
            Write("   - It genuinely enumerates under a different VID (unlikely, but the full");
            Write("     device dump below will show everything so we can spot it).");
            Write("");
        }
        else
        {
            Write($">> Found {steelSeriesDevices.Count} SteelSeries (VID 0x1038) HID interface(s):");
            Write(new string('-', 70));
            foreach (var device in steelSeriesDevices)
            {
                DumpDevice(device, highlighted: true);
            }
        }

        Write("");
        Write(new string('=', 70));
        Write("Full device list (every HID device on the system, for reference):");
        Write(new string('=', 70));
        foreach (var device in allDevices.Where(d => d.VendorID != SteelSeriesVendorId))
        {
            DumpDevice(device, highlighted: false);
        }

        FlushLogAndWait();
    }

    private static void DumpDevice(HidDevice device, bool highlighted)
    {
        string marker = highlighted ? "★" : " ";
        Write($"{marker} VID: 0x{device.VendorID:X4}   PID: 0x{device.ProductID:X4}   ReleaseNumberBcd: 0x{device.ReleaseNumberBcd:X4}");

        Write($"    Product name:      {TryGet(() => device.GetProductName())}");
        Write($"    Manufacturer:      {TryGet(() => device.GetManufacturer())}");
        Write($"    Serial number:     {TryGet(() => device.GetSerialNumber())}");
        Write($"    Device path:       {device.DevicePath}");
        Write($"    Max input len:     {device.GetMaxInputReportLength()} bytes");
        Write($"    Max output len:    {device.GetMaxOutputReportLength()} bytes");
        Write($"    Max feature len:   {device.GetMaxFeatureReportLength()} bytes");

        // Can we even open it? Interfaces claimed exclusively by the OS
        // keyboard/HID class driver often fail here - that's expected and
        // still useful information (tells us which interface is the "free"
        // vendor-control one vs. the plain keyboard one).
        string openResult;
        try
        {
            if (device.TryOpen(out var stream))
            {
                openResult = "OK (opened successfully)";
                stream.Dispose();
            }
            else
            {
                openResult = "FAILED (likely exclusively claimed by OS / another process)";
            }
        }
        catch (Exception ex)
        {
            openResult = $"FAILED ({ex.GetType().Name}: {ex.Message})";
        }
        Write($"    Open test:         {openResult}");

        // Raw report descriptor - always attempt, this is the most reliable
        // source of truth even if HidSharp's parser can't fully interpret it.
        try
        {
            byte[] raw = device.GetRawReportDescriptor();
            Write($"    Raw report descriptor ({raw.Length} bytes):");
            Write("      " + HexDump(raw));
        }
        catch (Exception ex)
        {
            Write($"    Raw report descriptor: FAILED ({ex.GetType().Name}: {ex.Message})");
        }

        // Parsed report descriptor - usage pages/usages and per-report field
        // layout. This can throw on nonstandard descriptors, so it's best
        // effort on top of the raw dump above.
        try
        {
            ReportDescriptor parsed = device.GetReportDescriptor();
            Write("    Parsed top-level collections:");
            foreach (var item in parsed.DeviceItems)
            {
                string usages = string.Join(", ", item.Usages.GetAllValues().Select(u => $"0x{u:X8}"));
                Write($"      UsagePage/Usage(s): {usages}");
                DumpReports("Input", item.InputReports);
                DumpReports("Output", item.OutputReports);
                DumpReports("Feature", item.FeatureReports);
            }
        }
        catch (Exception ex)
        {
            Write($"    Parsed report descriptor: FAILED ({ex.GetType().Name}: {ex.Message})");
        }

        Write("");
    }

    private static void DumpReports(string kind, IEnumerable<Report> reports)
    {
        foreach (var report in reports)
        {
            int totalBits = report.DataItems.Sum(di => di.ElementBits * di.ElementCount);
            Write($"        {kind} report - ReportID: {report.ReportID}, length: {(totalBits + 7) / 8 + 1} bytes (incl. report ID byte)");
            foreach (var dataItem in report.DataItems)
            {
                string usages = string.Join(", ", dataItem.Usages.GetAllValues().Select(u => $"0x{u:X8}"));
                Write($"          field: usages=[{usages}] elementBits={dataItem.ElementBits} elementCount={dataItem.ElementCount}");
            }
        }
    }

    private static string TryGet(Func<string> getter)
    {
        try
        {
            return getter();
        }
        catch (Exception ex)
        {
            return $"(unavailable: {ex.GetType().Name})";
        }
    }

    private static string HexDump(byte[] bytes)
    {
        return string.Join(" ", bytes.Select(b => b.ToString("X2")));
    }

    private static void Write(string line)
    {
        Console.WriteLine(line);
        Log.AppendLine(line);
    }

    private static void FlushLogAndWait()
    {
        try
        {
            string logPath = Path.Combine(AppContext.BaseDirectory, $"apex-device-scan-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(logPath, Log.ToString());
            Console.WriteLine();
            Console.WriteLine($"Full output saved to: {logPath}");
            Console.WriteLine("Send/paste that file back for the next step.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(Could not write log file: {ex.Message})");
        }

        Console.WriteLine();
        Console.WriteLine("Press Enter to exit...");
        Console.ReadLine();
    }
}
