namespace PegLine.Core;

public sealed class ImageRenamedEventArgs : EventArgs
{
    public required string OldPath { get; init; }

    public required string NewPath { get; init; }
}

/// <summary>
/// Watches screenshot folders. Created and Changed are debounced until the file can be opened,
/// because Snipping Tool and Win+PrtScn write the file in several steps.
/// </summary>
public sealed class ScreenshotWatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _settleDelay;
    private bool _disposed;

    public ScreenshotWatcher(TimeSpan? settleDelay = null)
    {
        _settleDelay = settleDelay ?? TimeSpan.FromMilliseconds(450);
    }

    public event EventHandler<string>? ImageCreated;

    public event EventHandler<string>? ImageChanged;

    public event EventHandler<string>? ImageDeleted;

    public event EventHandler<ImageRenamedEventArgs>? ImageRenamed;

    public void SetFolders(IEnumerable<string> folders)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            StopWatchers();
            foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    continue;
                }

                var watcher = new FileSystemWatcher(folder)
                {
                    Filter = "*.*",
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = false,
                };
                watcher.Created += OnCreated;
                watcher.Changed += OnChanged;
                watcher.Deleted += OnDeleted;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopWatchers();
            foreach (var pending in _pending.Values)
            {
                pending.Cancellation.Cancel();
            }

            _pending.Clear();
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs e) => Schedule(e.FullPath, created: true);

    private void OnChanged(object sender, FileSystemEventArgs e) => Schedule(e.FullPath, created: false);

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (!ImageFiles.IsSupported(e.FullPath))
        {
            return;
        }

        CancelPending(e.FullPath);
        ImageDeleted?.Invoke(this, e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        CancelPending(e.OldFullPath);
        CancelPending(e.FullPath);

        var oldImage = ImageFiles.IsSupported(e.OldFullPath);
        var newImage = ImageFiles.IsSupported(e.FullPath);
        if (oldImage && newImage)
        {
            ImageRenamed?.Invoke(this, new ImageRenamedEventArgs
            {
                OldPath = e.OldFullPath,
                NewPath = e.FullPath,
            });
            return;
        }

        if (oldImage)
        {
            ImageDeleted?.Invoke(this, e.OldFullPath);
        }

        if (newImage)
        {
            Schedule(e.FullPath, created: true);
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        if (sender is not FileSystemWatcher watcher)
        {
            return;
        }

        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or ArgumentException)
        {
            // The folder was removed. SetFolders is called again when settings change.
        }
    }

    private void Schedule(string path, bool created)
    {
        if (!ImageFiles.IsSupported(path))
        {
            return;
        }

        Pending pending;
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_pending.TryGetValue(path, out var existing))
            {
                existing.Created |= created;
                existing.Cancellation.Cancel();
                cancellation = new CancellationTokenSource();
                existing.Cancellation = cancellation;
                pending = existing;
            }
            else
            {
                cancellation = new CancellationTokenSource();
                pending = new Pending
                {
                    Created = created,
                    Cancellation = cancellation,
                };
                _pending[path] = pending;
            }
        }

        var token = cancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_settleDelay, token).ConfigureAwait(false);
                if (!await ImageFiles.WaitUntilReadable(path, token).ConfigureAwait(false))
                {
                    return;
                }

                if (pending.Created)
                {
                    ImageCreated?.Invoke(this, path);
                }
                else
                {
                    ImageChanged?.Invoke(this, path);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // A newer write replaced this wait.
            }
            finally
            {
                lock (_gate)
                {
                    if (_pending.TryGetValue(path, out var current) && ReferenceEquals(current.Cancellation, cancellation))
                    {
                        _pending.Remove(path);
                    }
                }

                cancellation.Dispose();
            }
        }, CancellationToken.None);
    }

    private void CancelPending(string path)
    {
        lock (_gate)
        {
            if (_pending.Remove(path, out var pending))
            {
                pending.Cancellation.Cancel();
            }
        }
    }

    private void StopWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnCreated;
            watcher.Changed -= OnChanged;
            watcher.Deleted -= OnDeleted;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ScreenshotWatcher));
        }
    }

    private sealed class Pending
    {
        public bool Created { get; set; }

        public required CancellationTokenSource Cancellation { get; set; }
    }
}
