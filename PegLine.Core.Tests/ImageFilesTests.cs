using PegLine.Core;

namespace PegLine.Core.Tests;

public class ImageFilesTests
{
    [Theory]
    [InlineData("shot.png", true)]
    [InlineData("shot.JPEG", true)]
    [InlineData("shot.webp", true)]
    [InlineData("notes.txt", false)]
    [InlineData(".hidden.png", false)]
    [InlineData("~temp.png", false)]
    [InlineData("download.png.crdownload", false)]
    [InlineData("", false)]
    public void IsSupported_FiltersNames(string name, bool supported)
    {
        var path = string.IsNullOrEmpty(name) ? name : Path.Combine(Path.GetTempPath(), name);
        Assert.Equal(supported, ImageFiles.IsSupported(path));
    }
}
