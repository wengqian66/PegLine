using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PegLine.Core;
using PegLine.Hosting;
using PegLine.ViewModels;

namespace PegLine.Views;

/// <summary>
/// Per-pixel alpha overlay via UpdateLayeredWindow. Replaces the opaque WinUI rope window.
/// </summary>
internal sealed class RopeOverlayForm : IDisposable
{
    private const string ClassName = "PegLine.RopeOverlay";
    private readonly AppHost _host;
    private readonly WndProcKeep _procKeep;
    private readonly List<HitZone> _hits = [];
    private readonly Dictionary<string, Image> _thumbs = new(StringComparer.OrdinalIgnoreCase);
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
    private float _swayPhase;
    private bool _trackingMouse;
    private string? _previewPegId;
    private long _previewSince;
    private readonly Dictionary<string, FallAnim> _falls = new();
    private readonly Dictionary<string, LayoutSlot> _slots = new();
    private long _idleHideAt;
    private float _reveal; // 0 hidden .. 1 shown
    private int _revealDir; // -1 hide, 0 idle, 1 show
    private bool _emptyGuide;
    private Rectangle _anchor; // last on-screen placement; layered updates must not move this
    private long _revealAnimStart;
    private float _revealAnimFrom;
    private bool _pointerOnRope;
    private uint _tickMs;
    private const int AnimTimerId = 1;

    private sealed record HitZone(Rectangle Bounds, string Kind, string? PegId);
    private sealed class FallAnim
    {
        public required string PegId;
        public long Start;
        public float X;
        public float Y;
        public int CardW;
        public int CardH;
        public float Tilt;
        public PegView Peg = null!;
        public Bitmap? Layer;
    }
    private sealed class LayoutSlot
    {
        public float X;
        public float Y;
        public int CardW;
        public int CardH;
        public float Tilt;
    }
    private delegate IntPtr WndProcKeep(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public RopeOverlayForm(AppHost host)
    {
        _host = host;
        _procKeep = WndProc;
        RegisterClass();
        var screen = ScreenBounds();
        _hwnd = Native.CreateWindowExW(
            Native.WS_EX_LAYERED | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
            ClassName,
            "挂绳",
            Native.WS_POPUP,
            screen.X,
            screen.Y,
            screen.Width,
            200,
            IntPtr.Zero,
            IntPtr.Zero,
            Native.GetModuleHandle(null),
            IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx overlay failed");
        }

        _anchor = new Rectangle(screen.X, screen.Y, screen.Width, 200);
        host.Pegs.CollectionChanged += OnPegsChanged;
        SyncThumbs();
        Render();
        UpdateEmpty();
        UseTick(RopeIdle.IdlePollMs);
    }

    public bool AllowClose { get; set; }

    public IntPtr Hwnd => _hwnd;

    /// <summary>True while the rope window is on screen, including during the show or hide slide.</summary>
    public bool IsShown => _visible;

    public bool CanShow => _host.Pegs.Count > 0 || _falls.Count > 0 || _emptyGuide;

    public void ShowRope()
    {
        if (_host.Pegs.Count == 0 && _falls.Count == 0 && !_emptyGuide)
        {
            RequestHide(animated: false);
            return;
        }

        // A show that leaves _idleHideAt in the past is already idle. The next timer
        // tick starts the hide slide and the rope parks flush with the top edge.
        // A deadline NoteActivity just armed (including a shorter one) is left alone.
        var now = Environment.TickCount64;
        if (_idleHideAt <= now)
        {
            _idleHideAt = RopeVisibility.ExtendIdle(_idleHideAt, now, RopeVisibility.ShowLingerMs);
        }

        if (_reveal <= 0.01f)
        {
            _reveal = 0.05f;
        }

        // Don't restart the clock when ShowRope is called again mid-slide (hover
        // keep-alive). Restarting would pin the rope at the top of the slide.
        if (_revealDir != 1)
        {
            if (_reveal >= 0.999f)
            {
                _reveal = 1f;
                _revealDir = 0;
            }
            else
            {
                BeginReveal(1);
            }
        }

        PlaceTop(preview: _previewPegId is not null || _falls.Count > 0);
        Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _anchor.X, _anchor.Y, _anchor.Width, _anchor.Height,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _visible = true;
        Render();
    }

    public void HideRope() => RequestHide(animated: true);

    private void RequestHide(bool animated)
    {
        if (!animated || !_visible || _reveal <= 0.01f)
        {
            _reveal = 0;
            _revealDir = 0;
            _pointerOnRope = false;
            UseTick(RopeIdle.IdlePollMs);
            Native.ShowWindow(_hwnd, Native.SW_HIDE);
            _visible = false;
            return;
        }

        if (_revealDir != -1)
        {
            BeginReveal(-1);
        }
    }

    private void BeginReveal(int direction)
    {
        _revealAnimFrom = _reveal;
        _revealAnimStart = Environment.TickCount64;
        _revealDir = direction;
        UseTick(RopeMotion.AnimTickMs);
    }

    private void UseTick(int milliseconds)
    {
        var next = (uint)milliseconds;
        if (_hwnd == IntPtr.Zero || _tickMs == next)
        {
            return;
        }

        _tickMs = next;
        Native.SetTimer(_hwnd, AnimTimerId, next, IntPtr.Zero);
    }

    private void SamplePointer()
    {
        _pointerOnRope = false;
        if (!_visible || _anchor.Width <= 0 || _anchor.Height <= 0)
        {
            return;
        }

        if (!Native.GetCursorPos(out var pt))
        {
            return;
        }

        var band = _falls.Count > 0 || _previewPegId is not null
            ? _anchor.Height
            : Math.Min(_anchor.Height, 230);
        _pointerOnRope = pt.X >= _anchor.X
            && pt.X < _anchor.Right
            && pt.Y >= _anchor.Y
            && pt.Y < _anchor.Y + band;
    }


    public void UpdateEmpty()
    {
        if ((_host.Pegs.Count == 0 && _falls.Count == 0) || !_host.IsRopeVisible)
        {
            HideRope();
        }
    }

    public void ShowStatus(string text)
    {
        _status = text;
        _statusUntil = Environment.TickCount64 + 1500;
        NoteActivity(show: true, lingerMs: 2000);
        if (_visible)
        {
            Render();
        }
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
                NoteActivity(show: true, lingerMs: 4500);
            }
        }

        UpdateEmpty();
        if (_visible)
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
                _thumbs[peg.FilePath] = MakeThumb(img, 720, 480);
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

    private void PlaceTop(bool preview = false)
    {
        var screen = ScreenBounds();
        if (screen.Width <= 0 || screen.Height <= 0)
        {
            return;
        }

        var height = 230;
        if (_falls.Count > 0)
        {
            height = Math.Min(screen.Height, 720);
        }
        else if (preview)
        {
            height = 520;
        }
        else if (_emptyGuide && _host.Pegs.Count == 0)
        {
            height = 96;
        }

        height = Math.Clamp(height, 96, Math.Max(96, screen.Height));
        _anchor = new Rectangle(screen.X, screen.Y, screen.Width, height);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _anchor.X, _anchor.Y, _anchor.Width, _anchor.Height, Native.SWP_NOACTIVATE);
    }

    private void Render()
    {
        if (_hwnd == IntPtr.Zero || _disposed)
        {
            return;
        }

        int width;
        int height;
        if (_anchor.Width > 0 && _anchor.Height > 0)
        {
            width = _anchor.Width;
            height = _anchor.Height;
        }
        else
        {
            Native.GetWindowRect(_hwnd, out var rect);
            width = Math.Max(200, rect.right - rect.left);
            height = Math.Max(120, rect.bottom - rect.top);
        }

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
            var slide = g.Save();
            g.TranslateTransform(0, (1f - _reveal) * -28f);
            DrawRope(g, width);
            DrawPegs(g, width);
            DrawStatus(g, width);
            DrawHoverPreview(g, width, height);
            g.Restore(slide);
            var slideDy = (int)Math.Round((1f - _reveal) * -28f);
            if (slideDy != 0 || _reveal < 0.92f)
            {
                for (var i = 0; i < _hits.Count; i++)
                {
                    var h = _hits[i];
                    var b = h.Bounds;
                    b.Offset(0, slideDy);
                    _hits[i] = h with { Bounds = b };
                }

                if (_reveal < 0.92f)
                {
                    _hits.Clear();
                }
            }
        }

        // Fade in the premultiply pass. A second full-width bitmap was most of the hitch.
        PushLayer(bmp, _reveal >= 0.999f ? 1f : Math.Clamp(_reveal, 0f, 1f));
    }


    private void DrawRope(Graphics g, int width)
    {
        const float y0 = 30f;
        const float sag = 28f; // deeper catenary
        var left = 48f;
        var right = width - 48f;
        var midX = width / 2f;

        // Soft under-shadow
        using (var underBrush = MakeRopeFadeBrush(left, right, Color.FromArgb(55, 30, 18, 10)))
        using (var under = new Pen(underBrush, 8f))
        {
            under.StartCap = LineCap.Round;
            under.EndCap = LineCap.Round;
            using var shadowPath = new GraphicsPath();
            shadowPath.AddBezier(left, y0 + 3, midX, y0 + sag + 5, midX, y0 + sag + 5, right, y0 + 3);
            g.DrawPath(under, shadowPath);
        }

        // Braided strands with end fade
        DrawRopeStrand(g, left, right, midX, y0 + 1.2f, sag + 1.0f, Color.FromArgb(230, 86, 56, 34), 2.2f);
        DrawRopeStrand(g, left, right, midX, y0 - 0.9f, sag - 0.5f, Color.FromArgb(245, 198, 158, 112), 1.7f);
        DrawRopeStrand(g, left, right, midX, y0, sag, Color.FromArgb(255, 150, 108, 70), 2.6f);
        DrawRopeStrand(g, left, right, midX, y0 - 1.5f, sag - 0.9f, Color.FromArgb(170, 255, 232, 200), 1.0f);

        // Twist marks (also fade near ends)
        for (var i = 1; i <= 22; i++)
        {
            var u = i / 23f;
            var edge = MathF.Min(u, 1f - u);
            var a = (int)(95 * Math.Clamp(edge / 0.14f, 0f, 1f));
            if (a < 8)
            {
                continue;
            }

            using var twist = new Pen(Color.FromArgb(a, 70, 48, 30), 1.1f);
            var pt = BezierPoint(left, y0, midX, y0 + sag, midX, y0 + sag, right, y0, u);
            var ang = u * MathF.PI * 11f;
            g.DrawLine(twist, pt.X - MathF.Cos(ang) * 3.2f, pt.Y - MathF.Sin(ang) * 2.4f,
                pt.X + MathF.Cos(ang) * 3.2f, pt.Y + MathF.Sin(ang) * 2.4f);
        }

        DrawHook(g, left - 10f, y0);
        DrawHook(g, right - 8f, y0);
    }

    private static LinearGradientBrush MakeRopeFadeBrush(float left, float right, Color solid)
    {
        var brush = new LinearGradientBrush(
            new PointF(left, 0),
            new PointF(right, 0),
            Color.FromArgb(0, solid),
            Color.FromArgb(0, solid));
        brush.InterpolationColors = new ColorBlend
        {
            Positions = [0f, 0.10f, 0.22f, 0.78f, 0.90f, 1f],
            Colors =
            [
                Color.FromArgb(0, solid),
                Color.FromArgb(solid.A / 3, solid),
                solid,
                solid,
                Color.FromArgb(solid.A / 3, solid),
                Color.FromArgb(0, solid),
            ],
        };
        return brush;
    }

    private static void DrawRopeStrand(Graphics g, float left, float right, float midX, float y, float sag, Color color, float thickness)
    {
        using var brush = MakeRopeFadeBrush(left, right, color);
        using var pen = new Pen(brush, thickness);
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        using var path = new GraphicsPath();
        path.AddBezier(left, y, midX, y + sag, midX, y + sag, right, y);
        g.DrawPath(pen, path);
    }

    private static float RopeYAt(int width, float x)
    {
        const float y0 = 30f;
        const float sag = 28f;
        var left = 48f;
        var right = width - 48f;
        var midX = width / 2f;
        var span = Math.Max(1f, right - left);
        var u = Math.Clamp((x - left) / span, 0f, 1f);
        return BezierPoint(left, y0, midX, y0 + sag, midX, y0 + sag, right, y0, u).Y;
    }

    private static PointF BezierPoint(float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3, float t)
    {
        var u = 1 - t;
        var uu = u * u;
        var tt = t * t;
        var x = uu * u * x0 + 3 * uu * t * x1 + 3 * u * tt * x2 + tt * t * x3;
        var y = uu * u * y0 + 3 * uu * t * y1 + 3 * u * tt * y2 + tt * t * y3;
        return new PointF(x, y);
    }

    private static void DrawHook(Graphics g, float x, float y)
    {
        // Small metal eyelet the line passes through
        using var ring = new Pen(Color.FromArgb(255, 170, 170, 176), 2.2f);
        using var shine = new Pen(Color.FromArgb(200, 255, 255, 255), 1.0f);
        using var fill = new SolidBrush(Color.FromArgb(180, 120, 120, 128));
        g.FillEllipse(fill, x - 1, y - 7, 12, 12);
        g.DrawEllipse(ring, x - 1, y - 7, 12, 12);
        g.DrawArc(shine, x, y - 6, 8, 8, 200, 100);
    }

    private static void DrawKnot(Graphics g, float x, float y)
    {
        using var dark = new SolidBrush(Color.FromArgb(255, 92, 64, 42));
        using var light = new SolidBrush(Color.FromArgb(255, 188, 148, 108));
        g.FillEllipse(dark, x, y - 5, 10, 10);
        g.FillEllipse(light, x + 2, y - 3.5f, 5, 5);
    }

    private void DrawPegs(Graphics g, int width)
    {
        const int maxW = 150;
        const int maxH = 118;
        const int gap = 20;
        var live = _host.Pegs.Where(p => !_falls.ContainsKey(p.Id)).ToList();
        var count = live.Count;
        _slots.Clear();
        if (count == 0)
        {
            DrawFallingPegs(g);
            return;
        }

        var sizes = new Size[count];
        var total = 0;
        for (var i = 0; i < count; i++)
        {
            sizes[i] = CardSizeFor(live[i], maxW, maxH);
            total += sizes[i].Width + (i > 0 ? gap : 0);
        }

        var x = Math.Max(28, (width - total) / 2);
        for (var index = 0; index < count; index++)
        {
            var peg = live[index];
            var cardW = sizes[index].Width;
            var cardH = sizes[index].Height;
            var cx = x + cardW / 2f;
            var ropeY = RopeYAt(width, cx);
            var y = ropeY + 16f;
            var hovering = string.Equals(_hoverPegId, peg.Id, StringComparison.Ordinal);
            var pressing = string.Equals(_pressAnimPegId, peg.Id, StringComparison.Ordinal);
            var scale = pressing ? 0.94f : hovering ? 1.05f : 1f;
            var sway = MathF.Sin(_swayPhase + index * 0.9f) * (hovering ? 2.4f : 1.35f);
            var tilt = (float)peg.Tilt + sway;
            _slots[peg.Id] = new LayoutSlot { X = x, Y = y, CardW = cardW, CardH = cardH, Tilt = tilt };

            var state = g.Save();
            var pivotX = cx;
            var pivotY = ropeY;
            g.TranslateTransform(pivotX, pivotY);
            g.RotateTransform(tilt);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-pivotX, -pivotY);

            var xi = (int)x;
            var yi = (int)Math.Round((double)y);
            DrawClothespin(g, cx - 5f, ropeY - 12f);
            DrawGlassCard(g, peg, xi, yi, cardW, cardH, hovering);

            var card = new Rectangle(xi - 4, yi - 4, cardW + 8, cardH + 8);
            var close = new Rectangle(xi + 2, yi + 2, 30, 30);
            _hits.Add(new HitZone(card, "peg", peg.Id));
            if (hovering)
            {
                _hits.Add(new HitZone(close, "close", peg.Id));
            }

            g.Restore(state);
            x += cardW + gap;
        }

        DrawFallingPegs(g);
    }

    private Size CardSizeFor(PegView peg, int maxW, int maxH)
    {
        if (_thumbs.TryGetValue(peg.FilePath, out var thumb) && thumb.Width > 0 && thumb.Height > 0)
        {
            var scale = Math.Min(maxW / (float)thumb.Width, maxH / (float)thumb.Height);
            return new Size(
                Math.Max(48, (int)Math.Round(thumb.Width * scale)),
                Math.Max(48, (int)Math.Round(thumb.Height * scale)));
        }

        return new Size(110, 96);
    }

    private void DrawClothespin(Graphics g, float x, float y)
    {
        var body = new RectangleF(x, y, 10, 28);
        using var metal = new LinearGradientBrush(
            body,
            Color.FromArgb(255, 210, 210, 214),
            Color.FromArgb(255, 140, 140, 148),
            LinearGradientMode.Horizontal);
        using var edge = new Pen(Color.FromArgb(255, 255, 255, 255), 0.8f);
        using var shade = new Pen(Color.FromArgb(255, 90, 90, 96), 0.8f);
        using var slot = new SolidBrush(Color.FromArgb(255, 60, 60, 66));
        using var drop = new SolidBrush(Color.FromArgb(255, 55, 48, 40));
        g.FillRectangle(drop, x + 1.5f, y + 2, 10, 28);
        var bodyRect = Rectangle.Round(body);
        FillRound(g, bodyRect, 3, metal);
        using (var rim = RoundPath(bodyRect, 3))
        {
            g.DrawPath(edge, rim);
        }
        g.DrawLine(shade, x + 9.2f, y + 3, x + 9.2f, y + 25);
        g.FillRectangle(slot, x + 2.5f, y + 9, 5, 2);
    }

    private void DrawGlassCard(Graphics g, PegView peg, int x, int y, int cardW, int cardH, bool hovering)
    {
        var card = new Rectangle(x, y, cardW, cardH);
        using (var drop = new SolidBrush(Color.FromArgb(70, 20, 16, 12)))
        {
            FillRound(g, new Rectangle(x + 4, y + 7, cardW, cardH), 14, drop);
        }
        using (var drop2 = new SolidBrush(Color.FromArgb(40, 20, 16, 12)))
        {
            FillRound(g, new Rectangle(x + 2, y + 4, cardW, cardH), 14, drop2);
        }

        var photoMax = new Rectangle(x, y, cardW, cardH);
        using (var cardClip = RoundPath(photoMax, 14))
        {
            var clipState = g.Save();
            g.SetClip(cardClip);
            if (_thumbs.TryGetValue(peg.FilePath, out var thumb))
            {
                // Card size follows the thumbnail aspect, so Fit fills the frame without cropping.
                g.DrawImage(thumb, photoMax);
            }
            else
            {
                using var ph = new SolidBrush(Color.FromArgb(255, 228, 226, 222));
                g.FillRectangle(ph, photoMax);
            }

            // Soft top specular so it still reads as a photo, not a raw bitmap
            using var gloss = new SolidBrush(Color.FromArgb(28, 255, 255, 255));
            g.FillRectangle(gloss, x, y, cardW, Math.Max(10, cardH / 7));
            g.Restore(clipState);
        }

        using (var rimLite = new Pen(Color.FromArgb(160, 255, 255, 255), 1.0f))
        using (var rimDark = new Pen(Color.FromArgb(90, 40, 40, 44), 0.9f))
        using (var outer = RoundPath(photoMax, 14))
        {
            g.DrawPath(rimDark, outer);
            g.DrawPath(rimLite, RoundPath(new Rectangle(x + 1, y + 1, cardW - 2, cardH - 2), 13));
        }

        if (hovering)
        {
            var close = new Rectangle(x + 6, y + 6, 24, 24);
            using (var cbg = new SolidBrush(Color.FromArgb(230, 248, 248, 250)))
            using (var cpen = new Pen(Color.FromArgb(200, 160, 158, 164)))
            using (var cink = new SolidBrush(Color.FromArgb(240, 40, 40, 44)))
            using (var cfont = new Font("Segoe UI", 10f, FontStyle.Bold))
            {
                g.FillEllipse(cbg, close);
                g.DrawEllipse(cpen, close);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("×", cfont, cink, close, sf);
            }
        }
    }

    private void DrawStatus(Graphics g, int width)
    {
        if (_status is null || Environment.TickCount64 > _statusUntil)
        {
            _status = null;
            return;
        }

        using var font = new Font("Segoe UI", 9f, FontStyle.Bold);
        var size = g.MeasureString(_status, font);
        var bounds = new Rectangle(
            Math.Max(8, (int)((width - size.Width) / 2) - 12),
            170,
            (int)size.Width + 24,
            (int)size.Height + 10);
        using var bg = new SolidBrush(Color.FromArgb(255, 244, 244, 246));
        using var edge = new Pen(Color.FromArgb(255, 200, 198, 202));
        using var ink = new SolidBrush(Color.FromArgb(255, 40, 40, 44));
        FillRound(g, bounds, 12, bg);
        using (var rim = RoundPath(bounds, 12))
        {
            g.DrawPath(edge, rim);
        }
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(_status, font, ink, bounds, sf);
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

    private static Rectangle CoverRect(int srcW, int srcH, Rectangle box)
    {
        if (srcW <= 0 || srcH <= 0)
        {
            return box;
        }

        var scale = Math.Max(box.Width / (float)srcW, box.Height / (float)srcH);
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
        using var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private void PushLayer(Bitmap bmp, float opacity = 1f)
    {
        using var premul = ToPremultiplied(bmp, opacity);
        var screenDc = Native.GetDC(IntPtr.Zero);
        var memDc = Native.CreateCompatibleDC(screenDc);
        var hBitmap = premul.GetHbitmap(Color.FromArgb(0));
        var old = Native.SelectObject(memDc, hBitmap);
        var size = new Native.SIZE { cx = premul.Width, cy = premul.Height };
        var pointSource = new Native.POINT { X = 0, Y = 0 };
        var blend = new Native.BLENDFUNCTION
        {
            BlendOp = 0,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 1,
        };
        // pptDst NULL: do not move the window. Passing GetWindowRect back through
        // UpdateLayeredWindow on a Per-Monitor V2 process can rescale the origin and
        // leave the rope flush with Y≈0. PlaceTop owns the position.
        var ok = Native.UpdateLayeredWindow(
            _hwnd,
            screenDc,
            IntPtr.Zero,
            ref size,
            memDc,
            ref pointSource,
            0,
            ref blend,
            2);
        if (!ok)
        {
            App.Log(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "UpdateLayeredWindow failed"));
        }

        RestoreAnchor();

        Native.SelectObject(memDc, old);
        Native.DeleteObject(hBitmap);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
    }

    private static Bitmap ToPremultiplied(Bitmap src, float opacity)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
        var rect = new Rectangle(0, 0, src.Width, src.Height);
        var opacityByte = (int)Math.Round(Math.Clamp(opacity, 0f, 1f) * 255f);
        var widthBytes = src.Width * 4;
        var row = new byte[widthBytes];
        var srcData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dstData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (var y = 0; y < src.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(srcData.Scan0, y * srcData.Stride), row, 0, widthBytes);
                for (var i = 0; i < widthBytes; i += 4)
                {
                    var a = row[i + 3] * opacityByte / 255;
                    row[i] = (byte)(row[i] * a / 255);
                    row[i + 1] = (byte)(row[i + 1] * a / 255);
                    row[i + 2] = (byte)(row[i + 2] * a / 255);
                    row[i + 3] = (byte)a;
                }

                Marshal.Copy(row, 0, IntPtr.Add(dstData.Scan0, y * dstData.Stride), widthBytes);
            }
        }
        finally
        {
            src.UnlockBits(srcData);
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
                    Render();
                    if (peg is not null)
                    {
                        _host.BeginFileDrag(peg);
                    }
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

                SamplePointer();
                TickAutoReveal();
                TickRevealAnim();

                // Finish discard falls
                var layoutChanged = false;
                if (_falls.Count > 0)
                {
                    var done = _falls.Values.Where(f => Environment.TickCount64 - f.Start >= 480).ToList();
                    foreach (var f in done)
                    {
                        _falls.Remove(f.PegId);
                        f.Layer?.Dispose();
                        _host.RemovePeg(f.Peg);
                    }

                    if (_falls.Count == 0)
                    {
                        PlaceTop(preview: _previewPegId is not null);
                        layoutChanged = true;
                    }
                }

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
                            PlaceTop(preview: true);
                        }
                    }
                }
                else if (_previewPegId is not null)
                {
                    _previewPegId = null;
                    _previewSince = 0;
                    hoverChanged = true;
                    PlaceTop(preview: false);
                }

                // TickRevealAnim already painted a slide frame. Otherwise only sway or
                // falling cards repaint — a rope that is just sitting there does not.
                if (RopeIdle.ShouldPaintMotionFrame(_visible, _revealDir != 0, _falls.Count > 0, _pointerOnRope))
                {
                    _swayPhase += 0.11f;
                    if (_swayPhase > MathF.PI * 2)
                    {
                        _swayPhase -= MathF.PI * 2;
                    }

                    Render();
                }
                else if (hoverChanged || layoutChanged)
                {
                    Render();
                }
                else if (_visible && _status is not null && Environment.TickCount64 > _statusUntil)
                {
                    _status = null;
                    Render();
                }

                UseTick(RopeIdle.TickIntervalMs(
                    _revealDir != 0,
                    _falls.Count > 0,
                    _pointerOnRope,
                    RopeMotion.AnimTickMs));
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
            PegLine.Platform.NativeMethods.AppendMenu(menu, PegLine.Platform.NativeMethods.MF_STRING, 6, "取下左侧全部");
            PegLine.Platform.NativeMethods.AppendMenu(menu, PegLine.Platform.NativeMethods.MF_STRING, 7, "取下右侧全部");

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
                case 6:
                    _host.RemovePegsBeside(peg, left: true);
                    break;
                case 7:
                    _host.RemovePegsBeside(peg, left: false);
                    break;
            }
        }
        finally
        {
            PegLine.Platform.NativeMethods.DestroyMenu(menu);
        }
    }

    private void DrawHoverPreview(Graphics g, int width, int height)
    {
        if (_previewPegId is null)
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
        var box = new Rectangle((width - w) / 2, Math.Max(88, height - h - 20), w, h);
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


    public void BeginDiscard(PegView peg)
    {
        if (_falls.ContainsKey(peg.Id))
        {
            return;
        }

        float x = 40, y = 48;
        int cw = 120, ch = 90;
        float tilt = (float)peg.Tilt;
        if (_slots.TryGetValue(peg.Id, out var slot))
        {
            x = slot.X;
            y = slot.Y;
            cw = slot.CardW;
            ch = slot.CardH;
            tilt = slot.Tilt;
        }

        Bitmap? layer = null;
        try
        {
            layer = new Bitmap(Math.Max(8, cw + 48), Math.Max(8, ch + 72), PixelFormat.Format32bppArgb);
            using var lg = Graphics.FromImage(layer);
            lg.SmoothingMode = SmoothingMode.AntiAlias;
            lg.InterpolationMode = InterpolationMode.HighQualityBilinear;
            lg.Clear(Color.Transparent);
            DrawClothespin(lg, cw / 2f - 5f + 24f, 8f);
            DrawGlassCard(lg, peg, 24, 36, cw, ch, false);
        }
        catch
        {
            layer?.Dispose();
            layer = null;
        }

        _falls[peg.Id] = new FallAnim
        {
            PegId = peg.Id,
            Start = Environment.TickCount64,
            X = x,
            Y = y,
            CardW = cw,
            CardH = ch,
            Tilt = tilt,
            Peg = peg,
            Layer = layer,
        };

        if (_previewPegId == peg.Id)
        {
            _previewPegId = null;
            _previewSince = 0;
        }

        NoteActivity(show: true, lingerMs: 6000);
        PlaceTop(preview: true);
        UseTick(RopeIdle.MotionTickMs);
        Render();
    }


    private void DrawFallingPegs(Graphics g)
    {
        const float duration = 480f;
        foreach (var fall in _falls.Values.ToList())
        {
            var raw = Math.Clamp((Environment.TickCount64 - fall.Start) / duration, 0f, 1f);
            // Smooth ease-in (quint) — less snappy than cubic, still accelerating like a drop.
            var e = raw * raw * raw * raw * raw;
            var dy = 380f * e;
            var angle = fall.Tilt + (fall.Tilt * 4f + 14f) * e;
            var alpha = Math.Clamp(1f - e, 0f, 1f);
            // Soft scale-down while falling.
            var scale = 1f - 0.12f * e;

            var state = g.Save();
            try
            {
                var pivotX = fall.X + fall.CardW / 2f;
                var pivotY = fall.Y - 18f;
                g.TranslateTransform(pivotX, pivotY + dy);
                g.RotateTransform(angle);
                g.ScaleTransform(scale, scale);
                g.TranslateTransform(-pivotX, -pivotY);

                if (fall.Layer is not null)
                {
                    var attrs = new System.Drawing.Imaging.ImageAttributes();
                    attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha });
                    var dest = new Rectangle(
                        (int)Math.Round(fall.X - 24),
                        (int)Math.Round(fall.Y - 28),
                        fall.Layer.Width,
                        fall.Layer.Height);
                    g.DrawImage(fall.Layer, dest, 0, 0, fall.Layer.Width, fall.Layer.Height, GraphicsUnit.Pixel, attrs);
                    attrs.Dispose();
                }
                else
                {
                    DrawClothespin(g, fall.X + fall.CardW / 2f - 5f, fall.Y - 28f);
                    DrawGlassCard(g, fall.Peg, (int)fall.X, (int)fall.Y, fall.CardW, fall.CardH, false);
                }
            }
            finally
            {
                g.Restore(state);
            }
        }
    }



    private void TickRevealAnim()
    {
        if (_revealDir == 0)
        {
            return;
        }

        var elapsed = Environment.TickCount64 - _revealAnimStart;
        _reveal = RopeMotion.RevealAt(_revealDir, _revealAnimFrom, elapsed);
        if (_revealDir > 0 && _reveal >= 1f)
        {
            _reveal = 1f;
            _revealDir = 0;
            // Opening is done — put the window back on the work-area top in case a
            // layered update nudged it during the slide. The timer footer picks 8ms,
            // 16ms, or the idle poll once this slide has finished.
            PlaceTop(preview: _previewPegId is not null || _falls.Count > 0);
        }
        else if (_revealDir < 0 && _reveal <= 0f)
        {
            _reveal = 0f;
            _revealDir = 0;
            _pointerOnRope = false;
            Native.ShowWindow(_hwnd, Native.SW_HIDE);
            _visible = false;
        }

        if (_visible || _reveal > 0)
        {
            Render();
        }
    }

    private void NoteActivity(bool show, int lingerMs = RopeVisibility.ShowLingerMs)
    {
        _idleHideAt = RopeVisibility.ExtendIdle(_idleHideAt, Environment.TickCount64, lingerMs);
        if (show && _host.IsRopeVisible && (_host.Pegs.Count > 0 || _falls.Count > 0 || _emptyGuide))
        {
            ShowRope();
        }
    }

    private bool CanIdleHide() =>
        RopeVisibility.ShouldAutoHide(_revealDir > 0, _visible, Environment.TickCount64, _idleHideAt);

    private void TickAutoReveal()
    {
        if (!_host.IsRopeVisible)
        {
            if (_visible)
            {
                HideRope();
            }

            return;
        }

        PegLine.Platform.NativeMethods.GetCursorPos(out var pt);
        var screen = ScreenBounds();
        // Tendedero-like: rest pointer at the very top of the screen.
        var nearTop = pt.Y >= screen.Y && pt.Y <= screen.Y + 8;

        if (_host.Pegs.Count == 0 && _falls.Count == 0)
        {
            if (nearTop)
            {
                _emptyGuide = true;
                _status = "截图或复制图片后会挂在这里 · 右键可复制";
                _statusUntil = Environment.TickCount64 + 2800;
                NoteActivity(show: true, lingerMs: 2800);
                return;
            }

            if (CanIdleHide())
            {
                _emptyGuide = false;
                HideRope();
            }
            else if (!_visible)
            {
                _emptyGuide = false;
            }

            return;
        }

        _emptyGuide = false;
        var band = _falls.Count > 0 ? Math.Min(screen.Height, 720) : (_previewPegId is null ? 200 : 500);
        var overRope = _visible
            && pt.X >= screen.X
            && pt.X < screen.X + screen.Width
            && pt.Y >= screen.Y
            && pt.Y <= screen.Y + band;

        if (nearTop || overRope || _falls.Count > 0)
        {
            NoteActivity(show: true, lingerMs: _falls.Count > 0 ? 6000 : 4200);
            return;
        }

        if (CanIdleHide())
        {
            HideRope();
        }
    }

    private void RestoreAnchor()
    {
        if (_anchor.Width <= 0 || _anchor.Height <= 0 || _hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!Native.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        if (rect.left == _anchor.X
            && rect.top == _anchor.Y
            && rect.right - rect.left == _anchor.Width
            && rect.bottom - rect.top == _anchor.Height)
        {
            return;
        }

        Native.SetWindowPos(
            _hwnd,
            Native.HWND_TOPMOST,
            _anchor.X,
            _anchor.Y,
            _anchor.Width,
            _anchor.Height,
            Native.SWP_NOACTIVATE);
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
        var id = hit?.PegId;
        if (string.Equals(_hoverPegId, id, StringComparison.Ordinal))
        {
            return;
        }

        _hoverPegId = id;
        _previewSince = id is null ? 0 : Environment.TickCount64;
        NoteActivity(show: false, lingerMs: 2800);
        if (id is null)
        {
            _previewPegId = null;
            if (_falls.Count == 0)
            {
                PlaceTop(preview: false);
            }
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
        public const uint LWA_COLORKEY = 0x00000001;
        public const int SRCCOPY = 0x00CC0020;

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
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetTimer(IntPtr hWnd, int nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool KillTimer(IntPtr hWnd, int uIDEvent);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

        [StructLayout(LayoutKind.Sequential)]
        public struct TRACKMOUSEEVENT
        {
            public int cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }

        public const uint TME_LEAVE = 0x00000002;
        public const uint WM_MOUSELEAVE = 0x02A3;
        public const uint WM_TIMER = 0x0113;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);
    }
}