// Offline model of the keyboard's profile image and the write transaction GG
// uses to store it. Nothing in this file touches hardware. All layouts here
// come from docs/PROTOCOL_NOTES.md and are checked against real captures by
// `ApexMacro verify`.

namespace ApexControl.Core;

public sealed record Step(bool IsFeature, byte[] Data)
{
    public string Describe() => IsFeature
        ? $"Feature {Data.Length}B hdr={Convert.ToHexString(Data, 0, 6)}"
        : $"Output  {Data.Length}B {Convert.ToHexString(Data, 0, 10)}";
}

public static class Crc32W
{
    // CRC-32, poly 0x04C11DB7, not reflected, init 0xFFFFFFFF, xorout 0, over
    // 32-bit little-endian words. Same as an STM32-style hardware CRC unit.
    public static uint Compute(byte[] data, int length)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i + 3 < length; i += 4)
        {
            crc ^= (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));
            for (int k = 0; k < 32; k++)
                crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04C11DB7u : crc << 1;
        }
        return crc;
    }
}

public sealed class ProfileImage
{
    public const int PageSize = 1024;
    public const int HalfSize = 512;
    public const int Region02Pages = 40;
    public const int Region03Pages = 12;
    public const int CrcOffset = 12556;   // CRC covers Region02[0..CrcOffset), stored little-endian right after

    public byte[] Region02 { get; } = new byte[Region02Pages * PageSize];
    public byte[] Region03 { get; } = new byte[Region03Pages * PageSize];

    public uint StoredCrc => BitConverter.ToUInt32(Region02, CrcOffset);
    public uint ComputedCrc => Crc32W.Compute(Region02, CrcOffset);

    public void UpdateCrc() => BitConverter.GetBytes(ComputedCrc).CopyTo(Region02, CrcOffset);

    public ProfileImage Clone()
    {
        var c = new ProfileImage();
        Region02.CopyTo(c.Region02, 0);
        Region03.CopyTo(c.Region03, 0);
        return c;
    }

    // Reassemble an image from a captured/generated transaction: Feature
    // frames come in pairs (half 0, half 2) per 1 KB page, 40 pages of region
    // 02 followed by 12 pages of region 03.
    public static ProfileImage FromSteps(IEnumerable<Step> steps)
    {
        var img = new ProfileImage();
        var frames = steps.Where(s => s.IsFeature).ToList();
        if (frames.Count != (Region02Pages + Region03Pages) * 2)
            throw new InvalidDataException($"Expected {(Region02Pages + Region03Pages) * 2} Feature frames, got {frames.Count}.");

        for (int i = 0; i < frames.Count; i++)
        {
            byte[] f = frames[i].Data;
            int page = i / 2, half = i % 2;
            byte expectedHalfByte = half == 0 ? (byte)0x00 : (byte)0x02;
            if (f.Length != 642 || f[0] != 0x03 || f[1] != 0 || f[2] != 0 || f[3] != expectedHalfByte || f[4] != 0x00 || f[5] != 0x02)
                throw new InvalidDataException($"Feature frame {i} has an unexpected header {Convert.ToHexString(f, 0, 6)}.");

            byte[] region = page < Region02Pages ? img.Region02 : img.Region03;
            int pageInRegion = page < Region02Pages ? page : page - Region02Pages;
            Array.Copy(f, 6, region, pageInRegion * PageSize + half * HalfSize, HalfSize);
        }
        return img;
    }
}

public static class Transaction
{
    // Full write transaction for a profile slot (1-5), in GG's order. Captured from GG for slot 1 (many captures) and
    // slot 2 (profile2-macro-save, profile2-actuation-socd): the slot number appears in the begin command
    // (88 00 <slot> <region>), in every page commit (05 00 <slot> <region> ...), in the eb command (eb <slot>) and in the
    // closing 89 <slot>, which leaves that profile active. The device's acks are the same for every slot. Callers that
    // talk to hardware must wait for the device's ack (mi_01 input: 88 01 after begin, 05 01 after each commit,
    // 01 00 after eb) before sending the next step.
    public static List<Step> Build(ProfileImage img, int slot = 1)
    {
        if (slot is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(slot), "Profile slot must be 1-5.");
        byte s = (byte)slot;
        var steps = new List<Step>();
        steps.Add(Out(0x88, 0x00, s, 0x02));
        AddPages(steps, img.Region02, ProfileImage.Region02Pages, 0x02, s);
        steps.Add(Out(0xEB, s));
        steps.Add(Out(0x88, 0x00, s, 0x03));
        AddPages(steps, img.Region03, ProfileImage.Region03Pages, 0x03, s);
        steps.Add(Out(0x89, s));
        steps.Add(Out(0x41, 0x00));
        steps.Add(Out(0x74, 0x00, 0x01, 0x00));
        steps.Add(Out(0x90, 0x00));
        steps.Add(Out(0x90, 0x01));
        return steps;
    }

    private static void AddPages(List<Step> steps, byte[] region, int pages, byte regionId, byte slot)
    {
        for (int p = 0; p < pages; p++)
        {
            steps.Add(Feature(0x00, region, p * ProfileImage.PageSize));
            steps.Add(Feature(0x02, region, p * ProfileImage.PageSize + ProfileImage.HalfSize));
            var commit = new byte[64];
            commit[0] = 0x05; commit[2] = slot; commit[3] = regionId; commit[5] = (byte)(p * 4); commit[9] = 0x04;
            steps.Add(new Step(false, commit));
        }
    }

    private static Step Feature(byte halfByte, byte[] region, int offset)
    {
        var f = new byte[642];
        f[0] = 0x03; f[3] = halfByte; f[5] = 0x02;
        Array.Copy(region, offset, f, 6, ProfileImage.HalfSize);
        return new Step(true, f);
    }

    private static Step Out(params byte[] head)
    {
        var d = new byte[64];
        head.CopyTo(d, 0);
        return new Step(false, d);
    }

    public static List<Step> Load(string path)
    {
        var steps = new List<Step>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length < 3) continue;
            steps.Add(new Step(line[0] == 'F', Convert.FromHexString(line.AsSpan(2))));
        }
        return steps;
    }
}
