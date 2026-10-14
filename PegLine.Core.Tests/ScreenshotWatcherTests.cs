using PegLine.Core;

namespace PegLine.Core.Tests;

public class ScreenshotWatcherTests
{
    [Fact]
    public async Task NewPng_RaisesCreatedOnce_AndIgnoresText()
    {
        var folder = CreateFolder();
        var created = new List<string>();
        using var watcher = new ScreenshotWatcher(TimeSpan.FromMilliseconds(80));
        watcher.ImageCreated += (_, path) => created.Add(path);
        watcher.SetFolders([folder]);

        var image = Path.Combine(folder, "shot.png");
        await File.WriteAllBytesAsync(image, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(image, [1, 2, 3, 4, 5]);
        await File.WriteAllTextAsync(Path.Combine(folder, "readme.txt"), "hello");

        await WaitUntil(() => created.Count >= 1);
        await Task.Delay(400);

        Assert.Equal([image], created.Select(Path.GetFullPath));
    }

    [Fact]
    public async Task RenameAndDelete_AreReported()
    {
        var folder = CreateFolder();
        var renamed = new List<(string Old, string New)>();
        var deleted = new List<string>();
        using var watcher = new ScreenshotWatcher(TimeSpan.FromMilliseconds(40));
        watcher.ImageRenamed += (_, e) => renamed.Add((e.OldPath, e.NewPath));
        watcher.ImageDeleted += (_, path) => deleted.Add(path);
        watcher.SetFolders([folder]);

        var created = new List<string>();
        watcher.ImageCreated += (_, path) => created.Add(path);
        var source = Path.Combine(folder, "before.png");
        var destination = Path.Combine(folder, "after.png");
        await File.WriteAllBytesAsync(source, [9, 9, 9]);
        await Task.Delay(400);
        File.Move(source, destination);
        await WaitUntil(() => renamed.Count >= 1 || (deleted.Contains(source) && created.Contains(destination)));

        File.Delete(destination);
        await WaitUntil(() => deleted.Contains(destination));

        if (renamed.Count > 0)
        {
            Assert.Equal(source, renamed[0].Old);
            Assert.Equal(destination, renamed[0].New);
        }
        else
        {
            Assert.Contains(source, deleted);
            Assert.Contains(destination, created);
        }
    }

    private static string CreateFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pegline-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The folder watcher did not report in time.");
    }
}
