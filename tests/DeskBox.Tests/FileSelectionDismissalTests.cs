using DeskBox.Services;
using System.Xml.Linq;

namespace DeskBox.Tests;

public sealed class FileSelectionDismissalTests
{
    [Fact]
    public void FileItemFocusCues_HaveOneOwnerAndKeepKeyboardFocusVisible()
    {
        XDocument document = XDocument.Load(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (string key in new[] { "SurfaceGridViewItemStyle", "SurfaceListViewItemStyle" })
        {
            XElement style = document.Descendants(ui + "Style")
                .Single(element => (string?)element.Attribute(x + "Key") == key);
            Assert.Equal("False", (string?)style.Elements(ui + "Setter")
                .Single(setter => (string?)setter.Attribute("Property") == "UseSystemFocusVisuals")
                .Attribute("Value"));
            XElement focused = style.Descendants(ui + "VisualState")
                .Single(state => (string?)state.Attribute(x + "Name") == "Focused");
            Assert.Contains(focused.Descendants(ui + "Setter"), setter =>
                (string?)setter.Attribute("Target") == "FocusVisualBorder.Opacity" &&
                (string?)setter.Attribute("Value") == "1");
        }
    }

    [Theory]
    [InlineData(150u, 100u, true)]
    [InlineData(100u, 150u, false)]
    [InlineData(100u, 100u, false)]
    [InlineData(10u, uint.MaxValue - 10, true)]
    [InlineData(uint.MaxValue - 10, 10u, false)]
    public void OutsideClick_OnlyDismissesAnEarlierSelectionAcrossClockWrap(
        uint eventTime, uint selectionTime, bool expected)
    {
        Assert.Equal(expected, FileSelectionDismissalPolicy.IsAfterSelection(eventTime, selectionTime));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void HookHealth_TracksBothInputConsumersWithoutEnablingTheGesture(
        bool doubleClickEnabled, bool hasObserver, bool expected)
    {
        var settings = new SettingsService(TestPaths.FromRepository(
            ".codex-temp/selection-dismissal-tests/" + Guid.NewGuid().ToString("N")));
        settings.Settings.DesktopDoubleClickEnabled = doubleClickEnabled;
        Action<uint>? observer = hasObserver ? _ => { } : null;
        using var service = new DesktopDoubleClickActivationService(
            settings, _ => Task.CompletedTask, observer);

        Assert.Equal(expected, ((IHookHealthProbeTarget)service).HookProbeWanted);
        Assert.Equal(doubleClickEnabled, settings.Settings.DesktopDoubleClickEnabled);
        Assert.False(service.IsActive);
        service.Dispose();
        Assert.False(((IHookHealthProbeTarget)service).HookProbeWanted);
    }

    [Fact]
    public void SharedInputObserver_RemainsAvailableWithDoubleClickDisabled()
    {
        string service = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/DesktopDoubleClickActivationService.cs"));
        int start = service.IndexOf("private void ProcessQueuedMouseDown", StringComparison.Ordinal);
        int end = service.IndexOf("private void NotifyOutsidePointerPressed", start, StringComparison.Ordinal);
        string process = service[start..end];

        Assert.Contains("_outsidePointerPressed is not null ||", service, StringComparison.Ordinal);
        Assert.True(process.IndexOf("NotifyOutsidePointerPressed(sample);", StringComparison.Ordinal) <
            process.IndexOf("if (!_settingsService.Settings.DesktopDoubleClickEnabled)", StringComparison.Ordinal));
        Assert.Contains("WidgetManager?.DismissFileSelectionsOnOutsideClick(eventTime)",
            File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.xaml.cs")), StringComparison.Ordinal);
    }
}
