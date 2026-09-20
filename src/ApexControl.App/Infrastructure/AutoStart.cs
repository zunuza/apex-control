using Microsoft.Win32;

namespace ApexControl.App.Infrastructure;

// "Start with Windows" for the current user only (no admin rights, nothing machine-wide): a value in
// HKCU\...\Run that launches the exe with --tray, so it comes up hidden in the notification area.
// If the user switches it off in Windows' own Startup settings, Windows records that under StartupApproved;
// this class honours it and clears it again when the app's own switch is turned back on.
public sealed class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private readonly string _name;
    private readonly string? _legacyName;

    // "SSAP" was this app's name for a short while; an entry made under that name still counts and is cleaned up.
    public AutoStart(string valueName = "ApexControl", string? legacyName = "SSAP")
    {
        _name = valueName;
        _legacyName = legacyName;
    }

    public static string BuildCommand(string exePath) => $"\"{exePath}\" --tray";

    // The command stored in the Run key, or null if there is none.
    public string? Command
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(_name) as string ?? (_legacyName is null ? null : key?.GetValue(_legacyName) as string);
        }
    }

    // Windows' Startup settings (or Task Manager) have this entry switched off.
    public bool DisabledInWindows
    {
        get
        {
            if (Command is null) return false;
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            string name;
            using (RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey))
                name = run?.GetValue(_name) is not null || _legacyName is null ? _name : _legacyName;
            return key?.GetValue(name) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;   // 02/06 = enabled, 03 = disabled
        }
    }

    public bool IsEnabled => Command is not null && !DisabledInWindows;

    public void Enable(string exePath)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            key.SetValue(_name, BuildCommand(exePath), RegistryValueKind.String);
            if (_legacyName is not null) key.DeleteValue(_legacyName, throwOnMissingValue: false);
        }
        ClearApproval();
    }

    public void Disable()
    {
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            key?.DeleteValue(_name, throwOnMissingValue: false);
            if (_legacyName is not null) key?.DeleteValue(_legacyName, throwOnMissingValue: false);
        }
        ClearApproval();
    }

    private void ClearApproval()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        key?.DeleteValue(_name, throwOnMissingValue: false);
        if (_legacyName is not null) key?.DeleteValue(_legacyName, throwOnMissingValue: false);
    }

    // Test aid: pretend the user switched it off in Windows' Startup settings.
    internal void SimulateDisabledInWindows()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(ApprovedKey);
        key.SetValue(_name, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
    }
}
