using ApexControl.Core;

namespace ApexMacro;

// Prints Core's progress and messages to the console.
internal sealed class ConsoleSink : IOperationSink
{
    public void Info(string message) => Console.WriteLine(message);

    public void Progress(int done, int total, string? label = null)
    {
        switch (label)
        {
            case "read-done": Console.WriteLine($"\r  {done}/{total} read steps - done.      "); break;
            case "done": Console.WriteLine($"\r  {done}/{total} steps - done.      "); break;
            default: Console.Write($"\r  {done}/{total} steps"); break;
        }
    }
}
