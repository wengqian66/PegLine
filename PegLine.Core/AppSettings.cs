namespace PegLine.Core;

/// <summary>
/// Everything PegLine remembers. Serialized to %LOCALAPPDATA%\PegLine\settings.json.
/// </summary>
public sealed class AppSettings
{
    public const int MinPegs = 1;
    public const int MaxPegsLimit = 40;
    public const string DefaultHotkey = "Ctrl+Alt+T";

    public bool IncludeDefaultScreenshotsFolder { get; set; } = true;

    public List<string> ExtraFolders { get; set; } = [];

    public int MaxPegs { get; set; } = 12;

    public string Hotkey { get; set; } = DefaultHotkey;

    public bool WatchClipboard { get; set; } = true;

    public bool DeleteFileOnRemove { get; set; }

    public bool RopeVisible { get; set; } = true;

    /// <summary>Overlay skin. Only "Rope" (top clothesline) is supported; Dock was removed.</summary>
    public string OverlaySkin { get; set; } = "Rope";

    /// <summary>
    /// Optional custom Saved folder. Empty/null uses <see cref="DefaultSavedFolder"/>.
    /// </summary>
    public string? SavedFolderPath { get; set; }

    /// <summary>
    /// Files the user took off the rope without deleting. A later Created event puts them back.
    /// </summary>
    public List<string> DismissedPaths { get; set; } = [];

    public void Normalize()
    {
        ExtraFolders ??= [];
        DismissedPaths ??= [];
        Hotkey ??= DefaultHotkey;

        // Force rope-only; migrate any saved "Dock" preference.
        OverlaySkin = "Rope";

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

        var customSaved = TryFull(SavedFolderPath);
        SavedFolderPath = customSaved is not null
            && !customSaved.Equals(DefaultSavedFolder(), StringComparison.OrdinalIgnoreCase)
            ? customSaved
            : null;

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

    /// <summary>Resolved Saved folder: custom path if set, otherwise LocalAppData\PegLine\Saved.</summary>
    public string SavedFolder()
    {
        var custom = TryFull(SavedFolderPath);
        return custom ?? DefaultSavedFolder();
    }

    public static string DefaultSavedFolder() =>
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
