namespace ApexControl.Core;

// Switching the active profile (Config 1..5). Decoded from GG's sidebar double-click
// (docs/PROTOCOL_NOTES.md, "Profile switching"): only Output commands, no profile data.
//   [69]  89 <slot>   (~0.3 s)   41   90   90 01
// GG sends the leading 69 exactly when slot 1 is the source or the target of the switch, and
// also in every read-session close-out (69, 89 <active slot>, ...). The keyboard offers no
// report of which slot is currently active that we know of, so we cannot know the source;
// we always send 69, which GG's own captures show is accepted for targets 1, 2, 3 and 4.
public static class ProfileSwitch
{
    public const int Slots = 5;

    public static List<PlanItem> BuildPlan(int slot, bool leading69 = true)
    {
        if (slot is < 1 or > Slots) throw new ArgumentOutOfRangeException(nameof(slot), "Profile must be 1-5.");
        var plan = new List<PlanItem>();
        if (leading69) plan.Add(Cmd(Reply.None, 0x69));
        plan.Add(Cmd(Reply.None, 0x89, (byte)slot));
        plan.Add(Cmd(Reply.None, 0x41) with { DelayBeforeMs = 300 });   // GG waits ~0.31 s after 89 before 41
        plan.Add(Cmd(Reply.Version, 0x90));
        plan.Add(Cmd(Reply.Version, 0x90, 0x01));
        return plan;
    }

    private static PlanItem Cmd(Reply expect, params byte[] head)
    {
        var d = new byte[64];
        head.CopyTo(d, 0);
        return new PlanItem(new Step(false, d), expect);
    }

    // profile-switch-observed.txt: "# label" line, then "O <hex>" lines, blank line between blocks.
    public static List<(string Label, List<Step> Steps)> LoadObserved(string path)
    {
        var result = new List<(string, List<Step>)>();
        string? label = null; var steps = new List<Step>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("# ")) { label = line[2..]; steps = new List<Step>(); }
            else if (line.StartsWith("O ")) steps.Add(new Step(false, Convert.FromHexString(line.AsSpan(2))));
            else if (line.Length == 0 && label is not null) { result.Add((label, steps)); label = null; }
        }
        if (label is not null) result.Add((label, steps));
        return result;
    }
}
