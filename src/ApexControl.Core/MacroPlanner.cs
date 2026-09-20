namespace ApexControl.Core;

// One requested change to a profile slot's macros (slot 1 unless the planner is given another): remove the macro on `Key`, or set it to a timeline of `Events` (or, for
// convenience, `Steps`, which are expanded to events; invalid steps make the plan refuse rather than throw).
public sealed record MacroEditRequest(bool Remove, byte Key, IReadOnlyList<MacroEvent>? Events, IReadOnlyList<MacroStep>? Steps = null)
{
    public static MacroEditRequest Removal(byte key) => new(true, key, null);
    public static MacroEditRequest ForEvents(byte key, IReadOnlyList<MacroEvent> events) => new(false, key, events);
    public static MacroEditRequest ForSteps(byte key, IReadOnlyList<MacroStep> steps) => new(false, key, null, steps);
    public static MacroEditRequest ForChord(byte key, byte[] chord, ushort holdMs) => ForSteps(key, new[] { new MacroStep(chord, holdMs, 0) });
}

// What an edit would do, worked out entirely in memory (nothing is sent to the keyboard). `Ok` false means the
// edit was refused: `Error` says why in plain words and the other fields may be empty.
public sealed record MacroPlan(
    bool Ok,
    string? Error,
    string Headline,
    string BackupTag,
    ProfileImage Before,
    ProfileImage After,
    ImageDiff? Diff,
    IReadOnlyList<string> MacrosBefore,
    IReadOnlyList<string> MacrosAfter,
    int WriteSteps,
    byte[]? PreWriteCommand = null,
    int Slot = 1,
    byte[]? PreWriteFeature = null);   // a 642-byte Feature frame sent (like GG's live actuation table) just before the save

// The same edit-and-check sequence `ApexMacro bind/unbind` performs, as a reusable step for the desktop app:
// CRC check, apply the edit, then verify with ImageDiffer that only keymap entries, the macro area and the CRC changed.
public static class MacroPlanner
{
    // One step reads "press Q, hold 100 ms"; several read "press Q (hold 50 ms), wait 100 ms, press W (hold 50 ms)".
    public static string DescribeSteps(IReadOnlyList<MacroStep> steps)
    {
        string Keys(MacroStep s) => string.Join("+", s.Keys.Select(KeyNames.Name));
        if (steps.Count == 1) return $"press {Keys(steps[0])}, hold {steps[0].HoldMs} ms";
        var parts = new List<string>();
        for (int i = 0; i < steps.Count; i++)
        {
            parts.Add($"press {Keys(steps[i])} (hold {steps[i].HoldMs} ms)");
            if (i < steps.Count - 1) parts.Add($"wait {steps[i].GapAfterMs} ms");
        }
        return string.Join(", ", parts);
    }

    public static MacroPlan Plan(byte[] region02Read, byte[] region03Read, MacroEditRequest req, int slot = 1)
    {
        ProfileImage before = ProfileWriter.ImageFromDump(region02Read, region03Read);
        string keyName = KeyNames.Name(req.Key);

        MacroPlan Refuse(string why) => new(false, why, "", "", before, before, null, Array.Empty<string>(), Array.Empty<string>(), 0, null, slot);

        if (before.StoredCrc != before.ComputedCrc)
            return Refuse($"The checksum stored in slot {slot} is not valid, so I won't touch it.");

        List<string> macrosBefore;
        try { macrosBefore = MacroEditor.DescribeMacros(before); }
        catch (InvalidOperationException ex) { return Refuse($"Slot {slot}'s macro area isn't in a shape I understand: " + ex.Message); }

        ProfileImage after = before.Clone();
        string headline, tag;
        try
        {
            if (req.Remove)
            {
                MacroEditor.RemoveMacro(after, req.Key);
                headline = $"Remove the macro on {keyName} and restore its normal function";
                tag = $"pre-unbind-{keyName}";
            }
            else
            {
                IReadOnlyList<MacroEvent> events = req.Events ?? MacroEditor.EventsFromSteps(req.Steps ?? Array.Empty<MacroStep>());
                string? bad = MacroEditor.ValidateEvents(events);
                if (bad is not null) return Refuse(bad);
                bool replacing = MacroEditor.IsBound(after, req.Key);
                if (replacing) MacroEditor.ReplaceEventMacro(after, req.Key, events);
                else MacroEditor.AddEventMacro(after, req.Key, events);
                headline = $"{(replacing ? "Replace the macro on" : "Bind")} {keyName}: {MacroEditor.DescribeEvents(events)}";
                tag = $"pre-bind-{keyName}";
            }
        }
        catch (InvalidOperationException ex) { return Refuse(ex.Message); }
        catch (ArgumentException ex) { return Refuse(ex.Message); }

        ImageDiff diff = ImageDiffer.Compute(before, after, region02Read);
        if (!diff.Ok) return Refuse("The change would touch something unexpected, so I refused it. " + string.Join(" ", diff.Problems));

        List<string> macrosAfter = MacroEditor.DescribeMacros(after);
        int steps = ProfileWriter.BuildWritePlan(after, slot).Count;
        return new MacroPlan(true, null, headline, tag, before, after, diff, macrosBefore, macrosAfter, steps, null, slot);
    }
}
