using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PegLine.Core;
using PegLine.Platform;
using PegLine.Services;
using PegLine.ViewModels;
using PegLine.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PegLine.Hosting;

/// <summary>
/// Owns the tray, the rope, the folder watcher, and the clipboard listener.
/// All peg list changes happen on the UI thread.
/// </summary>
public sealed class AppHost
{
    private readonly SettingsStore _store = new();
    private readonly PegBoard _board = new();
    private readonly ScreenshotWatcher _watcher = new();
    private readonly DispatcherQueue _dispatcher;
    private AppSettings _settings = new();
    private MessageWindow? _messages;
    private TrayIcon? _tray;
    private HotkeyHook? _hotkey;
    private HotkeyHook? _screenshotHotkey;
    private ClipboardMonitor? _clipboard;
    private RopeOverlayForm? _rope;
    private DockOverlayForm? _dock;
    private SnipActionBarForm? _actionBar;
    private Microsoft.UI.Xaml.Window? _keepAlive;
    private SettingsWindow? _settingsWindow;
    private FileStream? _instanceLock;
    private bool _quitting;
    private long _suppressClipboardUntil;
    private long _lastFolderPegTick;
    private int _clipboardBusy;
    private readonly HashSet<string> _quietWatchPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _contentHashByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pathByContentHash = new(StringComparer.OrdinalIgnoreCase);

    public AppHost()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Pegs.CollectionChanged += OnPegsChanged;
    }

    public static AppHost? Current { get; private set; }

    public ObservableCollection<PegView> Pegs { get; } = [];

    public AppSettings Settings => _settings;

    public bool IsRopeVisible => _settings.RopeVisible;

    public bool IsDockSkin =>
        string.Equals(_settings.OverlaySkin, "Dock", StringComparison.OrdinalIgnoreCase);

    public bool IsActionBarOpen => _actionBar?.IsOpen == true;

    public string OverlaySkin => _settings.OverlaySkin;

    public string HotkeyLabel =>
        HotkeyGesture.TryParse(_settings.Hotkey, out var gesture)
            ? gesture.ToDisplayString()
            : HotkeyGesture.Default.ToDisplayString();

    public string HotkeyStatusText =>
        _hotkey?.IsRegistered == true
            ? "显示/隐藏热键可用。"
            : "显示/隐藏热键被占用。绳子仍可从托盘打开。";

    public string ScreenshotHotkeyLabel =>
        HotkeyGesture.TryParse(_settings.ScreenshotHotkey, out var gesture)
            ? gesture.ToDisplayString()
            : HotkeyGesture.Parse(AppSettings.DefaultScreenshotHotkey).ToDisplayString();

    public string ScreenshotHotkeyStatusText =>
        _screenshotHotkey?.IsRegistered == true
            ? "截图热键可用：按下后打开系统截图，截完会自动挂上绳。"
            : "截图热键被占用。仍可用 Win + Shift + S（需打开剪贴板监视）。";

    public void Start()
    {
        if (!TryLockSingleInstance())
        {
            Application.Current.Exit();
            return;
        }

        Current = this;
        try
        {
            _settings = _store.Load();
            EnsureDefaultFolder();
            MigrateLegacySaved();
            _messages = new MessageWindow();
            _hotkey = new HotkeyHook(_messages, HotkeyHook.ToggleId);
            _hotkey.Pressed += ToggleRope;
            _screenshotHotkey = new HotkeyHook(_messages, HotkeyHook.ScreenshotId);
            _screenshotHotkey.Pressed += LaunchScreenClip;
            _clipboard = new ClipboardMonitor(_messages);
            _clipboard.ClipboardChanged += OnClipboardChanged;
            _tray = new TrayIcon(
                _messages,
                () => IsRopeVisible,
                () => HotkeyLabel,
                ToggleRope,
                ShowSettings,
                Quit);
            _watcher.ImageCreated += OnImageCreated;
            _watcher.ImageChanged += OnImageChanged;
            _watcher.ImageDeleted += OnImageDeleted;
            _watcher.ImageRenamed += OnImageRenamed;
            FillFromFolders();
            _watcher.SetFolders(_settings.ResolveWatchFolders());
            _keepAlive = new Microsoft.UI.Xaml.Window();
            _keepAlive.AppWindow.Hide();
            _actionBar = new SnipActionBarForm(this);
            CreateOverlayForSkin();

            ResumeHotkey();
            _clipboard.SetEnabled(_settings.WatchClipboard);
            _tray.Add();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Quit();
        }
    }

    public void ToggleRope() => SetRopeVisible(!_settings.RopeVisible);

    public void SetRopeVisible(bool visible)
    {
        _settings.RopeVisible = visible;
        _store.Save(_settings);
        if (visible)
        {
            if (IsDockSkin)
            {
                _dock?.ShowDock();
            }
            else
            {
                _rope?.ShowRope();
            }
        }
        else
        {
            _actionBar?.Dismiss();
            if (IsDockSkin)
            {
                _dock?.HideDock();
            }
            else
            {
                _rope?.HideRope();
            }
        }
    }

    public void SetOverlaySkin(string skin)
    {
        var normalized = string.Equals(skin, "Rope", StringComparison.OrdinalIgnoreCase) ? "Rope" : "Dock";
        if (string.Equals(_settings.OverlaySkin, normalized, StringComparison.OrdinalIgnoreCase)
            && ((normalized == "Dock" && _dock is not null) || (normalized == "Rope" && _rope is not null)))
        {
            return;
        }

        _settings.OverlaySkin = normalized;
        _store.Save(_settings);
        _actionBar?.Dismiss();
        DisposeOverlays();
        CreateOverlayForSkin();
        OverlayStatus("已切换为" + (IsDockSkin ? "侧边暂存坞" : "顶部晾衣绳"));
    }

    public void NotifyActionBarChanged()
    {
        _dock?.SetKeepOpen(IsActionBarOpen);
    }

    private void CreateOverlayForSkin()
    {
        if (IsDockSkin)
        {
            _dock = new DockOverlayForm(this);
            if (_settings.RopeVisible)
            {
                _dock.ShowDock();
            }
        }
        else
        {
            _rope = new RopeOverlayForm(this);
            if (_settings.RopeVisible)
            {
                _rope.ShowRope();
            }
        }
    }

    private void DisposeOverlays()
    {
        if (_rope is not null)
        {
            _rope.AllowClose = true;
            _rope.Close();
            _rope = null;
        }

        if (_dock is not null)
        {
            _dock.AllowClose = true;
            _dock.Close();
            _dock = null;
        }
    }

    private void OverlayStatus(string text)
    {
        if (IsDockSkin)
        {
            _dock?.ShowStatus(text);
        }
        else
        {
            _rope?.ShowStatus(text);
        }
    }

    private IntPtr OverlayHwnd =>
        IsDockSkin ? (_dock?.Hwnd ?? IntPtr.Zero) : (_rope?.Hwnd ?? IntPtr.Zero);

    public void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Reload();
        _settingsWindow.Activate();
    }

    public async Task CopyPegAsync(PegView peg)
    {
        _suppressClipboardUntil = Environment.TickCount64 + 1200;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(peg.FilePath);
            var done = new TaskCompletionSource();
            if (!_dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        ClipboardImages.CopyFile(file);
                        OverlayStatus("已复制");
                        done.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        done.TrySetException(ex);
                    }
                }))
            {
                return;
            }

            await done.Task;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _dispatcher.TryEnqueue(() => OverlayStatus("没能复制"));
        }
    }


    public void DiscardPeg(PegView peg)
    {
        if (IsDockSkin)
        {
            if (_dock is not null)
            {
                _dock.BeginDiscard(peg);
                return;
            }
        }
        else if (_rope is not null)
        {
            _rope.BeginDiscard(peg);
            return;
        }

        RemovePeg(peg);
    }

    public void RemovePeg(PegView peg)
    {
        if (_board.Remove(peg.Id) is null)
        {
            return;
        }

        ForgetContentHash(peg.FilePath);
        Pegs.Remove(peg);
        var savedRoot = AppSettings.SavedFolder();
        var underSaved = peg.FilePath.StartsWith(savedRoot, StringComparison.OrdinalIgnoreCase);
        var deleteFile = underSaved || peg.Origin == PegOrigin.Clipboard || _settings.DeleteFileOnRemove;
        if (!deleteFile)
        {
            _settings.Dismiss(peg.FilePath);
            _store.Save(_settings);
            OverlayStatus("已从绳子上取下");
            return;
        }

        try
        {
            if (File.Exists(peg.FilePath))
            {
                File.Delete(peg.FilePath);
            }

            OverlayStatus(underSaved ? "已从 Saved 删除" : (peg.Origin == PegOrigin.Clipboard ? "已删缓存" : "已删文件"));
        }
        catch (Exception ex)
        {
            App.Log(ex);
            OverlayStatus("没能删除文件");
        }
    }


    public void OpenPeg(PegView peg)
    {
        try
        {
            if (!File.Exists(peg.FilePath))
            {
                OverlayStatus("文件不在了");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = peg.FilePath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            App.Log(ex);
            OverlayStatus("打不开");
        }
    }


    public void SavePegToDownloads(PegView peg)
    {
        try
        {
            if (!File.Exists(peg.FilePath))
            {
                OverlayStatus("文件不在了");
                return;
            }

            var folder = AppSettings.SavedFolder();
            Directory.CreateDirectory(folder);

            if (peg.FilePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                OverlayStatus("已在 Saved");
                return;
            }

            var oldPath = peg.FilePath;
            var name = Path.GetFileName(oldPath);
            var dest = Path.Combine(folder, name);
            if (File.Exists(dest))
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                dest = Path.Combine(folder, $"{stem}-{DateTime.Now:HHmmss}{ext}");
            }

            var destFull = Path.GetFullPath(dest);
            _quietWatchPaths.Add(destFull);
            File.Copy(oldPath, dest, overwrite: false);

            // Remap onto Saved and mark as folder-origin so discard deletes Saved file.
            var updated = _board.UpdatePath(oldPath, destFull);
            if (updated is not null)
            {
                updated.Origin = PegOrigin.Folder;
                peg.UpdatePath(updated.FilePath);
                peg.SetOrigin(PegOrigin.Folder);
                RemapContentHash(oldPath, destFull);
                Pegs.Remove(peg);
                Pegs.Insert(0, peg);
            }

            if (peg.Origin == PegOrigin.Clipboard || oldPath.IndexOf("clipboard", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    if (File.Exists(oldPath)
                        && !oldPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(oldPath);
                    }
                }
                catch (Exception ex)
                {
                    App.Log(ex);
                }
            }

            OverlayStatus("已存到 Saved");
        }
        catch (Exception ex)
        {
            App.Log(ex);
            OverlayStatus("没能保存");
        }
    }

    public void RemovePegsBeside(PegView peg, bool left)
    {
        var index = Pegs.ToList().FindIndex(p => p.Id == peg.Id);
        if (index < 0)
        {
            return;
        }

        var targets = left ? Pegs.Take(index).ToList() : Pegs.Skip(index + 1).ToList();
        foreach (var item in targets)
        {
            DiscardPeg(item);
        }

        OverlayStatus(left ? "已取下左侧" : "已取下右侧");
    }

    public void BeginFileDrag(PegView peg)
    {
        try
        {
            if (!File.Exists(peg.FilePath))
            {
                return;
            }

            FileDrag.Begin(OverlayHwnd, peg.FilePath);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _ = CopyPegAsync(peg);
        }
    }

    public void NoteDragResult(PegView peg, DataPackageOperation result)
    {
        if (result != DataPackageOperation.Move || File.Exists(peg.FilePath))
        {
            return;
        }

        _board.Remove(peg.Id);
        Pegs.Remove(peg);
    }

    public void SetIncludeDefaultFolder(bool include)
    {
        if (_settings.IncludeDefaultScreenshotsFolder == include)
        {
            return;
        }

        _settings.IncludeDefaultScreenshotsFolder = include;
        _store.Save(_settings);
        EnsureDefaultFolder();
        Rewatch();
    }

    public void AddExtraFolder(string path)
    {
        var full = Path.GetFullPath(path);
        if (_settings.ExtraFolders.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.ExtraFolders.Add(full);
        _settings.Normalize();
        _store.Save(_settings);
        Rewatch();
    }

    public void RemoveExtraFolder(string path)
    {
        var full = Path.GetFullPath(path);
        var removed = _settings.ExtraFolders.RemoveAll(item => item.Equals(full, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            return;
        }

        _store.Save(_settings);
        Rewatch();
    }

    public void SetMaxPegs(int value)
    {
        value = Math.Clamp(value, 4, 30);
        if (value == _settings.MaxPegs)
        {
            return;
        }

        _settings.MaxPegs = value;
        _store.Save(_settings);
        if (Pegs.Count > value)
        {
            DropViews(_board.TrimTo(value));
        }
        else
        {
            FillFromFolders();
        }
    }

    public void SetWatchClipboard(bool enabled)
    {
        if (_settings.WatchClipboard == enabled)
        {
            return;
        }

        _settings.WatchClipboard = enabled;
        _store.Save(_settings);
        _clipboard?.SetEnabled(enabled);
    }

    public void SetDeleteFileOnRemove(bool enabled)
    {
        if (_settings.DeleteFileOnRemove == enabled)
        {
            return;
        }

        _settings.DeleteFileOnRemove = enabled;
        _store.Save(_settings);
    }

    public void PauseHotkey()
    {
        _hotkey?.Unregister();
        _screenshotHotkey?.Unregister();
    }

    public bool ResumeHotkey()
    {
        var okToggle = false;
        if (_hotkey is not null && HotkeyGesture.TryParse(_settings.Hotkey, out var toggle))
        {
            okToggle = _hotkey.Register(toggle);
        }

        if (_screenshotHotkey is not null && HotkeyGesture.TryParse(_settings.ScreenshotHotkey, out var shot))
        {
            _screenshotHotkey.Register(shot);
        }

        return okToggle;
    }

    public bool TrySetHotkey(HotkeyGesture gesture, out string? error)
    {
        _settings.Hotkey = gesture.ToStorageString();
        _store.Save(_settings);
        var registered = _hotkey?.Register(gesture) == true;
        // keep screenshot hotkey registered
        if (_screenshotHotkey is not null && HotkeyGesture.TryParse(_settings.ScreenshotHotkey, out var shot))
        {
            _screenshotHotkey.Register(shot);
        }

        error = registered ? null : "这个热键被其他程序占用了。";
        return registered;
    }

    public bool TrySetScreenshotHotkey(HotkeyGesture gesture, out string? error)
    {
        if (HotkeyGesture.TryParse(_settings.Hotkey, out var toggle)
            && toggle.Modifiers == gesture.Modifiers
            && toggle.VirtualKey == gesture.VirtualKey)
        {
            error = "不能和「显示/隐藏绳子」用同一个热键。";
            return false;
        }

        _settings.ScreenshotHotkey = gesture.ToStorageString();
        _store.Save(_settings);
        var registered = _screenshotHotkey?.Register(gesture) == true;
        if (_hotkey is not null && HotkeyGesture.TryParse(_settings.Hotkey, out var keep))
        {
            _hotkey.Register(keep);
        }

        error = registered ? null : "这个热键被其他程序占用了。";
        return registered;
    }

    public void LaunchScreenClip()
    {
        try
        {
            // Same overlay family as Win+Shift+S; result goes to clipboard → PegLine hangs it.
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-screenclip:",
                UseShellExecute = true,
            });
            OverlayStatus("框选截图…");
        }
        catch (Exception ex)
        {
            App.Log(ex);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "SnippingTool.exe",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex2)
            {
                App.Log(ex2);
                _dispatcher.TryEnqueue(() => OverlayStatus("打不开截图工具"));
            }
        }
    }

    public IReadOnlyList<FolderRow> FolderRows() =>
        _settings.ExtraFolders
            .Select(path => new FolderRow
            {
                FullPath = path,
                Status = Directory.Exists(path) ? "正在监听" : "找不到这个文件夹",
            })
            .ToList();

    public void Quit()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        Current = null;
        try
        {
            _tray?.Dispose();
            _clipboard?.Dispose();
            _hotkey?.Dispose();
            _screenshotHotkey?.Dispose();
            _watcher.Dispose();
            _messages?.Dispose();
            if (_settingsWindow is not null)
            {
                _settingsWindow.Close();
                _settingsWindow = null;
            }

            _actionBar?.Close();
            _actionBar = null;
            DisposeOverlays();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            ReleaseInstanceLock();
            Application.Current.Exit();
        }
    }

    private void MigrateLegacySaved()
    {
        try
        {
            var dest = AppSettings.SavedFolder();
            Directory.CreateDirectory(dest);
            var legacy = AppSettings.LegacySavedFolder();
            if (!Directory.Exists(legacy)
                || legacy.Equals(dest, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(legacy))
            {
                if (!ImageFiles.IsSupported(file))
                {
                    continue;
                }

                var name = Path.GetFileName(file);
                var target = Path.Combine(dest, name);
                if (File.Exists(target))
                {
                    continue;
                }

                File.Copy(file, target, overwrite: false);
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }


    private static string? ComputeFileHash(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var hash = SHA256.HashData(fs);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return null;
        }
    }

    private void RememberContentHash(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var hash = ComputeFileHash(full);
            if (hash is null)
            {
                return;
            }

            if (_contentHashByPath.TryGetValue(full, out var old) && old != hash)
            {
                if (_pathByContentHash.TryGetValue(old, out var mapped)
                    && mapped.Equals(full, StringComparison.OrdinalIgnoreCase))
                {
                    _pathByContentHash.Remove(old);
                }
            }

            _contentHashByPath[full] = hash;
            _pathByContentHash[hash] = full;
        }
        catch
        {
        }
    }

    private void RemapContentHash(string oldPath, string newPath)
    {
        try
        {
            var oldFull = Path.GetFullPath(oldPath);
            var newFull = Path.GetFullPath(newPath);
            if (_contentHashByPath.TryGetValue(oldFull, out var hash))
            {
                _contentHashByPath.Remove(oldFull);
                _contentHashByPath[newFull] = hash;
                _pathByContentHash[hash] = newFull;
            }
            else
            {
                RememberContentHash(newFull);
            }
        }
        catch
        {
            RememberContentHash(newPath);
        }
    }

    private void ForgetContentHash(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (_contentHashByPath.Remove(full, out var hash))
            {
                if (_pathByContentHash.TryGetValue(hash, out var mapped)
                    && mapped.Equals(full, StringComparison.OrdinalIgnoreCase))
                {
                    _pathByContentHash.Remove(hash);
                }
            }
        }
        catch
        {
        }
    }

    private bool TryFindDuplicateByContent(string path, out string? existingPath)
    {
        existingPath = null;
        try
        {
            var full = Path.GetFullPath(path);
            var hash = ComputeFileHash(full);
            if (hash is null)
            {
                return false;
            }

            if (_pathByContentHash.TryGetValue(hash, out var mapped)
                && !mapped.Equals(full, StringComparison.OrdinalIgnoreCase)
                && File.Exists(mapped)
                && _board.ContainsPath(mapped))
            {
                existingPath = mapped;
                return true;
            }

            if (_board.ContainsPath(full))
            {
                RememberContentHash(full);
            }
        }
        catch
        {
        }

        return false;
    }

    private void Rewatch()
    {
        _watcher.SetFolders(_settings.ResolveWatchFolders());
        SyncFolderPegs();
    }

    private void SyncFolderPegs()
    {
        var folders = _settings.ResolveWatchFolders();
        foreach (var peg in Pegs.ToList())
        {
            if (peg.Origin == PegOrigin.Clipboard)
            {
                continue;
            }

            if (!File.Exists(peg.FilePath) || !IsInside(peg.FilePath, folders))
            {
                _board.Remove(peg.Id);
                Pegs.Remove(peg);
            }
        }

        FillFromFolders();
        DropViews(_board.TrimTo(_settings.MaxPegs));
    }

    private void FillFromFolders()
    {
        var recent = FolderScan.RecentImages(_settings.ResolveWatchFolders(), _settings.MaxPegs);
        foreach (var path in recent)
        {
            if (Pegs.Count >= _settings.MaxPegs)
            {
                break;
            }

            if (_settings.IsDismissed(path))
            {
                continue;
            }

            if (TryFindDuplicateByContent(path, out _))
            {
                continue;
            }

            var added = _board.TryAppend(path, PegOrigin.Folder, _settings.MaxPegs);
            if (added is not null)
            {
                var view = new PegView(added.Peg, Pegs.Count);
                Pegs.Add(view);
                RememberContentHash(path);
                _ = view.LoadThumbnailAsync();
            }
        }
    }

    private void OnImageCreated(object? sender, string path) => _dispatcher.TryEnqueue(() =>
    {
        try
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return;
            }

            // SavePegToDownloads already remapped the peg onto this path.
            if (_quietWatchPaths.Remove(full))
            {
                return;
            }

            if (_settings.IsDismissed(path))
            {
                _settings.Undismiss(path);
                _store.Save(_settings);
            }

            if (!File.Exists(path))
            {
                return;
            }

            if (TryFindDuplicateByContent(full, out var existing) && existing is not null)
            {
                var bumped = Mirror(_board.AddOrBump(existing, PegOrigin.Folder, _settings.MaxPegs));
                _lastFolderPegTick = Environment.TickCount64;
                AfterPegMirrored(bumped, fromClipboard: false);
                return;
            }

            var added = Mirror(_board.AddOrBump(path, PegOrigin.Folder, _settings.MaxPegs));
            RememberContentHash(full);
            _lastFolderPegTick = Environment.TickCount64;
            AfterPegMirrored(added, fromClipboard: false);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    });

    private void OnImageChanged(object? sender, string path) => _dispatcher.TryEnqueue(() =>
    {
        try
        {
            if (!_board.ContainsPath(path))
            {
                return;
            }

            Mirror(_board.AddOrBump(path, PegOrigin.Folder, _settings.MaxPegs));
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    });

    private void OnImageDeleted(object? sender, string path) => _dispatcher.TryEnqueue(() =>
    {
        var removed = _board.RemoveByPath(path);
        if (removed is null)
        {
            return;
        }

        var view = Pegs.FirstOrDefault(item => item.Id == removed.Id);
        if (view is not null)
        {
            Pegs.Remove(view);
        }
    });

    private void OnImageRenamed(object? sender, ImageRenamedEventArgs e) => _dispatcher.TryEnqueue(() =>
    {
        try
        {
            var updated = _board.UpdatePath(e.OldPath, e.NewPath);
            if (updated is not null)
            {
                var view = Pegs.FirstOrDefault(item => item.Id == updated.Id);
                if (view is not null)
                {
                    view.UpdatePath(updated.FilePath);
                    _ = view.LoadThumbnailAsync();
                }

                if (_settings.IsDismissed(e.OldPath))
                {
                    _settings.Undismiss(e.OldPath);
                    _settings.Dismiss(e.NewPath);
                    _store.Save(_settings);
                }

                return;
            }

            if (_settings.IsDismissed(e.OldPath))
            {
                _settings.Undismiss(e.OldPath);
                _settings.Dismiss(e.NewPath);
                _store.Save(_settings);
                return;
            }

            if (ImageFiles.IsSupported(e.NewPath) && File.Exists(e.NewPath))
            {
                Mirror(_board.AddOrBump(e.NewPath, PegOrigin.Folder, _settings.MaxPegs));
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    });

    private void OnClipboardChanged()
    {
        if (Environment.TickCount64 < _suppressClipboardUntil || !_settings.WatchClipboard)
        {
            return;
        }

        if (Interlocked.Exchange(ref _clipboardBusy, 1) == 1)
        {
            return;
        }

        _ = CaptureClipboardAsync();
    }

    private async Task CaptureClipboardAsync()
    {
        try
        {
            var allowBitmap = Environment.TickCount64 - _lastFolderPegTick >= 2500;
            var captured = await ClipboardImages.TryCaptureAsync(SettingsStore.ClipboardCacheDirectory(), allowBitmap);
            _dispatcher.TryEnqueue(() =>
            {
                if (captured is null || _settings.IsDismissed(captured.Value.Path))
                {
                    return;
                }

                var view = Mirror(_board.AddOrBump(captured.Value.Path, captured.Value.Origin, _settings.MaxPegs));
                RememberContentHash(captured.Value.Path);
                // Treat clipboard StorageItems / bitmap hang like a folder peg so the
                // follow-up Win+Shift+S bitmap event does not hang a second copy.
                _lastFolderPegTick = Environment.TickCount64;
                AfterPegMirrored(view, fromClipboard: true);
            });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            Interlocked.Exchange(ref _clipboardBusy, 0);
        }
    }

    private PegView? Mirror(PegAddResult result)
    {
        PegView? view;
        if (result.Kind == PegAddKind.Updated)
        {
            view = Pegs.FirstOrDefault(item => item.Id == result.Peg.Id);
            if (view is not null)
            {
                Pegs.Remove(view);
                Pegs.Insert(0, view);
                _ = view.LoadThumbnailAsync();
            }
        }
        else
        {
            view = new PegView(result.Peg, 0);
            Pegs.Insert(0, view);
            _ = view.LoadThumbnailAsync();
        }

        DropViews(result.Trimmed);
        return view;
    }

    private void AfterPegMirrored(PegView? view, bool fromClipboard)
    {
        if (view is null || !IsDockSkin)
        {
            return;
        }

        _dock?.NoteNewPeg();
        if (fromClipboard || IsDockSkin)
        {
            _actionBar?.ShowFor(view);
        }
    }

    private void DropViews(IReadOnlyList<PegRecord> records)
    {
        foreach (var record in records)
        {
            var view = Pegs.FirstOrDefault(item => item.Id == record.Id);
            if (view is not null)
            {
                Pegs.Remove(view);
            }
        }
    }

    private void OnPegsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        for (var index = 0; index < Pegs.Count; index++)
        {
            Pegs[index].Tilt = PegView.TiltFor(index);
        }

        if (IsDockSkin) { _dock?.UpdateEmpty(); } else { _rope?.UpdateEmpty(); }
    }

    private void EnsureDefaultFolder()
    {
        if (!_settings.IncludeDefaultScreenshotsFolder)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(AppSettings.DefaultScreenshotsFolder());
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private bool TryLockSingleInstance()
    {
        try
        {
            var directory = SettingsStore.DefaultDirectory();
            Directory.CreateDirectory(directory);
            _instanceLock = new FileStream(
                Path.Combine(directory, "instance.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ReleaseInstanceLock()
    {
        _instanceLock?.Dispose();
        _instanceLock = null;
    }

    private static bool IsInside(string file, IReadOnlyList<string> folders)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(file));
        return directory is not null && folders.Any(folder =>
            string.Equals(Path.GetFullPath(folder), directory, StringComparison.OrdinalIgnoreCase));
    }
}
