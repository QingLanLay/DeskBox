using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Helpers;

/// <summary>
/// Classifies .lnk items whose target is a Shell namespace object. Windows
/// gives such links a plain file menu (the namespace target's verbs do not
/// transfer), but DeskBox's grid menu follows the desktop-icon menu of the
/// target instead — so the kind must be known before the menu is built.
/// The result is cached on the item; resolution runs on the UI thread's STA
/// apartment (Shell COM apartment rules) and only parses stored link bytes,
/// never resolving or searching a target.
/// </summary>
internal static class NamespaceShortcutHelper
{
    // Parsing-name prefixes of the curated namespace targets. Matched as
    // prefixes (OrdinalIgnoreCase) because a desktop-absolute parsing name
    // can extend past the root CLSID (control-panel category pages are
    // "::{26EE0668-…}\::{…}") and GUID casing is not stable across writers.
    private const string ThisPcPrefix = "::{20D04FE0-";
    private const string RecycleBinPrefix = "::{645FF040-";
    private const string ControlPanelPrefix = "::{26EE0668-";
    private const string ControlPanelLegacyPrefix = "::{21EC2020-";

    /// <summary>
    /// The item's namespace classification, resolving (and caching) it on
    /// first use. Safe to call on every menu build.
    /// </summary>
    internal static NamespaceShortcutKind ResolveKind(WidgetItem? item)
    {
        if (item is null)
        {
            return NamespaceShortcutKind.None;
        }

        if (item.IsNamespaceShortcutKindResolved)
        {
            return item.NamespaceShortcutKind;
        }

        (NamespaceShortcutKind kind, string parsingName) =
            Resolve(item.Path);
        item.NamespaceShortcutKind = kind;
        item.NamespaceShortcutParsingName = parsingName;
        item.IsNamespaceShortcutKindResolved = true;
        return kind;
    }

    /// <summary>
    /// Resolves the namespace classification of one .lnk path. Returns the
    /// kind plus the target parsing name (empty for non-namespace links) so
    /// verbs like Create Shortcut can re-materialize the same target.
    /// </summary>
    internal static (NamespaceShortcutKind Kind, string ParsingName) Resolve(
        string? shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) ||
            !ShortcutHelper.IsShellLinkPath(shortcutPath))
        {
            return (NamespaceShortcutKind.None, string.Empty);
        }

        try
        {
            // Fast path: a link written by path form already stores the
            // "::{…}" parsing name as its target string. The metadata reader
            // is cached and never resolves the target.
            ShortcutInfo? stored = ShortcutHelper.ReadStoredMetadata(shortcutPath);
            if (stored is not null &&
                !string.IsNullOrWhiteSpace(stored.TargetPath))
            {
                string trimmedTarget = stored.TargetPath.Trim();
                NamespaceShortcutKind fromTarget = ClassifyParsingName(trimmedTarget);
                return fromTarget != NamespaceShortcutKind.None
                    ? (fromTarget, trimmedTarget)
                    : (NamespaceShortcutKind.None, string.Empty);
            }

            // PIDL-only link: read the stored IDList's parsing name.
            string? parsingName = ShellLinkVtableInterop.TryReadTargetParsingName(
                shortcutPath);
            return (ClassifyParsingName(parsingName), parsingName ?? string.Empty);
        }
        catch
        {
            return (NamespaceShortcutKind.None, string.Empty);
        }
    }

    /// <summary>
    /// Maps a desktop-absolute parsing name to its namespace kind. Pure
    /// string mapping, safe on any thread and directly unit-testable.
    /// </summary>
    internal static NamespaceShortcutKind ClassifyParsingName(
        string? parsingName)
    {
        if (string.IsNullOrWhiteSpace(parsingName))
        {
            return NamespaceShortcutKind.None;
        }

        string trimmed = parsingName.Trim();
        if (!trimmed.StartsWith("::", StringComparison.Ordinal))
        {
            return NamespaceShortcutKind.None;
        }

        if (trimmed.StartsWith(ThisPcPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return NamespaceShortcutKind.ThisPC;
        }

        if (trimmed.StartsWith(RecycleBinPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return NamespaceShortcutKind.RecycleBin;
        }

        if (trimmed.StartsWith(ControlPanelPrefix, StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith(ControlPanelLegacyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return NamespaceShortcutKind.ControlPanel;
        }

        return NamespaceShortcutKind.OtherNamespace;
    }
}
