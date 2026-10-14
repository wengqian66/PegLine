using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PegLine.Hosting;
using PegLine.ViewModels;

namespace PegLine.Views;

/// <summary>
/// Right-edge staging dock (default skin). GDI layered overlay like RopeOverlayForm.
/// </summary>
internal sealed class DockOverlayForm : IDisposable
{
    private const string ClassName = "PegLine.DockOverlay";
    private const int DockWidth = 248;
    private const int StripWidth = 10;
    private const int CardMaxW = 200;
    private const int CardMaxH = 120;
    private const int CardGap = 10;
    private const int AnimTimerId = 1;
    private const int IdleHideMs = 4000;

    private readonly AppHost _host;
    private readonly WndProcKeep _procKeep;
    private readonly List<HitZone> _hits = [];
    private readonly Dictionary<string, Image> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LayoutSlot> _slots = new();
    private IntPtr _hwnd;
    private bool _disposed;
    private bool _visible;
    private string? _status;
    private long _statusUntil;
    private Point? _press;
    private string? _pressPegId;
    private bool _dragging;
    private string? _hoverPegId;
    private string? _pressAnimPegId;
    private bool _trackingMouse;
    private string? _previewPegId;
    private long _previewSince;
    private long _idleHideAt;
    private float _reveal; // 0 collapsed .. 1 expanded
    private int _revealDir; // -1 hide, 0 idle, 1 show
    private bool _emptyGuide;
    private bool _keepOpen; // while action bar / drag

    private sealed record HitZone(Rectangle Bounds, string Kind, string? PegId);
    private sealed class LayoutSlot
    {
        public float X;
        public float Y;
        public int CardW;
        public int CardH;
    }

    private delegate IntPtr WndProcKeep(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public DockOverlayForm(AppHost host)
    {
        _host = host;
        _procKeep = WndProc;
        RegisterClass();
        var screen = ScreenBounds();
        var x = screen.Right - StripWidth;
        _hwnd = Native.CreateWindowExW(
            Native.WS_EX_LAYERED | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
            ClassName,
            "暂存坞",
            Native.WS_POPUP,
            x,
            screen.Y,
            StripWidth,
            screen.Height,
            IntPtr.Zero,
            IntPtr.Zero,
            Native.GetModuleHandle(null),
            IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx dock failed");
        }

        host.Pegs.CollectionChanged += OnPegsChanged;
        SyncThumbs();
        PlaceWindow();
        Render();
        UpdateEmpty();
        Native.SetTimer(_hwnd, AnimTimerId, 16, IntPtr.Zero);
    }

    public bool AllowClose { get; set; }
    public IntPtr Hwnd => _hwnd;

    public void SetKeepOpen(bool keep)
    {
        _keepOpen = keep;
        if (keep)
        {
            NoteActivity(show: true, lingerMs: IdleHideMs);
        }
    }

    public void ShowDock()
    {
        if (!_host.IsRopeVisible)
        {
            RequestHide(animated: false);
            return;
        }

        if (_host.Pegs.Count == 0 && !_emptyGuide)
        {
            // thin strip still tracks edge; expand only for tip
            EnsureStripVisible();
            return;
        }

        if (_reveal <= 0.01f)
        {
            _reveal = 0.05f;
        }

        _revealDir = 1;
        PlaceWindow();
        Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _visible = true;
        Render();
    }

    public void HideDock() => RequestHide(animated: true);

    private void RequestHide(bool animated)
    {
        if (_keepOpen || _host.IsActionBarOpen)
        {
            return;
        }

        if (!animated || !_visible || _reveal <= 0.01f)
        {
            _reveal = 0;
            _revealDir = 0;
            _emptyGuide = false;
            EnsureStripVisible();
            return;
        }

        _revealDir = -1;
    }

    public void UpdateEmpty()
    {
        if (!_host.IsRopeVisible)
        {
            RequestHide(animated: false);
            return;
        }

        if (_host.Pegs.Count == 0)
        {
            // keep edge strip; hide expanded content
            if (_reveal > 0.01f && !_emptyGuide && !_keepOpen)
            {
                RequestHide(animated: true);
            }
            else
            {
                EnsureStripVisible();
            }
        }
    }

    public void ShowStatus(string text)
    {
        _status = text;
        _statusUntil = Environment.TickCount64 + 1500;
        NoteActivity(show: true, lingerMs: 2200);
        if (_visible || _reveal > 0)
        {
            Render();
        }
    }

    public void NoteNewPeg()
    {
        NoteActivity(show: true, lingerMs: IdleHideMs + 800);
        ShowDock();
    }

    public void BeginDiscard(PegView peg)
    {
        // Dock uses simple remove (no fall animation for speed)
        _host.RemovePeg(peg);
    }

    public void Close()
    {
        AllowClose = true;
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.Pegs.CollectionChanged -= OnPegsChanged;
        foreach (var img in _thumbs.Values)
        {
            img.Dispose();
        }

        _thumbs.Clear();
        if (_hwnd != IntPtr.Zero)
        {
            Native.KillTimer(_hwnd, AnimTimerId);
            Native.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private void OnPegsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncThumbs();
        if (!_host.IsRopeVisible)
        {
            UpdateEmpty();
            return;
        }

        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            if (_host.Pegs.Count > 0)
            {
                NoteActivity(show: true, lingerMs: IdleHideMs + 500);
            }
        }

        UpdateEmpty();
        if (_visible || _reveal > 0)
        {
            Render();
        }
    }

    private void SyncThumbs()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var peg in _host.Pegs)
        {
            live.Add(peg.FilePath);
            if (_thumbs.ContainsKey(peg.FilePath))
            {
                continue;
            }

            try
            {
                using var fs = new FileStream(peg.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var img = Image.FromStream(fs);
                _thumbs[peg.FilePath] = MakeThumb(img, 640, 400);
            }
            catch
            {
            }
        }

        foreach (var key in _thumbs.Keys.ToArray())
        {
            if (live.Contains(key))
            {
                continue;
            }

            _thumbs[key].Dispose();
            _thumbs.Remove(key);
        }
    }

    private void EnsureStripVisible()
    {
        PlaceWindow();
        Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _visible = true;
        Render();
    }

    private void PlaceWindow()
    {
        var screen = ScreenBounds();
        var previewExtra = _previewPegId is not null ? 420 : 0;
        var contentW = (int)Math.Round(StripWidth + (DockWidth - StripWidth) * Math.Clamp(_reveal, 0f, 1f));
        var width = Math.Max(StripWidth, contentW + previewExtra);
        var x = screen.Right - width;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, screen.Y, width, screen.Height, Native.SWP_NOACTIVATE);
    }

    private void Render()
    {
        if (_hwnd == IntPtr.Zero || _disposed)
        {
            return;
        }

        Native.GetWindowRect(_hwnd, out var rect);
        var width = Math.Max(StripWidth, rect.right - rect.left);
        var height = Math.Max(120, rect.bottom - rect.top);

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.CompositingMode = CompositingMode.SourceOver;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            _hits.Clear();
            _slots.Clear();

            DrawEdgeStrip(g, width, height);

            if (_reveal > 0.02f || _emptyGuide)
            {
                var dockLeft = width - DockWidth;
                var slide = g.Save();
                // slide in from right
                var dx = (1f - _reveal) * DockWidth;
                g.TranslateTransform(dx, 0);
                DrawDockPanel(g, dockLeft, height);
                DrawCards(g, dockLeft, height);
                DrawStatus(g, dockLeft);
                DrawEmptyTip(g, dockLeft);
                g.Restore(slide);

                var slideDx = (int)Math.Round(dx);
                if (slideDx != 0 || _reveal < 0.9f)
                {
                    for (var i = 0; i < _hits.Count; i++)
                    {
                        var h = _hits[i];
                        var b = h.Bounds;
                        b.Offset(slideDx, 0);
                        _hits[i] = h with { Bounds = b };
                    }

                    if (_reveal < 0.9f)
                    {
                        // only strip is hittable while mostly collapsed
                        _hits.RemoveAll(z => z.Kind != "strip");
                    }
                }
            }

            DrawHoverPreview(g, width, height);
        }

        if (_reveal < 0.999f && _reveal > 0.01f)
        {
            using var faded = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
            using var fg = Graphics.FromImage(faded);
            // strip stays opaque; panel fades with reveal — simple full fade is fine
            var attrs = new ImageAttributes();
            attrs.SetColorMatrix(new ColorMatrix { Matrix33 = Math.Clamp(0.35f + _reveal * 0.65f, 0f, 1f) });
            fg.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attrs);
            attrs.Dispose();
            PushLayer(faded);
        }
        else
        {
            PushLayer(bmp);
        }
    }

    private void DrawEdgeStrip(Graphics g, int width, int height)
    {
        var strip = new Rectangle(width - StripWidth, 0, StripWidth, height);
        using var brush = new LinearGradientBrush(
            strip,
            Color.FromArgb(_reveal > 0.5f ? 40 : 90, 140, 110, 80),
            Color.FromArgb(_reveal > 0.5f ? 20 : 60, 90, 70, 50),
            LinearGradientMode.Horizontal);
        g.FillRectangle(brush, strip);
        using var accent = new SolidBrush(Color.FromArgb(160, 200, 140, 90));
        g.FillRectangle(accent, width - 3, height / 2 - 28, 2, 56);
        _hits.Add(new HitZone(strip, "strip", null));
    }

    private void DrawDockPanel(Graphics g, int left, int height)
    {
        var panel = new Rectangle(left + 4, 12, DockWidth - 12, height - 24);
        using (var drop = new SolidBrush(Color.FromArgb(50, 20, 16, 12)))
        {
            FillRound(g, new Rectangle(panel.X + 3, panel.Y + 4, panel.Width, panel.Height), 16, drop);
        }

        using var bg = new SolidBrush(Color.FromArgb(235, 250, 246, 238));
        using var edge = new Pen(Color.FromArgb(180, 210, 190, 160), 1.2f);
        FillRound(g, panel, 16, bg);
        using (var rim = RoundPath(panel, 16))
        {
            g.DrawPath(edge, rim);
        }

        using var titleFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
        using var titleInk = new SolidBrush(Color.FromArgb(230, 60, 48, 36));
        g.DrawString("暂存坞", titleFont, titleInk, left + 18, 22);
        using var subFont = new Font("Microsoft YaHei UI", 7.5f);
        using var subInk = new SolidBrush(Color.FromArgb(180, 120, 100, 80));
        var count = _host.Pegs.Count;
        g.DrawString(count == 0 ? "暂无图片" : $"{count} 张", subFont, subInk, left + 18, 42);
    }

    private void DrawCards(Graphics g, int left, int height)
    {
        var pegs = _host.Pegs.ToList();
        if (pegs.Count == 0)
        {
            return;
        }

        var y = 64f;
        var maxBottom = height - 40;
        foreach (var peg in pegs)
        {
            var size = CardSizeFor(peg);
            if (y + size.Height > maxBottom)
            {
                break;
            }

            var x = left + (DockWidth - size.Width) / 2f;
            var hovering = string.Equals(_hoverPegId, peg.Id, StringComparison.Ordinal);
            var pressing = string.Equals(_pressAnimPegId, peg.Id, StringComparison.Ordinal);
            var scale = pressing ? 0.96f : hovering ? 1.03f : 1f;

            var state = g.Save();
            var cx = x + size.Width / 2f;
            var cy = y + size.Height / 2f;
            g.TranslateTransform(cx, cy);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-cx, -cy);

            var xi = (int)Math.Round(x);
            var yi = (int)Math.Round(y);
            DrawCard(g, peg, xi, yi, size.Width, size.Height, hovering);
            _slots[peg.Id] = new LayoutSlot { X = x, Y = y, CardW = size.Width, CardH = size.Height };

            var card = new Rectangle(xi - 2, yi - 2, size.Width + 4, size.Height + 4);
            var close = new Rectangle(xi + 4, yi + 4, 26, 26);
            _hits.Add(new HitZone(card, "peg", peg.Id));
            if (hovering)
            {
                _hits.Add(new HitZone(close, "close", peg.Id));
            }

            g.Restore(state);
            y += size.Height + CardGap;
        }
    }

    private void DrawCard(Graphics g, PegView peg, int x, int y, int w, int h, bool hovering)
    {
        var card = new Rectangle(x, y, w, h);
        using (var drop = new SolidBrush(Color.FromArgb(60, 20, 16, 12)))
        {
            FillRound(g, new Rectangle(x + 2, y + 3, w, h), 12, drop);
        }

        using (var clip = RoundPath(card, 12))
        {
            var st = g.Save();
            g.SetClip(clip);
            if (_thumbs.TryGetValue(peg.FilePath, out var thumb))
            {
                g.DrawImage(thumb, FitRect(thumb.Width, thumb.Height, card));
            }
            else
            {
                using var ph = new SolidBrush(Color.FromArgb(255, 228, 226, 222));
                g.FillRectangle(ph, card);
            }

            g.Restore(st);
        }

        using var rim = new Pen(Color.FromArgb(160, 255, 255, 255), 1f);
        g.DrawPath(rim, RoundPath(card, 12));

        if (hovering)
        {
            var close = new Rectangle(x + 6, y + 6, 22, 22);
            using var cbg = new SolidBrush(Color.FromArgb(230, 248, 248, 250));
            using var cpen = new Pen(Color.FromArgb(200, 160, 158, 164));
            using var cink = new SolidBrush(Color.FromArgb(240, 40, 40, 44));
            using var cfont = new Font("Segoe UI", 9f, FontStyle.Bold);
            g.FillEllipse(cbg, close);
            g.DrawEllipse(cpen, close);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("×", cfont, cink, close, sf);
        }
    }

    private Size CardSizeFor(PegView peg)
    {
        if (_thumbs.TryGetValue(peg.FilePath, out var thumb) && thumb.Width > 0 && thumb.Height > 0)
        {
            var scale = Math.Min(CardMaxW / (float)thumb.Width, CardMaxH / (float)thumb.Height);
            return new Size(
                Math.Max(64, (int)Math.Round(thumb.Width * scale)),
                Math.Max(48, (int)Math.Round(thumb.Height * scale)));
        }

        return new Size(160, 100);
    }

    private void DrawStatus(Graphics g, int left)
    {
        if (_status is null || Environment.TickCount64 > _statusUntil)
        {
            _status = null;
            return;
        }

        using var font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
        var size = g.MeasureString(_status, font);
        var bounds = new Rectangle(
            left + 16,
            52,
            Math.Min(DockWidth - 32, (int)size.Width + 20),
            (int)size.Height + 8);
        using var bg = new SolidBrush(Color.FromArgb(240, 244, 244, 246));
        using var edge = new Pen(Color.FromArgb(200, 200, 198, 202));
        using var ink = new SolidBrush(Color.FromArgb(255, 40, 40, 44));
        FillRound(g, bounds, 10, bg);
        using (var rim = RoundPath(bounds, 10))
        {
            g.DrawPath(edge, rim);
        }

        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(_status, font, ink, bounds, sf);
    }

    private void DrawEmptyTip(Graphics g, int left)
    {
        if (!_emptyGuide || _host.Pegs.Count > 0)
        {
            return;
        }

        using var font = new Font("Microsoft YaHei UI", 8.5f);
        using var ink = new SolidBrush(Color.FromArgb(210, 90, 70, 50));
        var rect = new Rectangle(left + 16, 70, DockWidth - 32, 80);
        g.DrawString("截图或复制图片后会进这里\n左键复制 · 右键更多", font, ink, rect);
    }

    private void DrawHoverPreview(Graphics g, int width, int height)
    {
        if (_previewPegId is null || _reveal < 0.85f)
        {
            return;
        }

        var peg = _host.Pegs.FirstOrDefault(p => p.Id == _previewPegId);
        if (peg is null || !_thumbs.TryGetValue(peg.FilePath, out var thumb))
        {
            return;
        }

        const int maxW = 640;
        const int maxH = 400;
        var scale = Math.Min(maxW / (float)thumb.Width, maxH / (float)thumb.Height);
        var w = Math.Max(80, (int)Math.Round(thumb.Width * scale));
        var h = Math.Max(60, (int)Math.Round(thumb.Height * scale));
        var box = new Rectangle(Math.Max(8, width - DockWidth - w - 16), Math.Max(40, (height - h) / 2), w, h);
        using (var drop = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
        {
            FillRound(g, new Rectangle(box.X + 4, box.Y + 6, box.Width, box.Height), 12, drop);
        }

        using (var clip = RoundPath(box, 12))
        {
            var st = g.Save();
            g.SetClip(clip);
            g.DrawImage(thumb, box);
            g.Restore(st);
        }

        using var rim = new Pen(Color.FromArgb(200, 255, 255, 255), 1.2f);
        g.DrawPath(rim, RoundPath(box, 12));
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
            case Native.WM_LBUTTONDOWN:
            {
                var pt = ClientFromLParam(lParam);
                var hit = Hit(pt);
                if (hit?.Kind == "peg")
                {
                    _press = new Point(pt.X, pt.Y);
                    _pressPegId = hit.PegId;
                    _pressAnimPegId = hit.PegId;
                    _dragging = false;
                    Render();
                }
                else if (hit?.Kind == "strip")
                {
                    NoteActivity(show: true, lingerMs: IdleHideMs);
                    ShowDock();
                }
                else
                {
                    _press = null;
                    _pressPegId = null;
                    _pressAnimPegId = null;
                }

                return IntPtr.Zero;
            }
            case Native.WM_MOUSEMOVE:
            {
                EnsureMouseTracking();
                var ptMove = ClientFromLParam(lParam);
                UpdateHover(ptMove);

                if (_press is null || _pressPegId is null)
                {
                    return IntPtr.Zero;
                }

                var dx = ptMove.X - _press.Value.X;
                var dy = ptMove.Y - _press.Value.Y;
                if (!_dragging && dx * dx + dy * dy >= 100)
                {
                    _dragging = true;
                    _pressAnimPegId = null;
                    var peg = _host.Pegs.FirstOrDefault(p => p.Id == _pressPegId);
                    _press = null;
                    _pressPegId = null;
                    SetKeepOpen(true);
                    Render();
                    if (peg is not null)
                    {
                        _host.BeginFileDrag(peg);
                    }

                    SetKeepOpen(false);
                }

                return IntPtr.Zero;
            }
            case Native.WM_MOUSELEAVE:
            {
                _trackingMouse = false;
                if (_hoverPegId is not null || _pressAnimPegId is not null)
                {
                    _hoverPegId = null;
                    if (_press is null)
                    {
                        _pressAnimPegId = null;
                    }

                    Render();
                }

                return IntPtr.Zero;
            }
            case Native.WM_TIMER:
            {
                if (wParam.ToInt32() != AnimTimerId)
                {
                    break;
                }

                TickAutoReveal();
                TickRevealAnim();

                var hoverChanged = false;
                if (_hoverPegId is not null)
                {
                    if (_previewPegId != _hoverPegId)
                    {
                        if (_previewSince == 0)
                        {
                            _previewSince = Environment.TickCount64;
                        }
                        else if (Environment.TickCount64 - _previewSince >= 380)
                        {
                            _previewPegId = _hoverPegId;
                            hoverChanged = true;
                            PlaceWindow();
                        }
                    }
                }
                else if (_previewPegId is not null)
                {
                    _previewPegId = null;
                    _previewSince = 0;
                    hoverChanged = true;
                    PlaceWindow();
                }

                if (hoverChanged || (_visible && _revealDir != 0))
                {
                    Render();
                }
                else if (_visible && _status is not null)
                {
                    if (Environment.TickCount64 > _statusUntil)
                    {
                        _status = null;
                        Render();
                    }
                }

                return IntPtr.Zero;
            }
            case Native.WM_LBUTTONUP:
            {
                var pt = ClientFromLParam(lParam);
                var hit = Hit(pt);
                if (!_dragging)
                {
                    if (hit?.Kind == "close" && hit.PegId is not null)
                    {
                        var peg = _host.Pegs.FirstOrDefault(p => p.Id == hit.PegId);
                        if (peg is not null)
                        {
                            BeginDiscard(peg);
                        }
                    }
                    else if (hit?.Kind == "peg" && hit.PegId is not null)
                    {
                        var peg = _host.Pegs.FirstOrDefault(p => p.Id == hit.PegId);
                        if (peg is not null)
                        {
                            _ = _host.CopyPegAsync(peg);
                        }
                    }
                }

                _press = null;
                _pressPegId = null;
                _pressAnimPegId = null;
                _dragging = false;
                Render();
                return IntPtr.Zero;
            }
            case Native.WM_RBUTTONUP:
            {
                var pt = ClientFromLParam(lParam);
                var hit = Hit(pt);
                if (hit?.PegId is not null)
                {
                    var peg = _host.Pegs.FirstOrDefault(p => p.Id == hit.PegId);
                    if (peg is not null)
                    {
                        ShowPegMenu(peg);
                    }
                }

                return IntPtr.Zero;
            }
            case 0x0010:
                if (!AllowClose)
                {
                    _host.SetRopeVisible(false);
                    return IntPtr.Zero;
                }

                break;
            case 0x0002:
                _hwnd = IntPtr.Zero;
                return IntPtr.Zero;
        }

        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowPegMenu(PegView peg)
    {
        var menu = PegLine.Platform.NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            PegLine.Platform.NativeMethods.AppendMenu(menu, PegLine.Platform.NativeMethods.MF_STRING, 1, "复制");
            PegLine.Platform.NativeMethods.AppendMenu(menu, PegLine.Platform.NativeMethods.MF_STRING, 2, "快速查看");
            PegLine.Platform.NativeMethods.AppendMenu(menu, PegLine.Platform.NativeMethods.MF_STRING, 4, "存到 Saved");
            PegLine.Platform.NativeMethods.AppendMenu(menu, PegLine.Platform.NativeMethods.MF_SEPARATOR, 0, null);
            PegLine.Platform.NativeMethods.AppendMenu(menu, PegLine.Platform.NativeMethods.MF_STRING, 5, "丢弃");

            PegLine.Platform.NativeMethods.GetCursorPos(out var point);
            PegLine.Platform.NativeMethods.SetForegroundWindow(_hwnd);
            var command = PegLine.Platform.NativeMethods.TrackPopupMenu(
                menu,
                PegLine.Platform.NativeMethods.TPM_RETURNCMD | PegLine.Platform.NativeMethods.TPM_RIGHTBUTTON | PegLine.Platform.NativeMethods.TPM_NONOTIFY,
                point.X,
                point.Y,
                0,
                _hwnd,
                IntPtr.Zero);
            PegLine.Platform.NativeMethods.PostMessage(_hwnd, PegLine.Platform.NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case 1:
                    _ = _host.CopyPegAsync(peg);
                    break;
                case 2:
                    _host.OpenPeg(peg);
                    break;
                case 4:
                    _host.SavePegToDownloads(peg);
                    break;
                case 5:
                    BeginDiscard(peg);
                    break;
            }
        }
        finally
        {
            PegLine.Platform.NativeMethods.DestroyMenu(menu);
        }
    }

    private void TickRevealAnim()
    {
        if (_revealDir == 0)
        {
            return;
        }

        // ~120ms expand / ~100ms collapse
        var step = _revealDir > 0 ? 0.14f : 0.17f;
        _reveal = Math.Clamp(_reveal + _revealDir * step, 0f, 1f);
        PlaceWindow();
        if (_revealDir > 0 && _reveal >= 1f)
        {
            _reveal = 1f;
            _revealDir = 0;
        }
        else if (_revealDir < 0 && _reveal <= 0f)
        {
            _reveal = 0f;
            _revealDir = 0;
            _emptyGuide = false;
            PlaceWindow();
        }

        Render();
    }

    private void NoteActivity(bool show, int lingerMs = IdleHideMs)
    {
        _idleHideAt = Environment.TickCount64 + lingerMs;
        if (show && _host.IsRopeVisible)
        {
            if (_host.Pegs.Count > 0 || _emptyGuide)
            {
                ShowDock();
            }
            else
            {
                EnsureStripVisible();
            }
        }
    }

    private void TickAutoReveal()
    {
        if (!_host.IsRopeVisible)
        {
            if (_visible && _reveal > 0)
            {
                RequestHide(animated: false);
            }

            return;
        }

        PegLine.Platform.NativeMethods.GetCursorPos(out var pt);
        var screen = ScreenBounds();
        var nearRight = pt.X >= screen.Right - StripWidth - 2
            && pt.X <= screen.Right + 2
            && pt.Y >= screen.Y
            && pt.Y <= screen.Bottom;

        Native.GetWindowRect(_hwnd, out var wr);
        var overDock = _reveal > 0.2f
            && pt.X >= wr.left
            && pt.X < wr.right
            && pt.Y >= wr.top
            && pt.Y <= wr.bottom;

        if (_host.Pegs.Count == 0)
        {
            if (nearRight)
            {
                _emptyGuide = true;
                if (_reveal < 0.5f)
                {
                    _revealDir = 1;
                }

                NoteActivity(show: true, lingerMs: 2800);
                EnsureStripVisible();
                return;
            }

            if (_reveal > 0 && Environment.TickCount64 >= _idleHideAt && !_keepOpen && !_host.IsActionBarOpen)
            {
                RequestHide(animated: true);
            }

            return;
        }

        _emptyGuide = false;
        if (nearRight || overDock || _keepOpen || _host.IsActionBarOpen)
        {
            NoteActivity(show: true, lingerMs: IdleHideMs);
            return;
        }

        if (_reveal > 0.01f && Environment.TickCount64 >= _idleHideAt)
        {
            RequestHide(animated: true);
        }
    }

    private void EnsureMouseTracking()
    {
        if (_trackingMouse || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var tme = new Native.TRACKMOUSEEVENT
        {
            cbSize = Marshal.SizeOf<Native.TRACKMOUSEEVENT>(),
            dwFlags = Native.TME_LEAVE,
            hwndTrack = _hwnd,
            dwHoverTime = 0,
        };
        if (Native.TrackMouseEvent(ref tme))
        {
            _trackingMouse = true;
        }
    }

    private void UpdateHover(Native.POINT pt)
    {
        var hit = Hit(pt);
        var id = hit?.Kind is "peg" or "close" ? hit.PegId : null;
        if (string.Equals(_hoverPegId, id, StringComparison.Ordinal))
        {
            return;
        }

        _hoverPegId = id;
        _previewSince = id is null ? 0 : Environment.TickCount64;
        NoteActivity(show: false, lingerMs: IdleHideMs);
        if (id is null)
        {
            _previewPegId = null;
            PlaceWindow();
        }

        Render();
    }

    private HitZone? Hit(Native.POINT pt)
    {
        HitZone? pegHit = null;
        for (var i = _hits.Count - 1; i >= 0; i--)
        {
            var zone = _hits[i];
            if (!zone.Bounds.Contains(pt.X, pt.Y))
            {
                continue;
            }

            if (zone.Kind == "close")
            {
                return zone;
            }

            pegHit ??= zone;
        }

        return pegHit;
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

    private static Bitmap MakeThumb(Image src, int maxW, int maxH)
    {
        var scale = Math.Min(maxW / (float)src.Width, maxH / (float)src.Height);
        scale = Math.Min(scale, 1f);
        var w = Math.Max(1, (int)Math.Round(src.Width * scale));
        var h = Math.Max(1, (int)Math.Round(src.Height * scale));
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, w, h);
        return bmp;
    }

    private static Rectangle FitRect(int srcW, int srcH, Rectangle box)
    {
        if (srcW <= 0 || srcH <= 0)
        {
            return box;
        }

        var scale = Math.Min(box.Width / (float)srcW, box.Height / (float)srcH);
        var w = Math.Max(1, (int)Math.Round(srcW * scale));
        var h = Math.Max(1, (int)Math.Round(srcH * scale));
        return new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
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
        public const uint WM_LBUTTONDOWN = 0x0201;
        public const uint WM_LBUTTONUP = 0x0202;
        public const uint WM_RBUTTONUP = 0x0205;
        public const uint WM_MOUSEMOVE = 0x0200;
        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;
        public static readonly IntPtr HWND_TOPMOST = new(-1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const int ERROR_CLASS_ALREADY_EXISTS = 1410;
        public const uint TME_LEAVE = 0x00000002;
        public const uint WM_MOUSELEAVE = 0x02A3;
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

        [StructLayout(LayoutKind.Sequential)]
        public struct TRACKMOUSEEVENT
        {
            public int cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
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

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);
    }
}
