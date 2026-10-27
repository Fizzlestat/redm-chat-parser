using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace RedMChatParser;

internal sealed class TrayIcon : IDisposable
{
    private const int WM_TRAY = 0x8001;
    private const int NIM_ADD = 0;
    private const int NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x1;
    private const int NIF_ICON = 0x2;
    private const int NIF_TIP = 0x4;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    private readonly Window _window;
    private IntPtr _hwnd;
    private bool _added;
    private bool _wantVisible;
    private HwndSource? _source;

    public TrayIcon(Window window)
    {
        _window = window;
    }

    public event Action? OpenRequested;

    public event Action? ExitRequested;

    public bool Visible
    {
        get => _added;
        set
        {
            _wantVisible = value;
            Apply();
        }
    }

    public void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        Apply();
    }

    public void Dispose()
    {
        _source?.RemoveHook(WndProc);
        if (_added && _hwnd != IntPtr.Zero)
        {
            NOTIFYICONDATA data = CreateData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
    }

    private void Apply()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        NOTIFYICONDATA data = CreateData();
        if (_wantVisible && !_added)
            _added = Shell_NotifyIcon(NIM_ADD, ref data);
        else if (!_wantVisible && _added)
        {
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
    }

    private NOTIFYICONDATA CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_TRAY,
        hIcon = AppIcon.Handle,
        szTip = "RedM Chat Parser"
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_TRAY)
            return IntPtr.Zero;

        int click = lParam.ToInt32() & 0xFFFF;
        if (click == WM_LBUTTONUP)
        {
            OpenRequested?.Invoke();
            handled = true;
        }
        else if (click == WM_RBUTTONUP)
        {
            SetForegroundWindow(_hwnd);
            ContextMenu menu = new ContextMenu
            {
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
                PlacementTarget = _window
            };
            MenuItem open = new MenuItem { Header = Locale.Get("trayOpen") };
            MenuItem exit = new MenuItem { Header = Locale.Get("trayExit") };
            open.Click += (_, _) => OpenRequested?.Invoke();
            exit.Click += (_, _) => ExitRequested?.Invoke();
            menu.Items.Add(open);
            menu.Items.Add(exit);
            menu.IsOpen = true;
            handled = true;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }
}
