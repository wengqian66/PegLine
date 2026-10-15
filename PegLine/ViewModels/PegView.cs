using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using PegLine.Core;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PegLine.ViewModels;

public sealed class PegView : INotifyPropertyChanged
{
    private readonly List<IRandomAccessStream> _retainedPixels = [];
    private readonly DispatcherQueue _ui;
    private string _filePath;
    private string _displayName;
    private double _tilt;
    private BitmapImage? _thumbnail;
    private bool _failed;

    public PegView(PegRecord record, int index)
    {
        _ui = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Pegs are created on the UI thread.");
        Id = record.Id;
        Origin = record.Origin;
        _filePath = record.FilePath;
        _displayName = System.IO.Path.GetFileName(record.FilePath);
        _tilt = TiltFor(index);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public PegOrigin Origin { get; private set; }

    public string FilePath => _filePath;

    public string DisplayName => _displayName;

    public StorageFile? Storage { get; private set; }

    public double Tilt
    {
        get => _tilt;
        set
        {
            if (Math.Abs(_tilt - value) < 0.01)
            {
                return;
            }

            _tilt = value;
            OnPropertyChanged();
        }
    }

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public bool Failed
    {
        get => _failed;
        private set
        {
            if (_failed == value)
            {
                return;
            }

            _failed = value;
            OnPropertyChanged();
        }
    }

    public static double TiltFor(int index) => (index % 4) switch
    {
        0 => -2.4,
        1 => 1.8,
        2 => -1.1,
        _ => 2.6,
    };

    public void SetOrigin(PegOrigin origin) => Origin = origin;

    public void UpdatePath(string path)
    {
        _filePath = path;
        _displayName = System.IO.Path.GetFileName(path);
        Storage = null;
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(DisplayName));
    }

    /// <summary>
    /// WinUI decode for <see cref="PegControl"/>. The GDI rope draws its own thumbnails
    /// and does not call this — calling it at startup kept a decoded bitmap and a
    /// stream copy per peg that nothing on screen used.
    /// </summary>
    public async Task LoadThumbnailAsync()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(_filePath);
                using var source = await file.OpenReadAsync();
                var pixels = new InMemoryRandomAccessStream();
                await RandomAccessStream.CopyAsync(source, pixels);
                await DecodeOnUiAsync(file, pixels);
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 3)
                {
                    App.Log(ex);
                    PublishFailure();
                    return;
                }

                await Task.Delay(140);
            }
        }
    }

    private Task DecodeOnUiAsync(StorageFile file, InMemoryRandomAccessStream pixels)
    {
        var done = new TaskCompletionSource();
        if (!_ui.TryEnqueue(async () =>
            {
                try
                {
                    pixels.Seek(0);
                    var bitmap = new BitmapImage { DecodePixelWidth = 280 };
                    await bitmap.SetSourceAsync(pixels);
                    if (!RunOnUi(() =>
                        {
                            Storage = file;
                            _retainedPixels.Add(pixels);
                            if (_retainedPixels.Count > 2)
                            {
                                _retainedPixels.RemoveAt(0);
                            }

                            Thumbnail = bitmap;
                            Failed = false;
                            done.TrySetResult();
                        }))
                    {
                        done.TrySetException(new InvalidOperationException("UI dispatcher is shutting down."));
                    }
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            }))
        {
            done.TrySetResult();
        }

        return done.Task;
    }

    private void PublishFailure() => RunOnUi(() =>
    {
        Thumbnail = null;
        Failed = true;
    });

    private bool RunOnUi(Action action)
    {
        if (_ui.HasThreadAccess)
        {
            action();
            return true;
        }

        return _ui.TryEnqueue(() => action());
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
