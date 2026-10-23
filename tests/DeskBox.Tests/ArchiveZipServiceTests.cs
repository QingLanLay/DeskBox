using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Real-IO tests for the managed ZIP pipeline backing the "Compress to ZIP
/// file" / "Extract all" menu entries.
/// </summary>
public sealed class ArchiveZipServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "deskbox-zip-tests-" +
            Guid.NewGuid().ToString("N"));

    public ArchiveZipServiceTests()
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
            // Temp cleanup is best effort.
        }
    }

    [Fact]
    public async Task Compress_Files_And_Extract_RoundTrips_IntoSubfolder()
    {
        string a = Path.Combine(_root, "a.txt");
        string b = Path.Combine(_root, "b.log");
        await File.WriteAllTextAsync(a, "alpha");
        await File.WriteAllTextAsync(b, "beta");

        string zipPath = await ArchiveZipService.CompressToZipAsync([a, b]);
        Assert.Equal(Path.Combine(_root, "a.zip"), zipPath);
        Assert.True(File.Exists(zipPath));

        string destination = await ArchiveZipService.ExtractAllAsync(zipPath);
        Assert.Equal(Path.Combine(_root, "a"), destination);
        Assert.True(Directory.Exists(destination));
        Assert.Equal(
            "alpha",
            await File.ReadAllTextAsync(Path.Combine(destination, "a.txt")));
        Assert.Equal(
            "beta",
            await File.ReadAllTextAsync(Path.Combine(destination, "b.log")));
    }

    [Fact]
    public async Task Compress_Folder_IncludesNestedFiles()
    {
        string folder = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(Path.Combine(folder, "inner"));
        await File.WriteAllTextAsync(
            Path.Combine(folder, "top.txt"),
            "top");
        await File.WriteAllTextAsync(
            Path.Combine(folder, "inner", "deep.txt"),
            "deep");

        string zipPath = await ArchiveZipService.CompressToZipAsync([folder]);
        string destination = await ArchiveZipService.ExtractAllAsync(zipPath);
        Assert.Equal(
            "deep",
            await File.ReadAllTextAsync(Path.Combine(
                destination,
                "bundle",
                "inner",
                "deep.txt")));
        Assert.Equal(
            "top",
            await File.ReadAllTextAsync(Path.Combine(
                destination,
                "bundle",
                "top.txt")));
    }

    [Fact]
    public async Task Compression_NameCollision_GetsSuffix()
    {
        string a = Path.Combine(_root, "dup.txt");
        await File.WriteAllTextAsync(a, "one");
        await File.WriteAllTextAsync(Path.Combine(_root, "dup.zip"), "busy");

        string zipPath = await ArchiveZipService.CompressToZipAsync([a]);
        Assert.EndsWith("dup (2).zip", zipPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_NameCollision_GetsSuffixedFolder()
    {
        string a = Path.Combine(_root, "collide.txt");
        await File.WriteAllTextAsync(a, "one");
        string zipPath = await ArchiveZipService.CompressToZipAsync([a]);
        Directory.CreateDirectory(Path.Combine(_root, "collide"));

        string destination = await ArchiveZipService.ExtractAllAsync(zipPath);
        Assert.EndsWith("collide (2)", destination, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_RejectsEntriesEscapingDestination()
    {
        string zipPath = Path.Combine(_root, "evil.zip");
        using (var stream = new FileStream(
            zipPath,
            FileMode.CreateNew,
            FileAccess.Write))
        using (var archive = new System.IO.Compression.ZipArchive(
            stream,
            System.IO.Compression.ZipArchiveMode.Create))
        {
            System.IO.Compression.ZipArchiveEntry entry =
                archive.CreateEntry("../evil.txt");
            using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("escaped");
        }

        await Assert.ThrowsAsync<System.IO.InvalidDataException>(
            () => ArchiveZipService.ExtractAllAsync(zipPath));
        Assert.False(File.Exists(Path.Combine(
            Path.GetDirectoryName(_root)!,
            "evil.txt")));
    }

    [Fact]
    public async Task Extract_GbkEncodedNames_FallBackToAnsiCodePage()
    {
        // Build an archive whose entry name is raw GBK bytes without the
        // UTF-8 flag, the way local archivers produce it.
        string zipPath = Path.Combine(_root, "gbk.zip");
        using (var stream = new FileStream(
            zipPath,
            FileMode.CreateNew,
            FileAccess.Write))
        using (var archive = new System.IO.Compression.ZipArchive(
            stream,
            System.IO.Compression.ZipArchiveMode.Create,
            leaveOpen: false,
            entryNameEncoding: System.Text.Encoding.GetEncoding(936)))
        {
            System.IO.Compression.ZipArchiveEntry entry =
                archive.CreateEntry("中文文档.txt");
            using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("content");
        }

        string destination = await ArchiveZipService.ExtractAllAsync(zipPath);
        // The fallback decodes entry names with the SYSTEM ANSI code page:
        // on a zh-CN host that round-trips to the original name, on any
        // other locale it lands as the ANSI decoding of the raw GBK bytes.
        // Compute the expected name the same way so the contract holds on
        // every CI locale.
        byte[] gbkName = System.Text.Encoding.GetEncoding(936)
            .GetBytes("中文文档.txt");
        string expectedName = System.Text.Encoding.GetEncoding(0)
            .GetString(gbkName);
        Assert.True(
            File.Exists(Path.Combine(destination, expectedName)),
            $"expected '{expectedName}' inside '{destination}'");
    }

    [Fact]
    public async Task Compress_Tar_RoundTrips_FileAndNestedFolder()
    {
        string folder = Path.Combine(_root, "pack");
        Directory.CreateDirectory(Path.Combine(folder, "inner"));
        await File.WriteAllTextAsync(Path.Combine(folder, "top.txt"), "top");
        await File.WriteAllTextAsync(
            Path.Combine(folder, "inner", "deep.txt"),
            "deep");

        string tarPath = await ArchiveZipService.CompressAsync(
            [Path.Combine(folder, "top.txt"), folder],
            ArchiveCompressionFormat.Tar);
        Assert.EndsWith(".tar", tarPath, StringComparison.Ordinal);

        string extractDir = Path.Combine(_root, "tar-out");
        using (var fs = new FileStream(tarPath, FileMode.Open, FileAccess.Read))
        using (var reader = new System.Formats.Tar.TarReader(fs))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                if (entry.EntryType != System.Formats.Tar.TarEntryType.RegularFile)
                {
                    continue;
                }

                string target = Path.Combine(
                    extractDir,
                    entry.Name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }

        // Entries are relative to the first source item's directory (the
        // pack folder itself), matching the ZIP behavior.
        Assert.Equal(
            "top",
            await File.ReadAllTextAsync(Path.Combine(extractDir, "top.txt")));
        Assert.Equal(
            "deep",
            await File.ReadAllTextAsync(Path.Combine(
                extractDir,
                "inner",
                "deep.txt")));
    }
}
