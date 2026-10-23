using DeskBox.Helpers;

namespace DeskBox.Tests;

/// <summary>
/// Executable file types (.exe/.cmd/...) cannot be opened with another app:
/// the Windows Open With dialog refuses them, so the grid menu hides the
/// whole Open With entry for them (real-world report: FFWallpapersetup.exe
/// and 实时翻译.cmd showed a dead "choose another app" entry).
/// </summary>
public sealed class ShellOpenWithSupportTests
{
    [Theory]
    [InlineData("F:\\工作文件\\FFWallpapersetup_2.5.5.1.exe", false)]
    [InlineData("E:\\box\\综合\\实时翻译.cmd", false)]
    [InlineData("C:\\tools\\setup.msi", false)]
    [InlineData("C:\\tools\\run.bat", false)]
    [InlineData("C:\\tools\\legacy.scr", false)]
    [InlineData("C:\\tools\\old.com", false)]
    [InlineData("C:\\Users\\me\\Desktop\\应用.lnk", false)]
    [InlineData("C:\\Favorites\\站点.url", false)]
    [InlineData("C:\\docs\\notes.txt", true)]
    [InlineData("C:\\docs\\photo.JPG", true)]
    [InlineData("C:\\docs\\archive.zip", true)]
    [InlineData("C:\\docs\\LICENSE", true)]
    [InlineData("", false)]
    public void SupportsOpenWith_HidesExecutablesAndShortcuts(string path, bool expected)
    {
        Assert.Equal(expected, ShellOpenWithHelper.SupportsOpenWith(path));
    }
}
