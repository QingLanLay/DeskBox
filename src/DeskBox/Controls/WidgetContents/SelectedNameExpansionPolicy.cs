namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Pure gating rules for the Windows-desktop-style selected-name expansion
/// (a trimmed icon label grows downward on sole selection and collapses when
/// the selection changes) and for the slow-click rename entry (clicking the
/// label of the item that was already the sole selection). Hosts keep the
/// state; these functions keep the policy pinned by contract tests.
/// </summary>
public static class SelectedNameExpansionPolicy
{
    /// <summary>
    /// Sole selection may expand its trimmed label only in double-click-to-
    /// open mode. In single-click mode a click opens the item immediately, so
    /// there is no "selected, then pause" state for an expanded label or a
    /// slow second click to act on. Marquee box selection and active drags
    /// pass through momentary single-selection states and must not flash an
    /// expansion while they do.
    /// </summary>
    public static bool ShouldExpandSelectedItemName(
        bool isDoubleClickToOpen,
        int selectedItemCount,
        bool isBoxSelecting,
        bool isDragActive) =>
        isDoubleClickToOpen &&
        selectedItemCount == 1 &&
        !isBoxSelecting &&
        !isDragActive;

    /// <summary>
    /// A click may start inline rename only when it lands on the name label
    /// of the item that was already the sole selection before this click,
    /// with no modifier keys, in double-click-to-open mode. The caller reads
    /// the selection snapshot before the native selector commits this click
    /// (see ItemSurface_PointerPressed for that ordering).
    /// </summary>
    public static bool ShouldStartRenameFromItemClick(
        bool isDoubleClickToOpen,
        bool hasModifierKey,
        bool wasSoleSelectedItemBeforeClick,
        bool pointerPressedOnLabel) =>
        isDoubleClickToOpen &&
        !hasModifierKey &&
        wasSoleSelectedItemBeforeClick &&
        pointerPressedOnLabel;

    /// <summary>
    /// Height for the inline rename editor in icon mode: at least the tile's
    /// own label height, grown to the measured wrapped full-name height,
    /// clamped to the distance from the label to the host surface bottom so
    /// the desktop-style vertical editor never detaches from its item.
    /// </summary>
    public static double ClampRenameEditorHeight(
        double measuredNameHeight,
        double tileHeight,
        double availableHeight,
        double minimumHeight)
    {
        double ceiling = Math.Max(minimumHeight, availableHeight);
        return Math.Clamp(
            Math.Max(measuredNameHeight, tileHeight),
            minimumHeight,
            ceiling);
    }
}
