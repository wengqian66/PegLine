using System.Runtime.InteropServices;

namespace PegLine.Platform;

internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private readonly MessageWindow _window;
    private readonly Func<bool> _ropeVisible;
    private readonly Func<string> _hotkeyLabel;
    private readonly Action _toggle;
    private readonly Action _openSettings;
    private readonly Action _quit;
    private IntPtr _icon;
    private bool _added;

    public TrayIcon(
        MessageWindow window,
        Func<bool> ropeVisible,
        Func<string> hotkeyLabel,
        Action toggle,
        Action openSettings,
        Action quit)
    {
        _window = window;
        _ropeVisible = ropeVisible;
        _hotkeyLabel = hotkeyLabel;
        _toggle = toggle;
        _openSettings = openSettings;
        _quit = quit;
        _window.Message += OnMessage;
    }

    public void Add()
    {
        _icon = LoadIcon();
        var data = CreateData();
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
        _added = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
        if (!_added)
        {
            throw new InvalidOperationException("Shell_NotifyIcon failed. Error " + Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        _window.Message -= OnMessage;
        if (_added)
        {
            var data = CreateData();
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    private void OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != NativeMethods.WM_TRAY || wParam.ToInt32() != IconId)
        {
            return;
        }

        var mouse = (uint)(lParam.ToInt32() & 0xFFFF);
        if (mouse is NativeMethods.WM_LBUTTONUP or NativeMethods.WM_LBUTTONDBLCLK)
        {
            _toggle();
        }
        else if (mouse is NativeMethods.WM_RBUTTONUP or NativeMethods.WM_CONTEXTMENU)
        {
            ShowMenu();
        }
    }

    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var visible = _ropeVisible();
            var hotkey = _hotkeyLabel();
            var toggle = visible ? $"隐藏绳子\t{hotkey}" : $"显示绳子\t{hotkey}";
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, 1, toggle);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, 2, "设置…");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, 0, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, 3, "退出");

            NativeMethods.GetCursorPos(out var point);
            NativeMethods.SetForegroundWindow(_window.Hwnd);
            var command = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_NONOTIFY,
                point.X,
                point.Y,
                0,
                _window.Hwnd,
                IntPtr.Zero);
            NativeMethods.PostMessage(_window.Hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case 1:
                    _toggle();
                    break;
                case 2:
                    _openSettings();
                    break;
                case 3:
                    _quit();
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private NativeMethods.NOTIFYICONDATAW CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
        hWnd = _window.Hwnd,
        uID = IconId,
        uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
        uCallbackMessage = NativeMethods.WM_TRAY,
        hIcon = _icon,
        szTip = "挂绳 PegLine",
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static IntPtr LoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "PegLine.ico");
        if (File.Exists(path))
        {
            var icon = NativeMethods.LoadImage(IntPtr.Zero, path, NativeMethods.IMAGE_ICON, 0, 0, NativeMethods.LR_LOADFROMFILE | NativeMethods.LR_DEFAULTSIZE);
            if (icon != IntPtr.Zero)
            {
                return icon;
            }
        }

        return NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }
}
