using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PegLine.Hosting;
using PegLine.Platform;
using Windows.Foundation;

namespace PegLine.Views;

public sealed partial class RopeWindow : Window
{
    private readonly AppHost _host;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private DispatcherQueueTimer? _statusTimer;
    private DispatcherQueueTimer? _passThroughTimer;
    private bool _clickThrough;
    private bool _layingOut;

    public RopeWindow(AppHost host)
    {
        InitializeComponent();
        SystemBackdrop = null;
        Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _host = host;
        PegList.ItemsSource = host.Pegs;
        (_hwnd, _appWindow) = WindowChrome.Attach(this);
        Title = "挂绳";
        WindowChrome.MakeOverlay(_hwnd, _appWindow);
        _appWindow.Closing += OnClosing;
        Root.Loaded += (_, _) =>
        {
            EnsureClickThrough();
            WindowChrome.ApplyColorKey(_hwnd);
            DrawRope(Root.ActualWidth);
            UpdateEmpty();
            UpdateWindowShape();
        };
        if (!host.IsRopeVisible)
        {
            _appWindow.Hide();
        }
    }

    public bool AllowClose { get; set; }

    public void ShowRope()
    {
        WindowChrome.PlaceTopStrip(_hwnd, _appWindow, 72);
        _appWindow.Show();
        Activate();
        WindowChrome.ApplyOverlayStyles(_hwnd);
        WindowChrome.ApplyColorKey(_hwnd);
        EnsureClickThrough();
        UpdateEmpty();
        UpdateWindowShape();
    }

    public void HideRope() => _appWindow.Hide();

    public void UpdateEmpty()
    {
        var empty = _host.Pegs.Count == 0;
        EmptyTag.Visibility = Visibility.Collapsed; // never show the big empty card
        BrandButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        LayoutPegs();
        if (empty)
        {
            // Collapse the overlay so it cannot cover the desktop while idle.
            _appWindow.Hide();
            return;
        }

        if (_host.IsRopeVisible && !_appWindow.IsVisible)
        {
            WindowChrome.PlaceTopStrip(_hwnd, _appWindow, 72);
            _appWindow.Show();
            WindowChrome.ApplyOverlayStyles(_hwnd);
            WindowChrome.ApplyColorKey(_hwnd);
            EnsureClickThrough();
        }

        UpdateWindowShape();
    }

    public void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusPill.Opacity = 1;
        _statusTimer ??= CreateStatusTimer();
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (AllowClose)
        {
            _passThroughTimer?.Stop();
            WindowChrome.RemoveClickThrough(_hwnd);
            return;
        }

        args.Cancel = true;
        _host.SetRopeVisible(false);
    }

    private void EnsureClickThrough()
    {
        if (_clickThrough)
        {
            return;
        }

        WindowChrome.InstallClickThrough(_hwnd, IsInteractive);
        _clickThrough = true;
        _passThroughTimer ??= CreatePassThroughTimer();
        _passThroughTimer.Start();
        WindowChrome.RefreshPassThrough(_hwnd);
    }

    private bool IsInteractive(NativeMethods.POINT clientPixels)
    {
        if (Root.XamlRoot is null)
        {
            return true;
        }

        var scale = Root.XamlRoot.RasterizationScale;
        if (scale <= 0)
        {
            scale = 1;
        }

        return HitTest(new Point(clientPixels.X / scale, clientPixels.Y / scale));
    }

    private bool HitTest(Point dip)
    {
        if (Hits(BrandButton, dip, 4))
        {
            return true;
        }

        foreach (var peg in FindChildren<PegControl>(PegList))
        {
            if (Hits(peg, dip, 6))
            {
                return true;
            }
        }

        return false;
    }

    private bool Hits(FrameworkElement element, Point dip, double pad)
    {
        if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        var bounds = element.TransformToVisual(Root).TransformBounds(
            new Rect(-pad, -pad, element.ActualWidth + (pad * 2), element.ActualHeight + (pad * 2)));
        return bounds.Contains(dip);
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawRope(e.NewSize.Width);
        LayoutPegs();
    }

    private void DrawRope(double width)
    {
        if (width < 80)
        {
            return;
        }

        const double y = 28;
        const double sag = 16;
        var start = new Point(36, y);
        var control = new Point(width / 2, y + sag);
        var end = new Point(width - 36, y);
        RopeShadow.Data = Curve(new Point(start.X, start.Y + 2), new Point(control.X, control.Y + 2), new Point(end.X, end.Y + 2));
        RopeLine.Data = Curve(start, control, end);
        RopeHighlight.Data = Curve(new Point(start.X, start.Y - 1), new Point(control.X, control.Y - 1), new Point(end.X, end.Y - 1));
        Canvas.SetLeft(LeftKnob, 28);
        Canvas.SetTop(LeftKnob, y - 7);
        Canvas.SetLeft(RightKnob, width - 42);
        Canvas.SetTop(RightKnob, y - 7);
    }

    private void LayoutPegs()
    {
        if (_layingOut || PegScroll.ActualWidth <= 1)
        {
            return;
        }

        _layingOut = true;
        try
        {
            PegList.UpdateLayout();
            var overflow = PegList.ActualWidth > PegScroll.ActualWidth - 8;
            var mode = overflow ? ScrollMode.Enabled : ScrollMode.Disabled;
            var alignment = overflow ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            if (PegScroll.HorizontalScrollMode != mode)
            {
                PegScroll.HorizontalScrollMode = mode;
            }

            if (PegList.HorizontalAlignment != alignment)
            {
                PegList.HorizontalAlignment = alignment;
            }
        }
        finally
        {
            _layingOut = false;
        }
    }

    private void PegScroll_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(PegScroll).Properties.MouseWheelDelta;
        PegScroll.ChangeView(Math.Max(0, PegScroll.HorizontalOffset - delta), null, null, true);
        e.Handled = true;
    }


    private void UpdateWindowShape()
    {
        if (Root.XamlRoot is null)
        {
            return;
        }

        var scale = Root.XamlRoot.RasterizationScale;
        if (scale <= 0)
        {
            scale = 1;
        }

        var rects = new List<(int X, int Y, int Width, int Height)>();

        void AddElement(FrameworkElement element, double pad = 2)
        {
            if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                return;
            }

            var bounds = element.TransformToVisual(Root).TransformBounds(
                new Rect(-pad, -pad, element.ActualWidth + (pad * 2), element.ActualHeight + (pad * 2)));
            var x = (int)Math.Floor(bounds.X * scale);
            var y = (int)Math.Floor(bounds.Y * scale);
            var w = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
            var h = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
            rects.Add((x, y, w, h));
        }

        // Thin rope line only (not a tall white band)
        var ropeTop = (int)Math.Floor(18 * scale);
        var ropeHeight = Math.Max(8, (int)Math.Ceiling(14 * scale));
        rects.Add((0, ropeTop, (int)Math.Ceiling(Root.ActualWidth * scale), ropeHeight));

        AddElement(BrandButton, 4);
        if (EmptyTag.Visibility == Visibility.Visible)
        {
            AddElement(EmptyTag, 2);
        }

        if (StatusPill.Opacity > 0.05)
        {
            AddElement(StatusPill, 2);
        }

        foreach (var peg in FindChildren<PegControl>(PegList))
        {
            AddElement(peg, 4);
        }

        WindowChrome.SetWindowShape(_hwnd, rects);
    }

    private void Brand_Click(object sender, RoutedEventArgs e) => _host.ShowSettings();


    private DispatcherQueueTimer CreatePassThroughTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = true;
        timer.Interval = TimeSpan.FromMilliseconds(32);
        timer.Tick += (_, _) =>
        {
            WindowChrome.RefreshPassThrough(_hwnd);
            UpdateWindowShape();
        };
        return timer;
    }

    private DispatcherQueueTimer CreateStatusTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(1500);
        timer.Tick += (_, _) => StatusPill.Opacity = 0;
        return timer;
    }

    private static PathGeometry Curve(Point start, Point control, Point end)
    {
        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false,
            IsFilled = false,
        };
        figure.Segments.Add(new QuadraticBezierSegment
        {
            Point1 = control,
            Point2 = end,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindChildren<T>(child))
            {
                yield return nested;
            }
        }
    }
}
