namespace PegLine.Core;

/// <summary>
/// Image types the rope accepts. Matching is by extension only; decoding happens in the WinUI app.
/// </summary>
public static class ImageFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp",
    };

    public static bool IsSupported(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        if (name.Length == 0 || name[0] is '.' or '~')
        {
            return false;
        }

        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Extensions.Contains(Path.GetExtension(name));
    }

    public static async Task<bool> WaitUntilReadable(string path, CancellationToken cancellationToken, int attempts = 8)
    {
        long previous = -1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length > 0 && stream.Length == previous)
                {
                    return true;
                }

                previous = stream.Length;
            }
            catch (IOException)
            {
                // The screenshot tool is still writing the file.
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            if (attempt == attempts - 1)
            {
                return previous > 0;
            }

            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
