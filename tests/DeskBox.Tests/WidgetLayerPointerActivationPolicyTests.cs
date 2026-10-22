using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetLayerPointerActivationPolicyTests
{
    [Theory]
    [InlineData(true, false, true, false, true)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, false, true, false, false)]
    public void FocusRequest_PreventsPointerActivationButAllowsTextEntryAndKeyboardNavigation(
        bool pinned, bool textInput, bool pointerDriven, bool foreground, bool expected)
    {
        Assert.Equal(expected, WidgetLayerPointerActivationPolicy.ShouldSuppressFocus(
            pinned, textInput, pointerDriven, foreground));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void OrdinaryPointerActivation_IsSuppressedForEveryDesktopPinnedWidget(
        bool usesDesktopPinnedMode,
        bool expected)
    {
        Assert.Equal(
            expected,
            WidgetLayerPointerActivationPolicy.ShouldSuppress(usesDesktopPinnedMode));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void ActivatingClick_IsPreservedOnlyDuringAnActiveQuickReveal(
        bool usesQuickRevealMode,
        bool widgetsRaisedFromTray,
        bool expected)
    {
        Assert.Equal(
            expected,
            WidgetLayerPointerActivationPolicy.ShouldPreserveActivatingClick(
                usesQuickRevealMode,
                widgetsRaisedFromTray));
    }
}
