using System.Collections.Concurrent;
using HidSharp;

namespace ApexControl.Core;

// Talks to the vendor HID interface mi_01: Output reports via SET_REPORT,
// Feature SET/GET_REPORT, and its 64-byte INPUT report, which is where the
// keyboard's acks/replies arrive (hardware-confirmed). mi_04 is also opened and
// watched, but has never delivered anything; the counters in ReplyStats show it.
public sealed class KeyboardLink : IDisposable
{
    private const int VendorId = 0x1038, ProductId = 0x1614;

    private readonly HidStream _cmd;
    private readonly HidStream _in01;   // second handle on mi_01, used only for reading its input reports
    private readonly HidStream _ack;
    private readonly BlockingCollection<byte[]> _replies = new();
    private readonly Thread[] _readers;
    private volatile bool _closing;
    private int _seen01, _seen04;

    public string ReplyStats => $"reports seen: mi_01={_seen01}, mi_04={_seen04}";

    private KeyboardLink(HidStream cmd, HidStream in01, HidStream ack)
    {
        _cmd = cmd;
        _in01 = in01;
        _ack = ack;
        _readers = new[]
        {
            new Thread(() => ReadLoop(_in01, ref _seen01)) { IsBackground = true, Name = "mi_01 reader" },
            new Thread(() => ReadLoop(_ack, ref _seen04)) { IsBackground = true, Name = "mi_04 reader" },
        };
        foreach (var r in _readers) r.Start();
    }

    // Is the keyboard's command interface (mi_01) visible to Windows? Cheap; does not open anything.
    public static bool IsPresent() =>
        DeviceList.Local.GetHidDevices(VendorId, ProductId).Any(d => d.DevicePath.Contains("mi_01", StringComparison.OrdinalIgnoreCase));

    // Opens the command interface of the Apex Pro TKL, or (for the read-only compatibility check) of another SteelSeries
    // keyboard with the same interface layout.
    public static KeyboardLink Open(int productId = ProductId)
    {
        var devices = DeviceList.Local.GetHidDevices(VendorId, productId).ToList();
        HidDevice? cmd = devices.FirstOrDefault(d => d.DevicePath.Contains("mi_01", StringComparison.OrdinalIgnoreCase));
        HidDevice? ack = devices.FirstOrDefault(d => d.DevicePath.Contains("mi_04", StringComparison.OrdinalIgnoreCase));
        if (cmd is null || ack is null)
            throw new KeyboardException(productId == ProductId
                ? "Could not find the Apex Pro TKL's vendor interfaces (mi_01 / mi_04). Is it plugged in?"
                : $"Could not find vendor interfaces mi_01 / mi_04 on device {VendorId:X4}:{productId:X4}. Is it plugged in, and does it have the same interface layout?");

        if (!cmd.TryOpen(out HidStream? cmdStream) || cmdStream is null)
            throw new KeyboardException("Couldn't open mi_01. Fully quit SteelSeries GG (tray icon -> Quit) and retry.");
        if (!ack.TryOpen(out HidStream? ackStream) || ackStream is null)
        {
            cmdStream.Dispose();
            throw new KeyboardException("Couldn't open mi_04. Fully quit SteelSeries GG (tray icon -> Quit) and retry.");
        }

        if (!cmd.TryOpen(out HidStream? in01Stream) || in01Stream is null)
        {
            cmdStream.Dispose();
            ackStream.Dispose();
            throw new KeyboardException("Couldn't open a second read handle on mi_01.");
        }

        cmdStream.ReadTimeout = Timeout.Infinite;
        in01Stream.ReadTimeout = Timeout.Infinite;
        ackStream.ReadTimeout = Timeout.Infinite;
        return new KeyboardLink(cmdStream, in01Stream, ackStream);
    }

    public void SendOutput(byte[] data64)
    {
        var buf = new byte[65];                    // report ID 0 + 64 bytes
        Array.Copy(data64, 0, buf, 1, 64);
        _cmd.Write(buf, 0, buf.Length);
    }

    public void SetFeature(byte[] data642)
    {
        var buf = new byte[643];                   // report ID 0 + 642 bytes
        Array.Copy(data642, 0, buf, 1, 642);
        _cmd.SetFeature(buf);
    }

    public byte[] GetFeature()
    {
        var buf = new byte[643];
        _cmd.GetFeature(buf);
        var data = new byte[642];
        Array.Copy(buf, 1, data, 0, 642);
        return data;
    }

    public void DrainReplies()
    {
        while (_replies.TryTake(out _)) { }
    }

    // Waits for a reply from mi_04 that satisfies the predicate; unrelated
    // reports are skipped.
    public byte[] WaitFor(Func<byte[], bool> match, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var skipped = new List<string>();
        while (true)
        {
            int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (left <= 0 || !_replies.TryTake(out byte[]? r, left))
            {
                string extra = skipped.Count == 0 ? "" : $"; unexpected report(s) while waiting: {string.Join(" | ", skipped.Take(3))}";
                throw new TimeoutException($"Timed out waiting for the keyboard's reply ({ReplyStats}{extra}).");
            }
            if (match(r)) return r;
            skipped.Add(Convert.ToHexString(r, 0, Math.Min(r.Length, 12)));
        }
    }

    private void ReadLoop(HidStream stream, ref int counter)
    {
        var buf = new byte[65];
        try
        {
            while (!_closing)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 1) continue;
                Interlocked.Increment(ref counter);
                var report = new byte[n - 1];      // drop the report-ID byte
                Array.Copy(buf, 1, report, 0, n - 1);
                _replies.Add(report);
            }
        }
        catch (Exception) when (_closing) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _closing = true;
        _cmd.Dispose();
        _in01.Dispose();
        _ack.Dispose();
        foreach (var r in _readers) r.Join(500);
    }
}

