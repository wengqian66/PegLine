using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PegLine.Hosting;
using PegLine.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PegLine.Views;

public sealed partial class PegControl : UserControl
{
    public static readonly DependencyProperty PegProperty = DependencyProperty.Register(
        nameof(Peg),
        typeof(PegView),
        typeof(PegControl),
        new PropertyMetadata(null, OnPegChanged));

    private Point? _press;
    private bool _dragging;

    public PegControl()
    {
        InitializeComponent();
    }

    public PegView? Peg
    {
        get => (PegView?)GetValue(PegProperty);
        set => SetValue(PegProperty, value);
    }

    private static void OnPegChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (PegControl)sender;
        if (args.OldValue is PegView previous)
        {
            previous.PropertyChanged -= control.OnViewPropertyChanged;
        }

        if (args.NewValue is PegView next)
        {
            next.PropertyChanged += control.OnViewPropertyChanged;
            control.Apply(next);
        }
    }

    private void OnViewPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Peg is not null)
        {
            Apply(Peg);
        }
    }

    private void Apply(PegView peg)
    {
        Spin.Angle = peg.Tilt;
        Photo.Source = peg.Thumbnail;
        Caption.Text = peg.DisplayName;
        ToolTipService.SetToolTip(this, peg.DisplayName);
        Placeholder.Visibility = peg.HasThumbnail ? Visibility.Collapsed : Visibility.Visible;
        Placeholder.Text = peg.Failed ? "无法预览" : "…";
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        CloseButton.Opacity = 1;
        Spin.Angle = 0;
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        CloseButton.Opacity = 0;
        if (Peg is not null)
        {
            Spin.Angle = Peg.Tilt;
        }
    }

    private void Card_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        _press = e.GetCurrentPoint(this).Position;
        CapturePointer(e.Pointer);
    }

    private async void Card_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_press is null || _dragging || Peg is null)
        {
            return;
        }

        var position = e.GetCurrentPoint(this).Position;
        var dx = position.X - _press.Value.X;
        var dy = position.Y - _press.Value.Y;
        if (dx * dx + dy * dy < 36)
        {
            return;
        }

        _dragging = true;
        _press = null;
        ReleasePointerCapture(e.Pointer);
        var point = e.GetCurrentPoint(this);
        try
        {
            DragStarting += OnDragStarting;
            var result = await StartDragAsync(point);
            AppHost.Current?.NoteDragResult(Peg, result);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            DragStarting -= OnDragStarting;
        }
    }

    private void Card_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var copy = _press is not null && !_dragging;
        _press = null;
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        if (copy && Peg is not null)
        {
            _ = AppHost.Current?.CopyPegAsync(Peg);
        }
    }

    private void Close_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _press = null;
        _dragging = true;
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Peg is not null)
        {
            AppHost.Current?.RemovePeg(Peg);
        }
    }

    private void OnDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (Peg is null)
        {
            args.Cancel = true;
            return;
        }

        var path = Peg.FilePath;
        var stored = Peg.Storage;
        args.AllowedOperations = DataPackageOperation.Copy | DataPackageOperation.Move;
        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.Properties.Title = Peg.DisplayName;
        if (stored is not null)
        {
            args.Data.SetStorageItems(new IStorageItem[] { stored }, readOnly: false);
            args.Data.SetBitmap(RandomAccessStreamReference.CreateFromFile(stored));
            return;
        }

        args.Data.SetDataProvider(StandardDataFormats.StorageItems, request => ProvideStorageItem(request, path));
        args.Data.SetDataProvider(StandardDataFormats.Bitmap, request => ProvideBitmap(request, path));
    }

    private static void ProvideStorageItem(DataProviderRequest request, string path) =>
        Defer(request, async () =>
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            request.SetData(new List<IStorageItem> { file });
        });

    private static void ProvideBitmap(DataProviderRequest request, string path) =>
        Defer(request, async () =>
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            request.SetData(RandomAccessStreamReference.CreateFromFile(file));
        });

    private static void Defer(DataProviderRequest request, Func<Task> write)
    {
        var deferral = request.GetDeferral();
        _ = CompleteAsync(write, deferral);
    }

    private static async Task CompleteAsync(Func<Task> write, DataProviderDeferral deferral)
    {
        try
        {
            await write();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            deferral.Complete();
        }
    }
}
