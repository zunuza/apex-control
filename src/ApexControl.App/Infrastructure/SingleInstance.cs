namespace ApexControl.App.Infrastructure;

// One copy of the app at a time (two would fight over the keyboard). A second launch pokes the first one to show
// its window, then exits.
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly bool _owner;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex, EventWaitHandle show, bool owner)
    {
        _mutex = mutex;
        _show = show;
        _owner = owner;
    }

    // True if this process is the first (and so the only) instance. `name` distinguishes independent sets of instances.
    public static SingleInstance Acquire(string name, out bool isFirst)
    {
        var mutex = new Mutex(true, name + ".mutex", out isFirst);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".show");
        return new SingleInstance(mutex, show, isFirst);
    }

    // First instance: call `onShow` (on a worker thread) whenever another launch asks for the window.
    public void ListenForSecondLaunch(Action onShow)
    {
        if (!_owner) return;
        var thread = new Thread(() =>
        {
            var handles = new[] { _show, _stop.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0) onShow();
        }) { IsBackground = true, Name = "Apex Control single-instance listener" };
        thread.Start();
    }

    // Second instance: ask the first one to show itself.
    public void SignalFirst() => _show.Set();

    public void Dispose()
    {
        _stop.Cancel();
        if (_owner) { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } }
        _mutex.Dispose();
        _show.Dispose();
    }
}
