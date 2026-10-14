namespace PegLine.Core;

public static class FolderScan
{
    public static IReadOnlyList<string> RecentImages(IEnumerable<string> folders, int take)
    {
        if (take < 1)
        {
            return [];
        }

        var files = new List<FileInfo>();
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            IEnumerable<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var path in paths)
            {
                if (!ImageFiles.IsSupported(path))
                {
                    continue;
                }

                try
                {
                    files.Add(new FileInfo(path));
                }
                catch (IOException)
                {
                    // Skip files that vanish between enumerate and stat.
                }
            }
        }

        return files
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(take)
            .Select(file => file.FullName)
            .ToList();
    }
}
