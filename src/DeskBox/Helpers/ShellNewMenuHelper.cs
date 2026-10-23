using DeskBox.Services;

namespace DeskBox.Helpers;

public sealed record ShellNewEntry(
    string Extension,
    string DisplayName,
    string? TemplatePath)
{
    /// <summary>Null template means "create an empty file".</summary>
    public bool CreatesEmptyFile => TemplatePath is null;
}

/// <summary>
/// Enumerates the registered "New" file types (the same source the desktop
/// New submenu reads: HKCR\.ext\ShellNew) and materializes new files inside
/// the grid's own creation pipeline. Registry-backed association data only;
/// no shell extension is loaded.
/// </summary>
public static class ShellNewMenuHelper
{
    /// <summary>
    /// Raw registry shape of one ShellNew registration, fed to
    /// <see cref="BuildEntries"/> so the filtering rules stay testable.
    /// </summary>
    public readonly record struct ShellNewKeyData(
        string Extension,
        string? ExtensionDescription,
        string? ProgIdDescription,
        string? ItemName,
        string? NullFile,
        string? FileName,
        string? Command,
        string? TemplateFullPath);

    private const int MaxEntries = 16;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private static IReadOnlyList<ShellNewEntry>? _cachedEntries;
    private static long _cacheReadAt;

    private static readonly HashSet<string> ExcludedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // The shortcut wizard needs its own multi-step flow.
            ".lnk",
            // Folder-like or non-file registrations.
            ".library-ms",
            ".searchConnector-ms"
        };

    public static IReadOnlyList<ShellNewEntry> GetEntries()
    {
        long now = Environment.TickCount64;
        IReadOnlyList<ShellNewEntry>? cached = _cachedEntries;
        if (cached is not null &&
            now - _cacheReadAt < CacheLifetime.TotalMilliseconds)
        {
            return cached;
        }

        List<ShellNewEntry> entries = EnumerateRegistryEntries();
        _cachedEntries = entries;
        _cacheReadAt = now;
        return entries;
    }

    internal static void InvalidateCacheForTests()
    {
        _cachedEntries = null;
        _cacheReadAt = 0;
    }

    /// <summary>
    /// Applies the display-name precedence and filtering rules to raw
    /// registrations. Pure so tests can drive it without touching HKCR.
    /// </summary>
    internal static IReadOnlyList<ShellNewEntry> BuildEntries(
        IEnumerable<ShellNewKeyData> registrations)
    {
        var entries = new List<ShellNewEntry>();
        var seenNames = new HashSet<string>(StringComparer.CurrentCulture);
        foreach (ShellNewKeyData data in registrations)
        {
            if (!data.Extension.StartsWith('.'))
            {
                continue;
            }

            if (ExcludedExtensions.Contains(data.Extension))
            {
                continue;
            }

            // Command/Data-only registrations run an application instead of
            // creating a file; not supported in the grid pipeline.
            bool createsEmptyFile = data.NullFile is not null;
            bool copiesTemplate =
                data.FileName is not null &&
                data.TemplateFullPath is not null &&
                File.Exists(data.TemplateFullPath);
            if (!createsEmptyFile && !copiesTemplate)
            {
                continue;
            }

            string displayName =
                data.ItemName ??
                data.ProgIdDescription ??
                data.ExtensionDescription ??
                data.Extension.TrimStart('.');
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            if (!seenNames.Add($"{displayName}|{data.Extension}"))
            {
                continue;
            }

            entries.Add(new ShellNewEntry(
                data.Extension,
                displayName,
                createsEmptyFile ? null : data.TemplateFullPath));
        }

        return entries
            .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCulture)
            .Take(MaxEntries)
            .ToList();
    }

    /// <summary>
    /// Creates the new file inside <paramref name="destinationFolderPath"/>
    /// using the same collision naming as folder creation, and returns its
    /// full path (null when the template vanished or the write failed).
    /// </summary>
    public static string? CreateFile(
        ShellNewEntry entry,
        string destinationFolderPath,
        Func<string, string> baseNameFormatter)
    {
        if (string.IsNullOrWhiteSpace(destinationFolderPath) ||
            !Directory.Exists(destinationFolderPath))
        {
            return null;
        }

        try
        {
            string baseName = baseNameFormatter(entry.DisplayName);
            string targetPath = FileService.GetAvailablePath(Path.Combine(
                destinationFolderPath,
                baseName + entry.Extension));
            if (entry.CreatesEmptyFile)
            {
                using FileStream stream = new(
                    targetPath,
                    FileMode.CreateNew,
                    FileAccess.Write);
                return targetPath;
            }

            if (entry.TemplatePath is { } template &&
                File.Exists(template))
            {
                // Stream copy keeps this helper clear of the destructive
                // file-operation boundary (routed through FileService).
                using FileStream source = new(
                    template,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                using FileStream target = new(
                    targetPath,
                    FileMode.CreateNew,
                    FileAccess.Write);
                source.CopyTo(target);
                return targetPath;
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[ShellNew] create failed ext='{entry.Extension}': " +
                ex.Message);
        }

        return null;
    }

    private static List<ShellNewEntry> EnumerateRegistryEntries()
    {
        var registrations = new List<ShellNewKeyData>();
        try
        {
            using Microsoft.Win32.RegistryKey classesRoot =
                Microsoft.Win32.Registry.ClassesRoot;
            foreach (string keyName in classesRoot.GetSubKeyNames())
            {
                if (!keyName.StartsWith('.') ||
                    keyName.Length <= 1)
                {
                    continue;
                }

                try
                {
                    using Microsoft.Win32.RegistryKey? shellNew =
                        classesRoot.OpenSubKey(keyName + "\\ShellNew");
                    if (shellNew is null)
                    {
                        continue;
                    }

                    string? extensionDescription =
                        ReadDefaultValue(classesRoot, keyName);
                    string? progIdDescription =
                        extensionDescription is null
                            ? null
                            : ReadDefaultValue(classesRoot, extensionDescription);
                    string? fileName = shellNew.GetValue("FileName") as string;

                    registrations.Add(new ShellNewKeyData(
                        keyName,
                        extensionDescription,
                        progIdDescription,
                        shellNew.GetValue("ItemName") as string,
                        shellNew.GetValue("NullFile") as string,
                        fileName,
                        shellNew.GetValue("Command") as string,
                        ResolveTemplatePath(fileName)));
                }
                catch
                {
                    // One unreadable association key must not break the
                    // whole New submenu.
                }
            }
        }
        catch
        {
            // Registry unavailable (for example a locked-down session):
            // fall through with whatever was collected.
        }

        return BuildEntries(registrations).ToList();
    }

    private static string? ReadDefaultValue(
        Microsoft.Win32.RegistryKey root,
        string subKeyName)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key =
                root.OpenSubKey(subKeyName);
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveTemplatePath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        if (fileName.Contains('\\') || fileName.Contains('/'))
        {
            return fileName;
        }

        string? windir = Environment.GetEnvironmentVariable("windir");
        return string.IsNullOrEmpty(windir)
            ? null
            : Path.Combine(windir, "ShellNew", fileName);
    }
}
