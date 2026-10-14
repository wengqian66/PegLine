namespace PegLine.Core;

public enum PegOrigin
{
    Folder = 0,
    Clipboard = 1,
}

public enum PegAddKind
{
    Added,
    Updated,
}

public sealed class PegRecord
{
    public required string Id { get; init; }

    public required string FilePath { get; set; }

    public PegOrigin Origin { get; set; }

    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.Now;
}

public sealed class PegAddResult
{
    public required PegAddKind Kind { get; init; }

    public required PegRecord Peg { get; init; }

    public IReadOnlyList<PegRecord> Trimmed { get; init; } = [];
}

/// <summary>
/// The ordered line of images. Index 0 is the newest peg. Not thread-safe; the UI thread owns it.
/// </summary>
public sealed class PegBoard
{
    private readonly List<PegRecord> _items = [];

    public IReadOnlyList<PegRecord> Items => _items;

    public PegAddResult AddOrBump(string path, PegOrigin origin, int maxPegs)
    {
        var full = Path.GetFullPath(path);
        var existing = FindByPath(full);
        if (existing is not null)
        {
            _items.Remove(existing);
            _items.Insert(0, existing);
            return new PegAddResult
            {
                Kind = PegAddKind.Updated,
                Peg = existing,
                Trimmed = Trim(maxPegs),
            };
        }

        var record = new PegRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            FilePath = full,
            Origin = origin,
            AddedAt = DateTimeOffset.Now,
        };
        _items.Insert(0, record);
        return new PegAddResult
        {
            Kind = PegAddKind.Added,
            Peg = record,
            Trimmed = Trim(maxPegs),
        };
    }

    public IReadOnlyList<PegRecord> TrimTo(int maxPegs) => Trim(maxPegs);

    /// <summary>
    /// Adds a peg at the oldest end when the path is not already on the board.
    /// Used to fill the rope from a newest-first scan without reordering pictures already hanging.
    /// </summary>
    public PegAddResult? TryAppend(string path, PegOrigin origin, int maxPegs)
    {
        var full = Path.GetFullPath(path);
        if (FindByPath(full) is not null || _items.Count >= Math.Clamp(maxPegs, AppSettings.MinPegs, AppSettings.MaxPegsLimit))
        {
            return null;
        }

        var record = new PegRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            FilePath = full,
            Origin = origin,
            AddedAt = DateTimeOffset.Now,
        };
        _items.Add(record);
        return new PegAddResult
        {
            Kind = PegAddKind.Added,
            Peg = record,
        };
    }

    public PegRecord? Remove(string id)
    {
        var index = _items.FindIndex(item => item.Id == id);
        if (index < 0)
        {
            return null;
        }

        var item = _items[index];
        _items.RemoveAt(index);
        return item;
    }

    public PegRecord? RemoveByPath(string path)
    {
        var item = FindByPath(Path.GetFullPath(path));
        if (item is null)
        {
            return null;
        }

        _items.Remove(item);
        return item;
    }

    public PegRecord? UpdatePath(string oldPath, string newPath)
    {
        var item = FindByPath(Path.GetFullPath(oldPath));
        if (item is null)
        {
            return null;
        }

        item.FilePath = Path.GetFullPath(newPath);
        return item;
    }

    public bool ContainsPath(string path)
    {
        try
        {
            return FindByPath(Path.GetFullPath(path)) is not null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private PegRecord? FindByPath(string fullPath) =>
        _items.Find(item => item.FilePath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));

    private List<PegRecord> Trim(int maxPegs)
    {
        var limit = Math.Clamp(maxPegs, AppSettings.MinPegs, AppSettings.MaxPegsLimit);
        var trimmed = new List<PegRecord>();
        while (_items.Count > limit)
        {
            var oldest = _items[^1];
            _items.RemoveAt(_items.Count - 1);
            trimmed.Add(oldest);
        }

        return trimmed;
    }
}
