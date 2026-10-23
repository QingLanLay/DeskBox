namespace DeskBox.Services;

internal static class FileSelectionDismissalPolicy
{
    // MSLLHOOKSTRUCT.time and Environment.TickCount share the wrapping
    // millisecond clock. Equal ticks are ambiguous, so retain the new selection.
    internal static bool IsAfterSelection(uint eventTime, uint selectionTime) =>
        unchecked((int)(eventTime - selectionTime)) > 0;
}
