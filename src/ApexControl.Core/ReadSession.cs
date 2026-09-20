// The exact host-side sequence GG runs at startup to read all 5 profile slots
// (docs/PROTOCOL_NOTES.md, "READ protocol"). This file only BUILDS the plan;
// `ApexMacro verify-dump` proves it is byte-identical to GG's captured
// sequence, and Dumper.cs is what executes it against hardware.

namespace ApexControl.Core;

public enum Reply { None, Version, F4Ack, ReadAck, BeginAck, CommitAck, EbAck }

// Slot/Region/Page/Half are only meaningful when FetchAfter is set: after
// sending this Feature request, do a GET_REPORT and store the returned bytes.
public sealed record PlanItem(Step Frame, Reply Expect, bool FetchAfter = false, int Slot = 0, int Region = 0, int Page = 0, int Half = 0, int FetchLength = 0, int DelayBeforeMs = 0);

public static class ReadSession
{
    public const int Region02Pages = 13;   // 0x00..0x30
    public const int Region03Pages = 12;   // 0x00..0x2c
    public const int Slots = 5;

    public static List<PlanItem> BuildPlan(int[]? slots = null, int activeSlot = 1)
    {
        slots ??= Enumerable.Range(1, Slots).ToArray();
        var plan = new List<PlanItem>();

        // Session start: version queries, f4/6c setup, version queries again.
        plan.Add(Cmd(Reply.Version, 0x90));
        plan.Add(Cmd(Reply.Version, 0x90, 0x01));
        plan.Add(Cmd(Reply.F4Ack, 0xF4));
        plan.Add(Cmd(Reply.None, 0x6C, 0x00, 0x01));
        plan.Add(Cmd(Reply.Version, 0x90));
        plan.Add(Cmd(Reply.Version, 0x90, 0x01));

        // 2-byte header read (returns 0f 00).
        plan.Add(new PlanItem(ReadCmd(slot: 1, region: 2, page: 0, length: 2), Reply.ReadAck));
        plan.Add(new PlanItem(FeatureRequest(half: 0, length: 2), Reply.None, FetchAfter: true, Slot: 1, Region: 2, Page: -1, Half: 0, FetchLength: 2));

        // Region 02 for slots 1..5, then region 03 for slots 1..5, page by page.
        foreach (int region in new[] { 2, 3 })
        {
            int pages = region == 2 ? Region02Pages : Region03Pages;
            foreach (int slot in slots)
            {
                for (int page = 0; page < pages; page++)
                {
                    plan.Add(new PlanItem(ReadCmd(slot, region, page, length: 1024), Reply.ReadAck));
                    foreach (int half in new[] { 0, 2 })
                        plan.Add(new PlanItem(FeatureRequest(half, length: 512), Reply.None, FetchAfter: true, Slot: slot, Region: region, Page: page, Half: half, FetchLength: 512));
                }
            }
        }

        // Session end.
        plan.AddRange(BuildEndSequence(activeSlot));
        return plan;
    }

    // Just the tail, used to leave the device in a normal state if a read fails mid-way.
    // GG's captured timing: ~0.58 s of quiet after the last read before 69, and
    // ~0.31 s between 89 01 and 41 (the keyboard is busy then and ignores the
    // version query if it arrives too soon).
    //
    // 89 <slot> selects the ACTIVE profile (see profile-switch captures): GG re-selects whichever profile
    // it considers active here. The keyboard has no report of its active profile that we know of, so the
    // caller chooses; the default, 1, is what GG's capture with Config 1 active did.
    public static List<PlanItem> BuildEndSequence(int activeSlot = 1) => new()
    {
        Cmd(Reply.None, 0x69) with { DelayBeforeMs = 500 },
        Cmd(Reply.None, 0x89, (byte)activeSlot),
        Cmd(Reply.None, 0x41) with { DelayBeforeMs = 300 },
        Cmd(Reply.Version, 0x90),
        Cmd(Reply.Version, 0x90, 0x01),
    };

    private static PlanItem Cmd(Reply expect, params byte[] head)
    {
        var d = new byte[64];
        head.CopyTo(d, 0);
        return new PlanItem(new Step(false, d), expect);
    }

    // 85 00 <slot> <region> 00 <page*4> 00 00 <len LE16>
    private static Step ReadCmd(int slot, int region, int page, int length)
    {
        var d = new byte[64];
        d[0] = 0x85; d[2] = (byte)slot; d[3] = (byte)region; d[5] = (byte)(page * 4);
        d[8] = (byte)(length & 0xFF); d[9] = (byte)(length >> 8);
        return new Step(false, d);
    }

    // 83 00 00 <half> <len LE16>: "send me `length` bytes at half-offset".
    private static Step FeatureRequest(int half, int length)
    {
        var f = new byte[642];
        f[0] = 0x83; f[3] = (byte)half; f[4] = (byte)(length & 0xFF); f[5] = (byte)(length >> 8);
        return new Step(true, f);
    }
}

