using PegLine.Core;

namespace PegLine.Core.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Normalize_ClampsPegsAndRepairsHotkey()
    {
        var settings = new AppSettings
        {
            MaxPegs = 400,
            Hotkey = "not a key",
            ExtraFolders = ["  ", "relative-folder"],
        };

        settings.Normalize();

        Assert.Equal(AppSettings.MaxPegsLimit, settings.MaxPegs);
        Assert.Equal(AppSettings.DefaultHotkey, settings.Hotkey);
        Assert.Single(settings.ExtraFolders);
        Assert.True(Path.IsPathRooted(settings.ExtraFolders[0]));
    }

    [Fact]
    public void ResolveWatchFolders_IncludesDefaultAndExtras()
    {
        var extra = Path.Combine(Path.GetTempPath(), "pegline-extra-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings
        {
            IncludeDefaultScreenshotsFolder = true,
            ExtraFolders = [extra, extra],
        };
        settings.Normalize();

        var folders = settings.ResolveWatchFolders();

        Assert.Contains(settings.SavedFolder(), folders, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(folders, f => f.EndsWith(Path.Combine("Pictures", "Screenshots"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Path.GetFullPath(extra), folders, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(folders.Count, folders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Dismiss_IsCappedAndCaseInsensitive()
    {
        var settings = new AppSettings();
        var path = Path.Combine(Path.GetTempPath(), "Shot.PNG");
        settings.Dismiss(path);

        Assert.True(settings.IsDismissed(path.ToLowerInvariant()));
        settings.Undismiss(path.ToUpperInvariant());
        Assert.False(settings.IsDismissed(path));

        for (var i = 0; i < 520; i++)
        {
            settings.Dismiss(Path.Combine(Path.GetTempPath(), $"peg-{i}.png"));
        }

        settings.Normalize();
        Assert.Equal(500, settings.DismissedPaths.Count);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pegline-settings-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        var extra = Path.Combine(directory, "captures");
        var original = new AppSettings
        {
            IncludeDefaultScreenshotsFolder = false,
            ExtraFolders = [extra],
            MaxPegs = 8,
            Hotkey = "Ctrl+Shift+S",
            WatchClipboard = true,
            DeleteFileOnRemove = true,
            RopeVisible = false,
            SavedFolderPath = Path.Combine(directory, "MySaved"),
        };
        original.Dismiss(Path.Combine(extra, "old.png"));

        store.Save(original);
        var loaded = store.Load();

        Assert.False(loaded.IncludeDefaultScreenshotsFolder);
        Assert.Equal(Path.GetFullPath(extra), Assert.Single(loaded.ExtraFolders));
        Assert.Equal(8, loaded.MaxPegs);
        Assert.Equal("Ctrl+Shift+S", loaded.Hotkey);
        Assert.True(loaded.WatchClipboard);
        Assert.True(loaded.DeleteFileOnRemove);
        Assert.False(loaded.RopeVisible);
        Assert.Equal(Path.GetFullPath(Path.Combine(directory, "MySaved")), loaded.SavedFolderPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(Path.Combine(directory, "MySaved")), loaded.SavedFolder(), StringComparer.OrdinalIgnoreCase);
        Assert.True(loaded.IsDismissed(Path.Combine(extra, "old.png")));
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "pegline-missing-" + Guid.NewGuid().ToString("N"), "settings.json"));

        var settings = store.Load();

        Assert.True(settings.IncludeDefaultScreenshotsFolder);
        Assert.Equal(12, settings.MaxPegs);
        Assert.Equal(AppSettings.DefaultHotkey, settings.Hotkey);
        Assert.True(settings.WatchClipboard);
        Assert.False(settings.DeleteFileOnRemove);
        Assert.True(settings.RopeVisible);
        Assert.Null(settings.SavedFolderPath);
        Assert.Equal(AppSettings.DefaultSavedFolder(), settings.SavedFolder(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SavedFolder_CustomPath_IsUsedInWatchFolders()
    {
        var custom = Path.Combine(Path.GetTempPath(), "pegline-saved-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings
        {
            IncludeDefaultScreenshotsFolder = false,
            SavedFolderPath = custom,
        };
        settings.Normalize();

        Assert.Equal(Path.GetFullPath(custom), settings.SavedFolder(), StringComparer.OrdinalIgnoreCase);
        var folders = settings.ResolveWatchFolders();
        Assert.Contains(Path.GetFullPath(custom), folders, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SavedFolder_DefaultEqualsLocalAppData()
    {
        var settings = new AppSettings();
        settings.Normalize();
        Assert.Null(settings.SavedFolderPath);
        Assert.Equal(AppSettings.DefaultSavedFolder(), settings.SavedFolder());
        Assert.Contains("PegLine", settings.SavedFolder());
        Assert.EndsWith("Saved", settings.SavedFolder(), StringComparison.OrdinalIgnoreCase);
    }
}

