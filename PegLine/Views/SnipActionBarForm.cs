using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PegLine.Hosting;
using PegLine.ViewModels;

namespace PegLine.Views;

/// <summary>
/// Post-screenshot mini action bar: 复制 / 存 Saved / 丢弃 / 关闭.
/// </summary>
internal sealed class SnipActionBarForm : IDisposable
{
    private const string ClassName = "PegLine.SnipActionBar";
    private const int AnimTimerId = 1;
    private const int AutoDismissMs = 6000;
    private const int BarH = 52;
    private const int Pad = 10;

    private readonly AppHost _host;
    private readonly WndProcKeep _procKeep;
    private readonly List<HitZone> _hits = [];
    private IntPtr _hwnd;
    private bool _disposed;
    private bool _visible;
    private PegView? _peg;
    private long _dismissAt;
    private float _reveal;
    private int _revealDir;
    private string? _hoverKind;

    private sealed record HitZone(Rectangle Bounds, string Kind);

    private delegate IntPtr WndProcKeep(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public SnipActionBarForm(AppHost host)
    {
        _host = host;
        _procKeep = WndProc;
        RegisterClass();
        _hwnd = Native.CreateWindowExW(
            Native.WS_EX_LAYERED | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
            ClassName,
            "截图操作",
            Native.WS_POPUP,
            0,
            0,
            420,
            BarH + 20,
            IntPtr.Zero,
            IntPtr.Zero,
            Native.GetModuleHandle(null),
            IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx action bar failed");
        }

        Native.SetTimer(_hwnd, AnimTimerId, 16, IntPtr.Zero);
    }

    public bool IsOpen => _visible && _peg is not null && _reveal > 0.05f;

    public void ShowFor(PegView peg)
    {
        _peg = peg;
        _dismissAt = Environment.TickCount64 + AutoDismissMs;
        _revealDir = 1;
        if (_reveal <= 0.01f)
        {
            _reveal = 0.08f;
        }

        PlaceNearBottom();
        Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _visible = true;
        _host.NotifyActionBarChanged();
        Render();
    }

    public void Dismiss()
    {
        _revealDir = -1;
        _peg = null;
        _host.NotifyActionBarChanged();
    }

    public void Close()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            Native.KillTimer(_hwnd, AnimTimerId);
            Native.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private void PlaceNearBottom()
    {
        var screen = ScreenBounds();
        const int width = 420;
        const int height = BarH + 24;
        var x = screen.X + (screen.Width - width) / 2;
        var y = screen.Bottom - height - 48;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, width, height, Native.SWP_NOACTIVATE);
    }

    private void Render()
    {
        if (_hwnd == IntPtr.Zero || _disposed)
        {
            return;
        }

        Native.GetWindowRect(_hwnd, out var rect);
        var width = Math.Max(200, rect.right - rect.left);
        var height = Math.Max(40, rect.bottom - rect.top);

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            _hits.Clear();

            if (_peg is null && _reveal <= 0.01f)
            {
                PushLayer(bmp);
                return;
            }

            var slide = g.Save();
            g.TranslateTransform(0, (1f - _reveal) * 18f);
            DrawBar(g, width, height);
            g.Restore(slide);
        }

        if (_reveal < 0.999f)
        {
            using var faded = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
            using var fg = Graphics.FromImage(faded);
            var attrs = new ImageAttributes();
            attrs.SetColorMatrix(new ColorMatrix { Matrix33 = Math.Clamp(_reveal, 0f, 1f) });
            fg.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attrs);
            attrs.Dispose();
            PushLayer(faded);
        }
        else
        {
            PushLayer(bmp);
        }
    }

    private void DrawBar(Graphics g, int width, int height)
    {
        var bar = new Rectangle(12, 8, width - 24, BarH);
        using (var drop = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
        {
            FillRound(g, new Rectangle(bar.X + 2, bar.Y + 3, bar.Width, bar.Height), 14, drop);
        }

        using var bg = new SolidBrush(Color.FromArgb(245, 252, 248, 240));
        using var edge = new Pen(Color.FromArgb(200, 200, 180, 150), 1.2f);
        FillRound(g, bar, 14, bg);
        using (var rim = RoundPath(bar, 14))
        {
            g.DrawPath(edge, rim);
        }

        var labels = new (string Kind, string Text)[]
        {
            ("copy", "复制"),
            ("save", "存 Saved"),
            ("discard", "丢弃"),
            ("close", "关闭"),
        };

        using var font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
        var x = bar.X + Pad;
        var btnH = 32;
        var y = bar.Y + (BarH - btnH) / 2;
        foreach (var (kind, text) in labels)
        {
            var tw = g.MeasureString(text, font).Width;
            var btnW = (int)tw + 22;
            var btn = new Rectangle(x, y, btnW, btnH);
            var hover = string.Equals(_hoverKind, kind, StringComparison.Ordinal);
            var fill = kind switch
            {
                "copy" => Color.FromArgb(hover ? 255 : 230, 140, 90, 55),
                "save" => Color.FromArgb(hover ? 255 : 230, 70, 120, 90),
                "discard" => Color.FromArgb(hover ? 255 : 220, 160, 70, 60),
                _ => Color.FromArgb(hover ? 230 : 200, 180, 175, 168),
            };
            using var brush = new SolidBrush(fill);
            using var ink = new SolidBrush(kind == "close"
                ? Color.FromArgb(255, 50, 45, 40)
                : Color.FromArgb(255, 255, 252, 248));
            FillRound(g, btn, 10, brush);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, ink, btn, sf);
            _hits.Add(new HitZone(btn, kind));
            x += btnW + 8;
        }
    }

    private void PushLayer(Bitmap bmp)
    {
        using var premul = ToPremultiplied(bmp);
        var screenDc = Native.GetDC(IntPtr.Zero);
        var memDc = Native.CreateCompatibleDC(screenDc);
        var hBitmap = premul.GetHbitmap(Color.FromArgb(0));
        var old = Native.SelectObject(memDc, hBitmap);
        var size = new Native.SIZE { cx = premul.Width, cy = premul.Height };
        var pointSource = new Native.POINT { X = 0, Y = 0 };
        Native.GetWindowRect(_hwnd, out var rect);
        var topPos = new Native.POINT { X = rect.left, Y = rect.top };
        var blend = new Native.BLENDFUNCTION
        {
            BlendOp = 0,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 1,
        };
        Native.UpdateLayeredWindow(_hwnd, screenDc, ref topPos, ref size, memDc, ref pointSource, 0, ref blend, 2);
        Native.SelectObject(memDc, old);
        Native.DeleteObject(hBitmap);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
    }

    private static Bitmap ToPremultiplied(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
        var rect = new Rectangle(0, 0, src.Width, src.Height);
        using var straight = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(straight))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(src, 0, 0);
        }

        var srcData = straight.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dstData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var bytes = Math.Abs(srcData.Stride) * src.Height;
            var buffer = new byte[bytes];
            Marshal.Copy(srcData.Scan0, buffer, 0, bytes);
            for (var i = 0; i + 3 < bytes; i += 4)
            {
                var b = buffer[i];
                var gch = buffer[i + 1];
                var r = buffer[i + 2];
                var a = buffer[i + 3];
                buffer[i] = (byte)(b * a / 255);
                buffer[i + 1] = (byte)(gch * a / 255);
                buffer[i + 2] = (byte)(r * a / 255);
            }

            Marshal.Copy(buffer, 0, dstData.Scan0, bytes);
        }
        finally
        {
            straight.UnlockBits(srcData);
            dst.UnlockBits(dstData);
        }

        return dst;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_NCHITTEST:
            {
                var pt = PackPoint(lParam);
                Native.ScreenToClient(hWnd, ref pt);
                if (Hit(pt) is null)
                {
                    return new IntPtr(Native.HTTRANSPARENT);
                }

                return new IntPtr(1);
            }
            case Native.WM_MOUSEMOVE:
            {
                var pt = ClientFromLParam(lParam);
                var hit = Hit(pt);
                var kind = hit?.Kind;
                if (!string.Equals(_hoverKind, kind, StringComparison.Ordinal))
                {
                    _hoverKind = kind;
                    Render();
                }

                return IntPtr.Zero;
            }
            case Native.WM_LBUTTONUP:
            {
                var pt = ClientFromLParam(lParam);
                var hit = Hit(pt);
                if (hit is not null && _peg is not null)
                {
                    HandleAction(hit.Kind, _peg);
                }

                return IntPtr.Zero;
            }
            case Native.WM_TIMER:
            {
                if (wParam.ToInt32() != AnimTimerId)
                {
                    break;
                }

                if (_revealDir != 0)
                {
                    var step = _revealDir > 0 ? 0.16f : 0.2f;
                    _reveal = Math.Clamp(_reveal + _revealDir * step, 0f, 1f);
                    if (_revealDir > 0 && _reveal >= 1f)
                    {
                        _reveal = 1f;
                        _revealDir = 0;
                    }
                    else if (_revealDir < 0 && _reveal <= 0f)
                    {
                        _reveal = 0f;
                        _revealDir = 0;
                        _visible = false;
                        _peg = null;
                        Native.ShowWindow(_hwnd, Native.SW_HIDE);
                        _host.NotifyActionBarChanged();
                    }

                    Render();
                }

                if (_visible && _peg is not null && Environment.TickCount64 >= _dismissAt)
                {
                    Dismiss();
                }

                return IntPtr.Zero;
            }
            case 0x0002:
                _hwnd = IntPtr.Zero;
                return IntPtr.Zero;
        }

        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void HandleAction(string kind, PegView peg)
    {
        switch (kind)
        {
            case "copy":
                _ = _host.CopyPegAsync(peg);
                Dismiss();
                break;
            case "save":
                _host.SavePegToDownloads(peg);
                Dismiss();
                break;
            case "discard":
                _host.DiscardPeg(peg);
                Dismiss();
                break;
            case "close":
                Dismiss();
                break;
        }
    }

    private HitZone? Hit(Native.POINT pt)
    {
        for (var i = _hits.Count - 1; i >= 0; i--)
        {
            if (_hits[i].Bounds.Contains(pt.X, pt.Y))
            {
                return _hits[i];
            }
        }

        return null;
    }

    private void RegisterClass()
    {
        var windowClass = new Native.WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_procKeep),
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = ClassName,
            hCursor = Native.LoadCursorW(IntPtr.Zero, new IntPtr(32512)),
        };
        if (Native.RegisterClassW(ref windowClass) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != Native.ERROR_CLASS_ALREADY_EXISTS)
            {
                throw new System.ComponentModel.Win32Exception(error);
            }
        }
    }

    private static Rectangle ScreenBounds()
    {
        Native.GetCursorPos(out var cursor);
        var monitor = Native.MonitorFromPoint(cursor, 2);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfoW(monitor, ref info);
        var wa = info.rcWork;
        return Rectangle.FromLTRB(wa.left, wa.top, wa.right, wa.bottom);
    }

    private static Native.POINT PackPoint(IntPtr lParam)
    {
        var packed = unchecked((int)lParam.ToInt64());
        return new Native.POINT { X = (short)(packed & 0xFFFF), Y = (short)((packed >> 16) & 0xFFFF) };
    }

    private static Native.POINT ClientFromLParam(IntPtr lParam)
    {
        var packed = unchecked((int)lParam.ToInt64());
        return new Native.POINT { X = (short)(packed & 0xFFFF), Y = (short)((packed >> 16) & 0xFFFF) };
    }

    private static GraphicsPath RoundPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Max(2, radius * 2);
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void FillRound(Graphics g, Rectangle bounds, int radius, Brush brush)
    {
        using var path = RoundPath(bounds, radius);
        g.FillPath(brush, path);
    }

    private static class Native
    {
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const uint WS_POPUP = 0x80000000;
        public const int HTTRANSPARENT = -1;
        public const uint WM_NCHITTEST = 0x0084;
        public const uint WM_LBUTTONUP = 0x0202;
        public const uint WM_MOUSEMOVE = 0x0200;
        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;
        public static readonly IntPtr HWND_TOPMOST = new(-1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const int ERROR_CLASS_ALREADY_EXISTS = 1410;
        public const uint WM_TIMER = 0x0113;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSW
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx; public int cy; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr ho);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetTimer(IntPtr hWnd, int nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool KillTimer(IntPtr hWnd, int uIDEvent);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);
    }
}
