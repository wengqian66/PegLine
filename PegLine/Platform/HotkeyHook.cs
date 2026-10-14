using PegLine.Core;

namespace PegLine.Platform;

internal sealed class HotkeyHook : IDisposable
{
    public const int ToggleId = 0x5047;
    public const int ScreenshotId = 0x5048;

    private readonly MessageWindow _window;
    private readonly int _id;
    private bool _registered;

    public HotkeyHook(MessageWindow window, int id)
    {
        _window = window;
        _id = id;
        _window.Message += OnMessage;
    }

    public bool IsRegistered => _registered;

    public event Action? Pressed;

    public bool Register(HotkeyGesture gesture)
    {
        Unregister();
        _registered = NativeMethods.RegisterHotKey(
            _window.Hwnd,
            _id,
            (uint)gesture.Modifiers | NativeMethods.MOD_NOREPEAT,
            (uint)gesture.VirtualKey);
        return _registered;
    }

    public void Unregister()
    {
        NativeMethods.UnregisterHotKey(_window.Hwnd, _id);
        _registered = false;
    }

    public void Dispose()
    {
        Unregister();
        _window.Message -= OnMessage;
    }

    private void OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke();
        }
    }
}
