namespace DeskBox.Models;

/// <summary>
/// Classification of a shortcut whose target is a Shell namespace object
/// (This PC, Recycle Bin, Control Panel, …) instead of a filesystem path.
/// DeskBox materializes dropped namespace items as PIDL-only .lnk files;
/// the kind decides which native desktop-icon verbs the item menu offers.
/// </summary>
public enum NamespaceShortcutKind
{
    /// <summary>Not a namespace shortcut: a filesystem target or no target.</summary>
    None = 0,

    ThisPC,

    RecycleBin,

    ControlPanel,

    /// <summary>
    /// A "::{…}" namespace target that is none of the curated ones (Network,
    /// Libraries, third-party namespaces). It gets the namespace hide rules
    /// but no extra verbs.
    /// </summary>
    OtherNamespace
}
