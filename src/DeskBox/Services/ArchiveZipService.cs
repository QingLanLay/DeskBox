using System.IO.Compression;
using System.Text;
using DeskBox.Services;

namespace DeskBox.Services;

/// <summary>
/// Archive formats offered by the grid "Compress to" submenu. 7z has no
/// BCL writer and would need the LZMA SDK (batch debt pending a go).
/// </summary>
public enum ArchiveCompressionFormat
{
    Zip,
    Tar
}

/// <summary>
/// Managed archive support for the grid context menu ("Compress to" /
/// "Extract all"). Runs entirely inside the DeskBox file pipeline, so it
/// works without any third-party archiver installed. Only .zip is
/// extracted; other archive formats belong to the full system menu.
/// </summary>
public static class ArchiveZipService
{
    static ArchiveZipService()
    {
        // Zips produced by common Chinese archivers store GBK entry names
        // without the UTF-8 flag; the code pages provider is required to
        // decode them with the system ANSI code page.
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // Fall back to default UTF-8 decoding (names may garble).
        }
    }

    /// <summary>
    /// Creates an archive in the requested format next to the first source
    /// item; entries keep their relative paths from the common container.
    /// Multi-item selections are named after the first item, matching
    /// Explorer's compress behavior.
    /// </summary>
    public static Task<string> CompressAsync(
        IReadOnlyList<string> sourcePaths,
        ArchiveCompressionFormat format)
    {
        if (sourcePaths.Count == 0)
        {
            throw new ArgumentException(
                "At least one source path is required.",
                nameof(sourcePaths));
        }

        string container =
            Path.GetDirectoryName(Path.GetFullPath(sourcePaths[0])) ??
            Environment.CurrentDirectory;

        string baseName = GetCompressionBaseName(sourcePaths);
        string archivePath = FileService.GetAvailablePath(Path.Combine(
            container,
            baseName + (format == ArchiveCompressionFormat.Tar
                ? ".tar"
                : ".zip")));

        return Task.Run(() =>
        {
            if (format == ArchiveCompressionFormat.Tar)
            {
                WriteTar(sourcePaths, container, archivePath);
                return archivePath;
            }

            WriteZip(sourcePaths, container, archivePath);
            return archivePath;
        });
    }

    private static void WriteZip(
        IReadOnlyList<string> sourcePaths,
        string container,
        string zipPath)
    {
        using var stream = new FileStream(
                zipPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            using var archive = new ZipArchive(
                stream,
                ZipArchiveMode.Create,
                leaveOpen: false);
            foreach (string sourcePath in sourcePaths)
            {
                string fullPath = Path.GetFullPath(sourcePath);
                string relativePath = Path.GetRelativePath(container, fullPath)
                    .Replace('\\', '/');
                if (File.Exists(fullPath))
                {
                    archive.CreateEntryFromFile(
                        fullPath,
                        relativePath,
                        CompressionLevel.Optimal);
                }
                else if (Directory.Exists(fullPath))
                {
                    archive.CreateEntry(
                        relativePath + "/",
                        CompressionLevel.Optimal);
                    foreach (string file in Directory
                                 .EnumerateFiles(
                                     fullPath,
                                     "*",
                                     SearchOption.AllDirectories))
                    {
                        string fileRelative =
                            Path.GetRelativePath(container, file)
                                .Replace('\\', '/');
                        archive.CreateEntryFromFile(
                            file,
                            fileRelative,
                            CompressionLevel.Optimal);
                    }
                }
            }
    }

    private static void WriteTar(
        IReadOnlyList<string> sourcePaths,
        string container,
        string tarPath)
    {
        using var stream = new FileStream(
            tarPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var writer = new System.Formats.Tar.TarWriter(stream);
        foreach (string file in EnumerateFiles(sourcePaths, tarPath))
        {
            string relativePath = Path.GetRelativePath(container, file)
                .Replace('\\', '/');
            writer.WriteEntry(file, relativePath);
        }
    }

    private static IEnumerable<string> EnumerateFiles(
        IReadOnlyList<string> sourcePaths,
        string excludeArchivePath)
    {
        foreach (string sourcePath in sourcePaths)
        {
            string fullPath = Path.GetFullPath(sourcePath);
            if (File.Exists(fullPath))
            {
                if (!string.Equals(
                        fullPath,
                        excludeArchivePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    yield return fullPath;
                }
            }
            else if (Directory.Exists(fullPath))
            {
                foreach (string file in Directory.EnumerateFiles(
                             fullPath,
                             "*",
                             SearchOption.AllDirectories))
                {
                    // The archive lands next to the selection, so it would
                    // otherwise swallow itself when a folder is selected.
                    if (!string.Equals(
                            file,
                            excludeArchivePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        yield return file;
                    }
                }
            }
        }
    }

    /// <summary>
    /// ZIP shorthand for <see cref="CompressAsync"/>.
    /// </summary>
    public static Task<string> CompressToZipAsync(
        IReadOnlyList<string> sourcePaths) =>
        CompressAsync(sourcePaths, ArchiveCompressionFormat.Zip);

    /// <summary>
    /// Extracts every entry into a fresh folder named after the archive
    /// (name collisions get the standard " (2)" suffix), like the system
    /// Extract All default. Rejects archive entries that escape the
    /// destination folder.
    /// </summary>
    public static Task<string> ExtractAllAsync(string archivePath)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException(
                "The archive does not exist.",
                archivePath);
        }

        string container =
            Path.GetDirectoryName(Path.GetFullPath(archivePath)) ??
            Environment.CurrentDirectory;
        string baseName = Path.GetFileNameWithoutExtension(archivePath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "archive";
        }

        string destinationPath = FileService.GetAvailablePath(
            Path.Combine(container, baseName));

        return Task.Run(() =>
        {
            Directory.CreateDirectory(destinationPath);
            Encoding? entryEncoding = ResolveEntryEncoding(archivePath);
            using var stream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var archive = new ZipArchive(
                stream,
                ZipArchiveMode.Read,
                leaveOpen: false,
                entryNameEncoding: entryEncoding);
            string destinationRoot = Path.GetFullPath(destinationPath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                ExtractEntry(entry, destinationRoot);
            }

            return destinationPath;
        });
    }

    private static string GetCompressionBaseName(
        IReadOnlyList<string> sourcePaths)
    {
        string firstPath = Path.GetFileName(sourcePaths[0].TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        string baseName = Path.GetFileNameWithoutExtension(firstPath);
        return string.IsNullOrWhiteSpace(baseName) ? "archive" : baseName;
    }

    /// <summary>
    /// Picks the entry name decoding: entries that come back with UTF-8
    /// replacement characters were encoded in the system ANSI code page
    /// (typical for locally produced archives), so the archive is reopened
    /// with that code page.
    /// </summary>
    private static Encoding? ResolveEntryEncoding(string archivePath)
    {
        try
        {
            using var probeStream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var probe = new ZipArchive(
                probeStream,
                ZipArchiveMode.Read,
                leaveOpen: false);
            foreach (ZipArchiveEntry entry in probe.Entries)
            {
                if (entry.FullName.Contains('\uFFFD'))
                {
                    try
                    {
                        return Encoding.GetEncoding(0);
                    }
                    catch (ArgumentException)
                    {
                        return null;
                    }
                }
            }
        }
        catch (InvalidDataException)
        {
            // Not a readable zip; let the extraction pass produce the
            // caller-facing failure.
        }

        return null;
    }

    private static void ExtractEntry(
        ZipArchiveEntry entry,
        string destinationRoot)
    {
        string entryPath = entry.FullName.Replace(
            '/',
            Path.DirectorySeparatorChar);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(
                Path.Combine(destinationRoot, entryPath));
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or
            PathTooLongException)
        {
            throw new InvalidDataException(
                $"The archive contains an invalid entry name: {entry.FullName}");
        }

        bool isInsideRoot = fullPath.StartsWith(
            destinationRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
        if (!isInsideRoot)
        {
            throw new InvalidDataException(
                "The archive contains an entry that would extract " +
                "outside the destination folder.");
        }

        if (entry.FullName.EndsWith('/'))
        {
            Directory.CreateDirectory(fullPath);
            return;
        }

        string? parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        entry.ExtractToFile(fullPath, overwrite: false);
    }
}
