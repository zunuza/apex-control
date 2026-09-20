using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ApexControl.App.Infrastructure;

// A notification-area (tray) icon without WinForms: a hidden window receives the icon's mouse messages.
// Left click / double click raise OpenRequested; right click shows a small native menu (Open / Quit).
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8001;
    private const int WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_NULL = 0;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100;
    private const uint IdOpen = 1, IdQuit = 2;

    private readonly HwndSource _source;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly IntPtr _icon;
    private readonly string _tip;
    private bool _added, _wanted;

    public TrayIcon(IntPtr iconHandle, string tip)
    {
        _icon = iconHandle;
        _tip = tip.Length > 120 ? tip[..120] : tip;
        // A real (hidden) top-level window, so Explorer's "TaskbarCreated" broadcast reaches it and the menu can take focus.
        _source = new HwndSource(new HwndSourceParameters("Apex Control tray") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    public event Action? OpenRequested;
    public event Action? QuitRequested;

    // True if the icon was placed in the tray.
    public bool IsAdded => _added;

    public bool Add()
    {
        _wanted = true;
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        return _added;
    }

    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        var data = Data(NIF_INFO);
        data.szInfoTitle = title.Length > 60 ? title[..60] : title;
        data.szInfo = text.Length > 250 ? text[..250] : text;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _source.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APP_TRAY)
        {
            int mouse = lParam.ToInt32() & 0xFFFF;
            if (mouse is WM_LBUTTONUP or WM_LBUTTONDBLCLK) OpenRequested?.Invoke();
            else if (mouse == WM_RBUTTONUP) ShowMenu();
            handled = true;
        }
        else if (_taskbarCreated != 0 && (uint)msg == _taskbarCreated && _wanted)
        {
            Add();   // Explorer (re)started: put the icon back
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING, IdOpen, "Open Apex Control");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, IdQuit, "Quit Apex Control");
            GetCursorPos(out POINT pt);
            SetForegroundWindow(_source.Handle);
            uint cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _source.Handle, IntPtr.Zero);
            PostMessage(_source.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd == IdOpen) OpenRequested?.Invoke();
            else if (cmd == IdQuit) QuitRequested?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
