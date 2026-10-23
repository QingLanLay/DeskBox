using DeskBox.Models;

namespace DeskBox.Services;

internal enum InitialFileWidgetSetupDecision
{
    None,
    DeferUntilInteractiveLaunch,
    ResolveExistingConfiguration,
    CreateDefaultWidget
}

internal readonly record struct InitialFileWidgetSetupSnapshot(
    bool IsInteractiveLaunch,
    SettingsLoadRecoveryState SettingsLoadState,
    bool HasResolvedSetup,
    bool HasConfiguredFileWidget,
    // True only after the user explicitly chose "start fresh" in the
    // wipe-recovery offer. DefaultsAfterFailure otherwise suppresses widget
    // creation (a recovery launch must not manufacture widgets); an explicit
    // fresh start reopens that first-run decision.
    bool AllowDefaultAfterRecoveryFailure = false);

internal static class InitialFileWidgetSetupPolicy
{
    public static InitialFileWidgetSetupDecision Evaluate(
        InitialFileWidgetSetupSnapshot snapshot)
    {
        if (snapshot.HasResolvedSetup ||
            (snapshot.SettingsLoadState == SettingsLoadRecoveryState.DefaultsAfterFailure &&
             !snapshot.AllowDefaultAfterRecoveryFailure))
        {
            return InitialFileWidgetSetupDecision.None;
        }

        if (!snapshot.IsInteractiveLaunch)
        {
            return InitialFileWidgetSetupDecision.DeferUntilInteractiveLaunch;
        }

        return snapshot.HasConfiguredFileWidget
            ? InitialFileWidgetSetupDecision.ResolveExistingConfiguration
            : InitialFileWidgetSetupDecision.CreateDefaultWidget;
    }

    public static bool HasConfiguredFileWidget(AppSettings settings)
    {
        return settings.Widgets.Any(widget =>
            widget.WidgetKind == WidgetKind.File &&
            !settings.DeletedWidgetIds.Contains(widget.Id));
    }
}
