namespace ApexControl.Core;

// How long-running operations report back without knowing about consoles or windows.
public interface IOperationSink
{
    void Info(string message);
    void Progress(int done, int total, string? label = null);
}

public sealed class NullSink : IOperationSink
{
    public static readonly NullSink Instance = new();
    public void Info(string message) { }
    public void Progress(int done, int total, string? label = null) { }
}

// A problem talking to the keyboard whose message is meant to be shown to the user as-is.
public sealed class KeyboardException : Exception
{
    public KeyboardException(string message) : base(message) { }
}

// Is SteelSeries software holding the keyboard? (It must be fully quit for our tools to open it.)
public static class GgProcess
{
    public static bool IsRunning(out string[] names)
    {
        names = System.Diagnostics.Process.GetProcesses()
            .Select(p => p.ProcessName)
            .Where(n => n.StartsWith("SteelSeries", StringComparison.OrdinalIgnoreCase))
            .Distinct().ToArray();
        return names.Length > 0;
    }
}