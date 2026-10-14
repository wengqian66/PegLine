namespace PegLine.Core;

/// <summary>
/// Everything PegLine remembers. Serialized to %LOCALAPPDATA%\PegLine\settings.json.
/// </summary>
public sealed class AppSettings
{
    public const int MinPegs = 1;
    public const int MaxPegsLimit = 40;
    public const string DefaultHotkey = "Ctrl+Alt+T";
    public const string DefaultScreenshotHotkey = "Ctrl+Shift+S";

    public bool IncludeDefaultScreenshotsFolder { get; set; } = true;

    public List<string> ExtraFolders { get; set; } = [];

    public int MaxPegs { get; set; } = 12;

    public string Hotkey { get; set; } = DefaultHotkey;

    /// <summary>Global hotkey that opens the Windows snipping overlay (same idea as Win+Shift+S).</summary>
    public string ScreenshotHotkey { get; set; } = DefaultScreenshotHotkey;

    public bool WatchClipboard { get; set; } = true;

    public bool DeleteFileOnRemove { get; set; }

    public bool RopeVisible { get; set; } = true;

    /// <summary>Overlay skin: "Dock" (side staging dock, default) or "Rope" (top clothesline).</summary>
    public string OverlaySkin { get; set; } = "Dock";

    /// <summary>
    /// Files the user took off the rope without deleting. A later Created event puts them back.
    /// </summary>
    public List<string> DismissedPaths { get; set; } = [];

    public void Normalize()
    {
        ExtraFolders ??= [];
        DismissedPaths ??= [];
        Hotkey ??= DefaultHotkey;
        ScreenshotHotkey ??= DefaultScreenshotHotkey;

        if (string.IsNullOrWhiteSpace(OverlaySkin)
            || (!OverlaySkin.Equals("Dock", StringComparison.OrdinalIgnoreCase)
                && !OverlaySkin.Equals("Rope", StringComparison.OrdinalIgnoreCase)))
        {
            OverlaySkin = "Dock";
        }
        else
        {
            OverlaySkin = OverlaySkin.Equals("Rope", StringComparison.OrdinalIgnoreCase) ? "Rope" : "Dock";
        }

        if (MaxPegs < MinPegs)
        {
            MaxPegs = MinPegs;
        }
        else if (MaxPegs > MaxPegsLimit)
        {
            MaxPegs = MaxPegsLimit;
        }

        ExtraFolders = ExtraFolders
            .Select(TryFull)
            .Where(static path => path is not null)
            .Select(static path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Hotkey = HotkeyGesture.TryParse(Hotkey, out var gesture)
            ? gesture.ToStorageString()
            : DefaultHotkey;

        ScreenshotHotkey = HotkeyGesture.TryParse(ScreenshotHotkey, out var shot)
            ? shot.ToStorageString()
            : DefaultScreenshotHotkey;

        DismissedPaths = DismissedPaths
            .Select(TryFull)
            .Where(static path => path is not null)
            .Select(static path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .TakeLast(500)
            .ToList();
    }

    public bool IsDismissed(string path)
    {
        var full = TryFull(path);
        return full is not null && DismissedPaths.Contains(full, StringComparer.OrdinalIgnoreCase);
    }

    public void Dismiss(string path)
    {
        var full = TryFull(path);
        if (full is null || IsDismissed(full))
        {
            return;
        }

        DismissedPaths.Add(full);
        if (DismissedPaths.Count > 500)
        {
            DismissedPaths.RemoveRange(0, DismissedPaths.Count - 500);
        }
    }

    public void Undismiss(string path)
    {
        var full = TryFull(path);
        if (full is null)
        {
            return;
        }

        DismissedPaths.RemoveAll(item => item.Equals(full, StringComparison.OrdinalIgnoreCase));
    }

        public IReadOnlyList<string> ResolveWatchFolders()
    {
        var folders = new List<string>();
        try
        {
            Directory.CreateDirectory(SavedFolder());
        }
        catch
        {
        }

        folders.Add(SavedFolder());

        // Keep watching the old project Saved folder if it still has files.
        try
        {
            var legacy = LegacySavedFolder();
            if (Directory.Exists(legacy)
                && !legacy.Equals(SavedFolder(), StringComparison.OrdinalIgnoreCase))
            {
                folders.Add(legacy);
            }
        }
        catch
        {
        }

        if (IncludeDefaultScreenshotsFolder)
        {
            try
            {
                Directory.CreateDirectory(DefaultScreenshotsFolder());
                folders.Add(DefaultScreenshotsFolder());
            }
            catch
            {
            }
        }

        folders.AddRange(ExtraFolders);
        return folders
            .Select(TryFull)
            .Where(static path => path is not null)
            .Select(static path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

        public static string SavedFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PegLine",
            "Saved");

    /// <summary>Old project-relative Saved folder; migrated into LocalAppData on first run.</summary>
    public static string LegacySavedFolder() =>
        Path.Combine(@"D:\wengqian\PegLine\PegLine", "Saved");

    public static string DefaultScreenshotsFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures", "Screenshots");

    private static string? TryFull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
