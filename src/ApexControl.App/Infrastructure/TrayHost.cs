using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using ApexControl.App.ViewModels;

namespace ApexControl.App.Infrastructure;

// Makes the main window live in the tray: the close button hides it, a tray click shows it again, and only
// "Quit Apex Control" (or Windows logging off) really exits. Quitting is refused while a keyboard operation is running.
public sealed class TrayHost : IDisposable
{
    private readonly Window _window;
    private readonly MainViewModel _vm;
    private readonly TrayIcon _tray;
    private readonly IntPtr _icon;
    private bool _balloonShown;

    private System.Windows.Threading.DispatcherTimer? _retry;
    private int _retries;

    // startHidden: the window has not been shown (Windows start-up). The tray may not exist yet that early in a
    // login, so keep trying for a minute; if it never appears, show the window rather than leave the app unreachable.
    public TrayHost(Window window, MainViewModel vm, bool startHidden = false)
    {
        _window = window;
        _vm = vm;

        _icon = LoadTrayIcon();
        _tray = new TrayIcon(_icon, "Apex Control");
        _tray.OpenRequested += ShowWindow;
        _tray.QuitRequested += () => RequestQuit(shutdown: true);
        _tray.Add();

        _window.Closing += OnClosing;
        if (Application.Current is { } app) app.SessionEnding += (_, _) => Quitting = true;

        if (startHidden && !TrayAdded)
        {
            _retry = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _retry.Tick += (_, _) =>
            {
                if (_tray.Add() || ++_retries >= 30)
                {
                    _retry!.Stop();
                    if (!TrayAdded) ShowWindow();
                }
            };
            _retry.Start();
        }
    }

    public bool TrayAdded => _tray.IsAdded;
    public bool Quitting { get; private set; }

    public void ShowWindow()
    {
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void HideWindow()
    {
        _window.Hide();
        if (!_balloonShown)
        {
            _balloonShown = true;
            _tray.ShowBalloon("Apex Control is still running", "It's in the tray. Click the icon to open it again; right-click for Quit.");
        }
    }

    // Returns false (and says why) if a keyboard operation is in progress.
    public bool RequestQuit(bool shutdown)
    {
        if (_vm.IsBusy)
        {
            _tray.ShowBalloon("Apex Control is busy", "A keyboard operation is still running. Quit once it has finished.");
            return false;
        }
        Quitting = true;
        if (shutdown) Application.Current.Shutdown();
        return true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Quitting || !TrayAdded) return;   // without a tray icon there would be no way back, so just close
        e.Cancel = true;
        HideWindow();
    }

    public void Dispose()
    {
        _retry?.Stop();
        _window.Closing -= OnClosing;
        _tray.Dispose();
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    // The tray wants a small icon handle. app.ico holds PNG-compressed sizes; pick the one nearest the system's small-icon size.
    private static IntPtr LoadTrayIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            using var ms = new MemoryStream();
            info.Stream.CopyTo(ms);
            byte[] ico = ms.ToArray();

            int want = GetSystemMetrics(49);   // SM_CXSMICON
            int count = BitConverter.ToUInt16(ico, 4), bestSize = 0, bestOffset = 0, bestLength = 0, bestDiff = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                int e = 6 + i * 16;
                int size = ico[e] == 0 ? 256 : ico[e];
                int diff = Math.Abs(size - want);
                if (diff < bestDiff) { bestDiff = diff; bestSize = size; bestLength = BitConverter.ToInt32(ico, e + 8); bestOffset = BitConverter.ToInt32(ico, e + 12); }
            }
            byte[] image = new byte[bestLength];
            Array.Copy(ico, bestOffset, image, 0, bestLength);
            return CreateIconFromResourceEx(image, (uint)image.Length, true, 0x00030000, bestSize, bestSize, 0);
        }
        catch (Exception) { return IntPtr.Zero; }   // no icon is better than no app
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, bool icon, uint version, int cx, int cy, uint flags);
}
