namespace PegLine.Platform;

internal sealed class ClipboardMonitor : IDisposable
{
    private readonly MessageWindow _window;
    private bool _listening;

    public ClipboardMonitor(MessageWindow window)
    {
        _window = window;
        _window.Message += OnMessage;
    }

    public event Action? ClipboardChanged;

    public void SetEnabled(bool enabled)
    {
        if (enabled == _listening)
        {
            return;
        }

        if (enabled)
        {
            _listening = NativeMethods.AddClipboardFormatListener(_window.Hwnd);
        }
        else
        {
            NativeMethods.RemoveClipboardFormatListener(_window.Hwnd);
            _listening = false;
        }
    }

    public void Dispose()
    {
        SetEnabled(false);
        _window.Message -= OnMessage;
    }

    private void OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            ClipboardChanged?.Invoke();
        }
    }
}
