using DeskBox.Contracts;
using DeskBox.Features.Appearance;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class GlassMaterialTests
{
    [Theory]
    [InlineData(19044)]
    [InlineData(19045)]
    [InlineData(22000)]
    [InlineData(26100)]
    public void Glass_IsAvailableOnBothWindowsVersions(int build)
    {
        Assert.Equal(WidgetMaterialKinds.Glass,
            WindowsCompatibilityService.ResolveWidgetMaterialTypeForBuild(WidgetMaterialKinds.Glass, build));
        Assert.True(SettingsService.IsAcrylicMaterial(WidgetMaterialKinds.Glass));
        Assert.True(WidgetMaterialKinds.SupportsOpacity(WidgetMaterialKinds.Glass));
        Assert.True(WidgetMaterialKinds.SupportsMaterialIntensity(WidgetMaterialKinds.Glass));
    }

    [Fact]
    public async Task GlassSelectionAndIndependentSliders_SurviveReload()
    {
        string root = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsService(root);
            var coordinator = new AppearanceSettingsCoordinator(settings);
            var editor = new AppearanceSettingsViewModel(coordinator, key => key);
            Assert.Contains(editor.AvailableMaterialTypeOptions, item =>
                Equals(item.Value, WidgetMaterialKinds.Glass) && item.DisplayName == "Settings.Material.Glass");
            editor.MaterialType = WidgetMaterialKinds.Glass;
            Assert.True(editor.ShowOpacitySlider);
            Assert.True(editor.ShowMaterialIntensitySlider);

            editor.WidgetTransparency = 0.40;
            editor.MaterialIntensity = 0.22;
            Assert.Equal(0.60, settings.Settings.WidgetShell.WidgetOpacity, 5);
            Assert.Equal(0.22, settings.Settings.WidgetShell.WidgetMaterialIntensity, 5);
            Assert.True(await settings.SaveCheckedAsync(notifySubscribers: false));
            coordinator.Stop();

            var reloaded = new SettingsService(root);
            await reloaded.LoadAsync();
            Assert.Equal(WidgetMaterialKinds.Glass, reloaded.Settings.WidgetShell.WidgetMaterialType);
            Assert.Equal(0.60, reloaded.Settings.WidgetShell.WidgetOpacity, 5);
            Assert.Equal(0.22, reloaded.Settings.WidgetShell.WidgetMaterialIntensity, 5);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GlassConcentration_GrowsWithoutBecomingOpaque(bool isDark)
    {
        var clear = WidgetMaterialVisualCalculator.CalculateGlass(isDark, 0);
        var frosted = WidgetMaterialVisualCalculator.CalculateGlass(isDark, 1);
        Assert.True(frosted.TintOpacity > clear.TintOpacity);
        Assert.True(frosted.LuminosityOpacity > clear.LuminosityOpacity);
        Assert.InRange(frosted.TintOpacity, 0, 0.30);
        Assert.InRange(frosted.LuminosityOpacity, 0, 0.35);
        Assert.Equal(frosted, WidgetMaterialVisualCalculator.CalculateGlass(isDark, 100));
        Assert.Equal(clear, WidgetMaterialVisualCalculator.CalculateGlass(isDark, -100));
    }
}
