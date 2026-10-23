using DeskBox.Helpers;

namespace DeskBox.Tests;

public sealed class ShellNewMenuHelperTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "deskbox-shellnew-tests-" +
            Guid.NewGuid().ToString("N"));

    public ShellNewMenuHelperTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void BuildEntries_KeepsNullFileAndTemplateRegistrations()
    {
        string template = Path.Combine(_root, "tpl.docx");
        File.WriteAllText(template, "template");

        var entries = ShellNewMenuHelper.BuildEntries(
        [
            new ShellNewMenuHelper.ShellNewKeyData(
                ".txt", "txtfile", "文本文档", null, "", null, null, null),
            new ShellNewMenuHelper.ShellNewKeyData(
                ".docx", "Word.Document", null, null, null,
                "tpl.docx", null, template),
        ]);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e =>
            e.Extension == ".txt" && e.CreatesEmptyFile);
        Assert.Contains(entries, e =>
            e.Extension == ".docx" &&
            e.TemplatePath == template &&
            !e.CreatesEmptyFile);
    }

    [Fact]
    public void BuildEntries_SkipsUnsupportedRegistrations()
    {
        var entries = ShellNewMenuHelper.BuildEntries(
        [
            // Command-only: launches an app instead of creating a file.
            new ShellNewMenuHelper.ShellNewKeyData(
                ".cmd", null, null, null, null, null, "make.cmd", null),
            // Template file no longer exists.
            new ShellNewMenuHelper.ShellNewKeyData(
                ".bmp", null, null, null, null, "gone.bmp", null,
                Path.Combine(_root, "missing.bmp")),
            // Shortcut wizard is a separate flow.
            new ShellNewMenuHelper.ShellNewKeyData(
                ".lnk", null, "Shortcut", null, "", null, null, null),
            // Not an extension-shaped key.
            new ShellNewMenuHelper.ShellNewKeyData(
                "txtfile", null, null, null, "", null, null, null),
        ]);

        Assert.Empty(entries);
    }

    [Fact]
    public void BuildEntries_DisplayNamePrecedence_ItemNameBeatsProgId()
    {
        var entries = ShellNewMenuHelper.BuildEntries(
        [
            new ShellNewMenuHelper.ShellNewKeyData(
                ".xyz", "xyzfile", "XYZ 文档", "自定义名称", "", null, null,
                null),
        ]);

        ShellNewEntry entry = Assert.Single(entries);
        Assert.Equal("自定义名称", entry.DisplayName);
    }

    [Fact]
    public void BuildEntries_DeduplicatesSameNameAndExtension()
    {
        var entries = ShellNewMenuHelper.BuildEntries(
        [
            new ShellNewMenuHelper.ShellNewKeyData(
                ".aaa", null, "Alpha", null, "", null, null, null),
            new ShellNewMenuHelper.ShellNewKeyData(
                ".aaa", null, "Alpha", null, "", null, null, null),
        ]);

        Assert.Single(entries);
    }

    [Fact]
    public void CreateFile_EmptyFileEntry_UsesFormatterAndExtension()
    {
        var entry = new ShellNewEntry(".txt", "文本文档", null);

        string? created = ShellNewMenuHelper.CreateFile(
            entry,
            _root,
            name => $"新建{name}");

        Assert.NotNull(created);
        Assert.EndsWith("新建文本文档.txt", created, StringComparison.Ordinal);
        Assert.True(File.Exists(created));
        Assert.Empty(File.ReadAllText(created!));
    }

    [Fact]
    public void CreateFile_TemplateEntry_CopiesTemplate()
    {
        string template = Path.Combine(_root, "tpl2.docx");
        File.WriteAllText(template, "payload");
        var entry = new ShellNewEntry(".docx", "Word", template);

        string? created = ShellNewMenuHelper.CreateFile(
            entry,
            _root,
            name => name);

        Assert.NotNull(created);
        Assert.Equal("payload", File.ReadAllText(created!));
    }

    [Fact]
    public void CreateFile_Collision_GetsSuffix()
    {
        var entry = new ShellNewEntry(".txt", "Text", null);
        File.WriteAllText(Path.Combine(_root, "new text.txt"), "busy");

        string? created = ShellNewMenuHelper.CreateFile(
            entry,
            _root,
            _ => "new text");

        Assert.NotNull(created);
        Assert.EndsWith("new text (2).txt", created, StringComparison.Ordinal);
    }
}
