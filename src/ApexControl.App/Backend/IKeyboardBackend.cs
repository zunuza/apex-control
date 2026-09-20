using ApexControl.Core;

namespace ApexControl.App.Backend;

public sealed record KeyboardState(bool Present, bool GgRunning, string[] GgNames);

public sealed record OpResult(bool Ok, string Message);

// Whether this app starts with Windows. DisabledInWindows: the entry exists but Windows' Startup settings have it switched off.
public sealed record AutoStartState(bool Enabled, bool DisabledInWindows, string? Command);

// What this app last sent as actuation: the keyboard can't be asked for its actuation, so this is the only record.
public sealed record ActuationDraft(double GlobalMm, IReadOnlyDictionary<byte, double> PerKeyMm, DateTime SentAt);

// One slot exactly as read from the keyboard (region 02 as read: 13312 bytes; region 03: 12288 bytes).
public sealed record SlotReadResult(bool Ok, string Message, byte[]? Region02, byte[]? Region03);

// Everything the window needs from the keyboard. The real implementation uses ApexControl.Core; the
// preview implementation returns canned data so the UI can be rendered without hardware.
public interface IKeyboardBackend
{
    string BackupsRoot { get; }

    KeyboardState GetState();
    IReadOnlyList<BackupInfo> ListBackups();

    // Summary of a profile slot from the newest backup that contains it (null if never backed up).
    SlotSummary? LatestSummary(int slot);

    // Reads all 5 profiles into a new backup folder; the close-out leaves `activeSlot` as the active profile.
    Task<OpResult> BackupAllAsync(IOperationSink sink, int activeSlot = 1);
    Task<OpResult> SwitchProfileAsync(int slot, IOperationSink sink);
    // Writes the saved copy of one profile slot (from the backup folder) back to that same slot.
    Task<OpResult> RestoreSlotAsync(string backupDir, int slot, IOperationSink sink);

    // Read-only: reads one profile slot from the keyboard (leaves that Config active, like GG's own read closes out).
    Task<SlotReadResult> ReadSlotAsync(int slot, IOperationSink sink);

    // Writes an already-planned edit (a macro change or SOCD settings) to the plan's slot. It re-reads that slot first and refuses if the keyboard no longer
    // matches the image the plan was made from; saves a pre-write backup; writes; reads back to verify. The slot is left as the active profile.
    Task<OpResult> ApplyProfilePlanAsync(MacroPlan plan, IOperationSink sink);

    // Sends the actuation table (global mm plus per-key overrides; every value must be a known-good one) and, on
    // success, remembers it. Live on the keyboard; no profile data is read or written.
    // The keyboard cannot report its actuation, so what was sent is remembered per profile (`slot`) for the tab to show.
    Task<OpResult> ApplyActuationAsync(double globalMm, IReadOnlyDictionary<byte, double> perKeyMm, IOperationSink sink, int slot = 1);

    ActuationDraft? LoadLastActuation(int slot = 1);

    // Records a table that was sent as part of saving a profile (the plan flow sends the live frame itself).
    void RememberActuation(int slot, ActuationDraft draft);

    // Profile nicknames are kept by this app only (the keyboard's own profile names are not touched).
    // Compatibility check: what Windows reports for each SteelSeries device (sends nothing), and an opt-in read-only read of
    // Config 1 from a device with the TKL's command interface (the same commands GG sends at startup; writes nothing).
    IReadOnlyList<SteelSeriesDevice> ScanDevices();
    Task<ReadCheckResult> ReadCheckAsync(SteelSeriesDevice device, IOperationSink sink);

    AutoStartState GetAutoStart();
    void SetAutoStart(bool enabled);      // throws with a plain-words message if it can't

    IReadOnlyDictionary<int, string> LoadProfileNames();
    void SaveProfileNames(IReadOnlyDictionary<int, string> names);
}
