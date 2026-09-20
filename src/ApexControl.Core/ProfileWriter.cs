namespace ApexControl.Core;

public sealed record WriteResult(bool Success, int StepsDone, int TotalSteps, string? Error);

public sealed record VerifyResult(bool ReadFailed, bool Ok, SlotSummary? Summary, int Region02Diffs, int FirstRegion02Diff, int Region03Diffs);

// Writing a profile slot. Everything that talks to the keyboard here mirrors GG's own macro-save transaction
// (verified offline by `ApexMacro verify`); see docs/PROTOCOL_NOTES.md.
public static class ProfileWriter
{
    // Region 02 bytes at and beyond this offset are not covered by the CRC. In every dump taken before our own
    // first write they were 0xFF (erased flash); we always write 0xFF there, so a write changes only the bytes
    // that were meant to change. (GG's frames carry zeros there, and our first write, which copied that, left
    // zeros in flash - see PROTOCOL_NOTES.md.)
    public const int SafeEnd = ProfileImage.CrcOffset + 4;
    public const int ReadLen = ReadSession.Region02Pages * ProfileImage.PageSize;   // 13312: the part of region 02 we can read

    public static ProfileImage ImageFromDump(byte[] region02Read, byte[] region03Read)
    {
        if (region02Read.Length != ReadSession.Region02Pages * ProfileImage.PageSize || region03Read.Length != ReadSession.Region03Pages * ProfileImage.PageSize)
            throw new InvalidDataException("Unexpected dump sizes.");
        var img = new ProfileImage();
        Array.Copy(region02Read, img.Region02, SafeEnd);                    // pages 0..12 up to and including the CRC
        Array.Fill(img.Region02, (byte)0xFF, SafeEnd, ReadLen - SafeEnd);   // the unused tail: erased state
        Array.Copy(region03Read, img.Region03, region03Read.Length);
        return img;
    }

    // GG's macro-save transaction as executable steps, with the acks and pauses seen in capture.
    public static List<PlanItem> BuildWritePlan(ProfileImage img, int slot = 1)
    {
        var plan = new List<PlanItem>();
        foreach (Step s in Transaction.Build(img, slot))
        {
            if (s.IsFeature) { plan.Add(new PlanItem(s, Reply.None)); continue; }
            plan.Add(s.Data[0] switch
            {
                0x88 => new PlanItem(s, Reply.BeginAck),
                0x05 => new PlanItem(s, Reply.CommitAck),
                0xEB => new PlanItem(s, Reply.EbAck),
                0x41 => new PlanItem(s, Reply.None, DelayBeforeMs: 300),      // 89 01 -> 41 gap in GG's capture
                0x90 when s.Data[1] == 0x00 => new PlanItem(s, Reply.Version, DelayBeforeMs: 250),
                0x90 => new PlanItem(s, Reply.Version),
                _ => new PlanItem(s, Reply.None),
            });
        }
        return plan;
    }

    // Runs the write transaction. Stops immediately on any missing ack (does NOT send the finalize commands
    // after a failure), so the caller can point the user at `restore`.
    public static WriteResult Execute(KeyboardLink link, ProfileImage img, IOperationSink? sink = null, int slot = 1)
    {
        sink ??= NullSink.Instance;
        List<PlanItem> plan = BuildWritePlan(img, slot);
        sink.Info($"Writing slot {slot} ({plan.Count} steps)...");
        int done = 0;
        try
        {
            foreach (PlanItem item in plan)
            {
                Runner.Execute(link, item, sink: sink);
                done++;
                if (done % 10 == 0) sink.Progress(done, plan.Count, "steps");
            }
            sink.Progress(done, plan.Count, "done");
            return new WriteResult(true, done, plan.Count, null);
        }
        catch (Exception ex)
        {
            return new WriteResult(false, done, plan.Count, ex.Message);
        }
    }

    // Reads the slot back and checks it equals what we meant to write (all 13 KB of region 02 incl. the tail, and region 03).
    // The close-out leaves that slot active, as GG's own save does.
    public static VerifyResult VerifyReadBack(KeyboardLink link, ProfileImage expected, IOperationSink? sink = null, int slot = 1)
    {
        DumpResult res;
        try { res = ProfileReader.ReadSlots(link, new[] { slot }, sink); }
        catch (Exception) { return new VerifyResult(true, false, null, 0, -1, 0); }
        ProfileReader.CloseSession(link, slot, sink);

        byte[] r2 = res.Data[(slot, 2)], r3 = res.Data[(slot, 3)];
        int diff2 = 0, first2 = -1;
        for (int i = 0; i < ReadLen; i++)
            if (r2[i] != expected.Region02[i]) { diff2++; if (first2 < 0) first2 = i; }
        int diff3 = 0;
        for (int i = 0; i < r3.Length; i++) if (r3[i] != expected.Region03[i]) diff3++;

        SlotSummary summary = ProfileInfo.Summarize(slot, r2);
        bool ok = diff2 == 0 && diff3 == 0 && summary.CrcOk;
        return new VerifyResult(false, ok, summary, diff2, first2, diff3);
    }
}