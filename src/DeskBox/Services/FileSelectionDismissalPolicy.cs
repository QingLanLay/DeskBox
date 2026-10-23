namespace DeskBox.Services;

internal static class FileSelectionDismissalPolicy
{
    internal static bool IsOutsideTargetProcess(uint targetProcessId, uint applicationProcessId, bool isMenuProxy) =>
        targetProcessId != 0 && targetProcessId != applicationProcessId && !isMenuProxy;

    // MSLLHOOKSTRUCT.time and Environment.TickCount share the wrapping
    // millisecond clock. Equal ticks are ambiguous, so retain the new selection.
    internal static bool IsAfterSelection(uint eventTime, uint selectionTime) =>
        unchecked((int)(eventTime - selectionTime)) > 0;
}
