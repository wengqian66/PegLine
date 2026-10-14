using PegLine.Core;

namespace PegLine.Core.Tests;

public class PegBoardTests
{
    [Fact]
    public void Add_PutsNewestFirst_AndTrimsOldest()
    {
        var board = new PegBoard();
        var first = board.AddOrBump(PathOf("a.png"), PegOrigin.Folder, 2);
        var second = board.AddOrBump(PathOf("b.png"), PegOrigin.Folder, 2);
        var third = board.AddOrBump(PathOf("c.png"), PegOrigin.Folder, 2);

        Assert.Equal(PegAddKind.Added, third.Kind);
        Assert.Equal(["c.png", "b.png"], board.Items.Select(Name).ToArray());
        Assert.Equal(first.Peg.Id, Assert.Single(third.Trimmed).Id);
        Assert.NotEqual(second.Peg.Id, third.Trimmed[0].Id);
    }

    [Fact]
    public void DuplicatePath_BumpsWithoutASecondPeg()
    {
        var board = new PegBoard();
        var added = board.AddOrBump(PathOf("a.png"), PegOrigin.Folder, 12);
        var again = board.AddOrBump(PathOf("A.PNG"), PegOrigin.Clipboard, 12);

        Assert.Equal(PegAddKind.Updated, again.Kind);
        Assert.Equal(added.Peg.Id, again.Peg.Id);
        Assert.Equal(PegOrigin.Folder, again.Peg.Origin);
        Assert.Single(board.Items);
    }

    [Fact]
    public void TryAppend_KeepsNewestAtTheFront()
    {
        var board = new PegBoard();
        board.TryAppend(PathOf("new.png"), PegOrigin.Folder, 12);
        board.TryAppend(PathOf("old.png"), PegOrigin.Folder, 12);
        board.TryAppend(PathOf("new.png"), PegOrigin.Folder, 12);

        Assert.Equal(["new.png", "old.png"], board.Items.Select(Name).ToArray());
        Assert.Null(board.TryAppend(PathOf("extra.png"), PegOrigin.Folder, 2));
    }

    [Fact]
    public void Remove_UpdatePath_AndContains()
    {
        var board = new PegBoard();
        var added = board.AddOrBump(PathOf("a.png"), PegOrigin.Folder, 12);
        var updated = board.UpdatePath(PathOf("a.png"), PathOf("b.png"));

        Assert.NotNull(updated);
        Assert.True(board.ContainsPath(PathOf("b.png")));
        Assert.False(board.ContainsPath(PathOf("a.png")));
        Assert.Equal(added.Peg.Id, board.Remove(added.Peg.Id)?.Id);
        Assert.Empty(board.Items);
    }

    [Fact]
    public void RecentImages_SkipsOtherFiles_AndOrdersByWriteTime()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pegline-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var older = Path.Combine(folder, "older.png");
        var newer = Path.Combine(folder, "newer.jpg");
        File.WriteAllBytes(older, [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "notes.txt"), [1]);
        File.WriteAllBytes(newer, [4, 5]);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        var recent = FolderScan.RecentImages([folder, Path.Combine(folder, "missing")], 10);

        Assert.Equal([newer, older], recent);
    }

    private static string PathOf(string name) => Path.Combine(Path.GetTempPath(), "pegline-board", name);

    private static string Name(PegRecord record) => Path.GetFileName(record.FilePath);
}
