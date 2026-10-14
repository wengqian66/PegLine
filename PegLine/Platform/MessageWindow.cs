using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PegLine.Platform;

/// <summary>
/// Hidden top-level window that receives tray clicks, the global hotkey, and clipboard notifications.
/// A message-only HWND cannot reliably become the foreground window, which the tray menu needs.
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private const string ClassName = "PegLine.MessageWindow";
    private readonly NativeMethods.WndProc _proc;
    private bool _disposed;

    public MessageWindow()
    {
        _proc = Proc;
        var instance = NativeMethods.GetModuleHandle(null);
        var windowClass = new NativeMethods.WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = instance,
            lpszClassName = ClassName,
        };

        if (NativeMethods.RegisterClassW(ref windowClass) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ERROR_CLASS_ALREADY_EXISTS)
            {
                throw new Win32Exception(error, "RegisterClass failed for PegLine.");
            }
        }

        Hwnd = NativeMethods.CreateWindowExW(
            NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE,
            ClassName,
            "PegLine",
            NativeMethods.WS_POPUP,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (Hwnd == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed for PegLine.");
        }
    }

    public IntPtr Hwnd { get; }

    public event Action<uint, IntPtr, IntPtr>? Message;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(Hwnd);
        }
    }

    private IntPtr Proc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        Message?.Invoke(msg, wParam, lParam);
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }
}
