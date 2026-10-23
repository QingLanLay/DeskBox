using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private uint _lastItemSelectionChangedTime;
    private SelectorItem? _dismissedItemFocusContainer;
    private readonly HashSet<SelectorItem> _dismissedItemFocusContainers = [];

    private bool IsFileSelectionInteractionActive() =>
        IsStackPopoverInteractionActive ||
        _stackPopoverDragActive ||
        _stackPopoverTitleEditing ||
        IsStackPopoverItemRenameEditing ||
        _itemRenameTarget is not null ||
        _pendingPointerDragItems.Length > 0 ||
        _selectionPointerPressed ||
        _isBoxSelecting;

    internal void DismissSelectionOnOutsideClick(uint eventTime)
    {
        // Native input is validated off-thread. A delayed old click must not
        // clear a selection the user made after that click was sampled.
        if (FileSelectionDismissalPolicy.IsAfterSelection(eventTime, _lastItemSelectionChangedTime))
        {
            ClearItemSelectionIfInteractionIdle();
        }
    }

    private void DismissItemFocusVisual()
    {
        // A nonactivating island may retain local XAML focus after an outside
        // click. End its focus cue without focusing another control or HWND.
        foreach (ListViewBase view in new ListViewBase[] { ItemsGrid, ItemsList })
        {
            foreach (WidgetItem item in view.Items.OfType<WidgetItem>())
            {
                if (view.ContainerFromItem(item) is SelectorItem container)
                {
                    FrameworkElement? template = VisualTreeHelper.GetChildrenCount(container) > 0
                        ? VisualTreeHelper.GetChild(container, 0) as FrameworkElement : null;
                    var focusBorder = template?.FindName("FocusVisualBorder") as Border;
                    if (container.FocusState != FocusState.Unfocused || view.SelectedItems.Contains(item) ||
                        focusBorder?.Opacity > 0)
                    {
                        _dismissedItemFocusContainer = container;
                        _dismissedItemFocusContainers.Add(container);
                        SetItemFocusVisualVisibility(container, Visibility.Collapsed);
                    }

                    // Native island focus can already report Unfocused while
                    // its template still displays PointerFocused. Reset the
                    // realized template regardless of that stale focus flag.
                    _ = VisualStateManager.GoToState(container, "Unfocused", false);
                }
            }
        }
    }

    private void RestoreDismissedItemFocusVisual(SelectorItem? target, string state)
    {
        foreach (SelectorItem dismissed in _dismissedItemFocusContainers)
        {
            // Visibility is independent of WinUI's automatic FocusStates,
            // which can otherwise redraw a retained island focus after reset.
            _ = VisualStateManager.GoToState(dismissed,
                ReferenceEquals(dismissed, target) ? state : "Unfocused", false);
            SetItemFocusVisualVisibility(dismissed, Visibility.Visible);
        }

        _dismissedItemFocusContainers.Clear();
        _dismissedItemFocusContainer = null;
    }

    private static void SetItemFocusVisualVisibility(SelectorItem container, Visibility visibility)
    {
        if (VisualTreeHelper.GetChildrenCount(container) == 0 ||
            VisualTreeHelper.GetChild(container, 0) is not FrameworkElement template)
        {
            return;
        }

        foreach (string name in new[] { "FocusVisualBorder", "FocusVisualInnerBorder" })
        {
            if (template.FindName(name) is Border border)
            {
                border.Visibility = visibility;
            }
        }
    }
}
