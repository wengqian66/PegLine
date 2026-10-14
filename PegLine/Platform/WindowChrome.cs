using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.UI;

namespace PegLine.Platform;

internal static class WindowChrome
{
    private static readonly NativeMethods.SubclassProc Subclass = OnSubclass;
    private static readonly Dictionary<IntPtr, Func<NativeMethods.POINT, bool>> HitTests = [];
    private static readonly HashSet<IntPtr> Subclassed = [];
    private static readonly object Gate = new();

    public static (IntPtr Hwnd, AppWindow AppWindow) Attach(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return (hwnd, AppWindow.GetFromWindowId(windowId));
    }

    public static void MakeOverlay(IntPtr hwnd, AppWindow appWindow)
    {
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }

        appWindow.IsShownInSwitchers = false;
        appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        try
        {
            appWindow.TitleBar.BackgroundColor = Color.FromArgb(0, 0, 0, 0);
            appWindow.TitleBar.InactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);
            appWindow.TitleBar.ButtonBackgroundColor = Color.FromArgb(0, 0, 0, 0);
            appWindow.TitleBar.ButtonInactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);
            appWindow.TitleBar.SetDragRectangles([]);
        }
        catch (ArgumentException)
        {
        }

        ApplyOverlayStyles(hwnd);
    }

    public static void ApplyOverlayStyles(IntPtr hwnd)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOREDIRECTIONBITMAP;
        style &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));

        var margins = new NativeMethods.MARGINS
        {
            cxLeftWidth = -1,
            cxRightWidth = -1,
            cyTopHeight = -1,
            cyBottomHeight = -1,
        };
        NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        var backdrop = NativeMethods.DWMSBT_NONE;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        var corner = NativeMethods.DWMWCP_DONOTROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        MakeFullyTransparent(hwnd);
    }

    private static void MakeFullyTransparent(IntPtr hwnd)
    {
        // Kill Mica/Acrylic host backdrop and force the Win32 frame to show desktop through empty pixels.
        var hostBackdrop = 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_HOSTBACKDROPBRUSH, ref hostBackdrop, sizeof(int));
        var border = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_CAPTION_COLOR, ref border, sizeof(int));

        var accent = new NativeMethods.AccentPolicy
        {
            AccentState = NativeMethods.AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT,
            AccentFlags = 2,
            GradientColor = 0x00000000,
            AnimationId = 0,
        };
        var size = System.Runtime.InteropServices.Marshal.SizeOf(accent);
        var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(accent, ptr, false);
            var data = new NativeMethods.WindowCompositionAttributeData
            {
                Attribute = NativeMethods.WindowCompositionAttribute.WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size,
            };
            NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);

        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);
        }

    }

    /// <summary>
    /// When true, the whole HWND ignores mouse (clicks fall through). When false, interactive controls receive input.
    /// </summary>
    public static void SetPassThrough(IntPtr hwnd, bool passThrough)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        var next = passThrough
            ? style | NativeMethods.WS_EX_TRANSPARENT
            : style & ~NativeMethods.WS_EX_TRANSPARENT;
        if (next != style)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(next));
        }
    }


    public static void ApplyColorKey(IntPtr hwnd)
    {
        // Punch pure-white WinUI island fill. Pegs/buttons use warm paper colors, not #FFFFFF.
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((style & NativeMethods.WS_EX_LAYERED) == 0)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style | NativeMethods.WS_EX_LAYERED));
        }
        NativeMethods.SetLayeredWindowAttributes(hwnd, 0x00FFFFFF, 255, NativeMethods.LWA_COLORKEY);
    }


    /// <summary>
    /// Restricts the HWND to the given client-pixel rectangles so empty white WinUI fill is not shown.
    /// </summary>
    public static void SetWindowShape(IntPtr hwnd, IReadOnlyList<(int X, int Y, int Width, int Height)> clientRects)
    {
        if (clientRects.Count == 0)
        {
            NativeMethods.SetWindowRgn(hwnd, IntPtr.Zero, true);
            return;
        }

        var first = clientRects[0];
        var combined = NativeMethods.CreateRectRgn(first.X, first.Y, first.X + first.Width, first.Y + first.Height);
        for (var i = 1; i < clientRects.Count; i++)
        {
            var r = clientRects[i];
            var part = NativeMethods.CreateRectRgn(r.X, r.Y, r.X + r.Width, r.Y + r.Height);
            var next = NativeMethods.CreateRectRgn(0, 0, 0, 0);
            NativeMethods.CombineRgn(next, combined, part, NativeMethods.RGN_OR);
            NativeMethods.DeleteObject(combined);
            NativeMethods.DeleteObject(part);
            combined = next;
        }

        // SetWindowRgn takes ownership of the region.
        NativeMethods.SetWindowRgn(hwnd, combined, true);
    }

    public static void PlaceTopStrip(IntPtr hwnd, AppWindow appWindow, int heightDips)
    {
        NativeMethods.GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var height = (int)Math.Round(heightDips * (dpi / 96.0));
        height = Math.Min(height, Math.Max(80, work.Height / 3));
        appWindow.MoveAndResize(new RectInt32(work.X, work.Y, work.Width, height));
    }

    public static void PlaceSettings(IntPtr hwnd, AppWindow appWindow)
    {
        NativeMethods.GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var scale = dpi / 96.0;
        var width = (int)Math.Min(work.Width - 48, Math.Round(520 * scale));
        var height = (int)Math.Min(work.Height - 96, Math.Round(720 * scale));
        var x = work.X + Math.Max(0, (work.Width - width) / 2);
        var y = work.Y + (int)Math.Min(Math.Max(0, work.Height - height), Math.Round(112 * scale));
        appWindow.MoveAndResize(new RectInt32(x, y, Math.Max(360, width), Math.Max(480, height)));
        TintTitleBar(appWindow);
    }

    public static void InstallClickThrough(IntPtr hwnd, Func<NativeMethods.POINT, bool> isInteractiveClientPixel)
    {
        lock (Gate)
        {
            HitTests[hwnd] = isInteractiveClientPixel;
            SubclassTree(hwnd);
        }
    }

    public static void RemoveClickThrough(IntPtr hwnd)
    {
        lock (Gate)
        {
            HitTests.Remove(hwnd);
            foreach (var child in Subclassed.ToArray())
            {
                if (child == hwnd || IsDescendant(hwnd, child))
                {
                    NativeMethods.RemoveWindowSubclass(child, Subclass, 1);
                    Subclassed.Remove(child);
                }
            }
        }

        SetPassThrough(hwnd, false);
    }

    public static void RefreshPassThrough(IntPtr hwnd)
    {
        Func<NativeMethods.POINT, bool>? hit;
        lock (Gate)
        {
            HitTests.TryGetValue(hwnd, out hit);
            SubclassTree(hwnd);
        }

        if (hit is null)
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out var screen))
        {
            return;
        }

        var client = screen;
        if (!NativeMethods.ScreenToClient(hwnd, ref client))
        {
            SetPassThrough(hwnd, true);
            return;
        }

        NativeMethods.GetClientRect(hwnd, out var rect);
        if (client.X < rect.left || client.Y < rect.top || client.X >= rect.right || client.Y >= rect.bottom)
        {
            SetPassThrough(hwnd, true);
            return;
        }

        var interactive = false;
        try
        {
            interactive = hit(client);
        }
        catch
        {
            interactive = true;
        }

        SetPassThrough(hwnd, !interactive);
    }

    private static void SubclassTree(IntPtr root)
    {
        SubclassOne(root);
        for (var child = NativeMethods.GetWindow(root, NativeMethods.GW_CHILD);
             child != IntPtr.Zero;
             child = NativeMethods.GetWindow(child, NativeMethods.GW_HWNDNEXT))
        {
            SubclassTree(child);
        }
    }

    private static void SubclassOne(IntPtr hwnd)
    {
        if (!Subclassed.Add(hwnd))
        {
            return;
        }

        NativeMethods.SetWindowSubclass(hwnd, Subclass, 1, 0);
    }

    private static bool IsDescendant(IntPtr root, IntPtr node)
    {
        for (var cur = node; cur != IntPtr.Zero; cur = NativeMethods.GetParent(cur))
        {
            if (cur == root)
            {
                return true;
            }
        }

        return false;
    }

    private static void TintTitleBar(AppWindow appWindow)
    {
        var title = appWindow.TitleBar;
        var paper = Color.FromArgb(255, 243, 235, 221);
        var ink = Color.FromArgb(255, 44, 36, 22);
        var hover = Color.FromArgb(255, 228, 211, 188);
        title.BackgroundColor = paper;
        title.ForegroundColor = ink;
        title.InactiveBackgroundColor = paper;
        title.InactiveForegroundColor = ink;
        title.ButtonBackgroundColor = paper;
        title.ButtonForegroundColor = ink;
        title.ButtonInactiveBackgroundColor = paper;
        title.ButtonInactiveForegroundColor = ink;
        title.ButtonHoverBackgroundColor = hover;
        title.ButtonHoverForegroundColor = ink;
        title.ButtonPressedBackgroundColor = Color.FromArgb(255, 140, 74, 47);
        title.ButtonPressedForegroundColor = Color.FromArgb(255, 255, 249, 242);
    }

    private static IntPtr OnSubclass(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }

        if (msg == NativeMethods.WM_NCHITTEST)
        {
            IntPtr root = hWnd;
            Func<NativeMethods.POINT, bool>? hit = null;
            lock (Gate)
            {
                if (!HitTests.TryGetValue(hWnd, out hit))
                {
                    foreach (var pair in HitTests)
                    {
                        if (IsDescendant(pair.Key, hWnd))
                        {
                            root = pair.Key;
                            hit = pair.Value;
                            break;
                        }
                    }
                }
                else
                {
                    root = hWnd;
                }
            }

            if (hit is not null && TryClientPoint(root, lParam, out var client))
            {
                try
                {
                    if (!hit(client))
                    {
                        return new IntPtr(NativeMethods.HTTRANSPARENT);
                    }
                }
                catch
                {
                }
            }
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private static bool TryClientPoint(IntPtr hwnd, IntPtr lParam, out NativeMethods.POINT client)
    {
        var packed = unchecked((int)lParam.ToInt64());
        client = new NativeMethods.POINT
        {
            X = (short)(packed & 0xFFFF),
            Y = (short)((packed >> 16) & 0xFFFF),
        };
        return NativeMethods.ScreenToClient(hwnd, ref client);
    }
}
