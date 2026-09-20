namespace ApexControl.Core;

public sealed record DumpResult(Dictionary<(int Slot, int Region), byte[]> Data, byte[]? Header, HashSet<string> Firmware);

// READ-ONLY: the plan contains only the commands GG itself sends to read
// (90 / f4 / 6c / 85 / 83 / 69 / 89 / 41). It never sends a write command.
public static class ProfileReader
{
    // Runs GG's session start + reads for the given slots. On failure, tries to close the session (if it had
    // opened) and rethrows. On success the session is left open: call CloseSession.
    public static DumpResult ReadSlots(KeyboardLink link, int[] slots, IOperationSink? sink = null)
    {
        sink ??= NullSink.Instance;
        List<PlanItem> plan = ReadSession.BuildPlan(slots);
        List<PlanItem> endSeq = ReadSession.BuildEndSequence();
        List<PlanItem> readPhase = plan.Take(plan.Count - endSeq.Count).ToList();

        var data = new Dictionary<(int Slot, int Region), byte[]>();
        foreach (int s in slots)
        {
            data[(s, 2)] = new byte[ReadSession.Region02Pages * ProfileImage.PageSize];
            data[(s, 3)] = new byte[ReadSession.Region03Pages * ProfileImage.PageSize];
        }
        byte[]? header = null;
        var firmware = new HashSet<string>();

        void OnFetch(PlanItem it, byte[] resp)
        {
            if (it.Page < 0) header = resp.Take(it.FetchLength).ToArray();
            else Array.Copy(resp, 0, data[(it.Slot, it.Region)], it.Page * ProfileImage.PageSize + (it.Half == 0 ? 0 : ProfileImage.HalfSize), it.FetchLength);
        }

        sink.Info($"Reading slot(s) {string.Join(",", slots)} ({readPhase.Count} steps)...");
        int done = 0;
        try
        {
            foreach (PlanItem item in readPhase)
            {
                Runner.Execute(link, item, OnFetch, firmware, sink);
                done++;
                if (done % 40 == 0) sink.Progress(done, readPhase.Count, "steps");
            }
            sink.Progress(done, readPhase.Count, "read-done");
        }
        catch (Exception ex)
        {
            sink.Info($"ERROR at step {done}/{readPhase.Count}: {ex.Message}");
            // The f4/6c setup (steps 2-3) is what opens a session; before that there is nothing to close.
            if (done >= 4) CloseSession(link, 1, sink);
            else sink.Info("Failed before the session was opened, so nothing else was sent.");
            throw;
        }
        return new DumpResult(data, header, firmware);
    }

    // GG's end-of-session commands. `activeSlot` is the profile left active (89 <slot>): the keyboard has no
    // report of its active profile, so the caller chooses; 1 is what GG's own capture did with Config 1 active.
    public static bool CloseSession(KeyboardLink link, int activeSlot = 1, IOperationSink? sink = null)
    {
        sink ??= NullSink.Instance;
        sink.Info($"Closing the session (GG's end-of-session commands, with GG's timing; leaves Config {activeSlot} active)...");
        try
        {
            foreach (PlanItem end in ReadSession.BuildEndSequence(activeSlot)) Runner.Execute(link, end, sink: sink);
            sink.Info("  done.");
            return true;
        }
        catch (Exception ex)
        {
            sink.Info($"  warning: close-out did not complete ({ex.Message}). Data already read is unaffected.");
            return false;
        }
    }
}