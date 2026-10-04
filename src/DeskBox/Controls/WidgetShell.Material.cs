using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

public sealed partial class WidgetShell
{
    private WidgetMaterialSystemBackdrop? _materialBackdrop;
    private long? _materialCornerCallback;
    private long? _materialBorderCallback;

    internal void ApplyMaterialSurface(WidgetMaterialBackdropAppearance? appearance)
    {
        if (appearance is not { } material)
        {
            _materialBackdrop?.Suspend();
            MaterialPlate.Visibility = Visibility.Collapsed;
            GlassReflection.Visibility = Visibility.Collapsed;
            ShellRoot.CornerRadius = new CornerRadius(0);
            return;
        }

        if (_materialBackdrop is null)
        {
            _materialBackdrop = new WidgetMaterialSystemBackdrop(
                material, closeElementTargetOnDisconnect: true);
            MaterialPlate.SystemBackdrop = _materialBackdrop;
            _materialCornerCallback = BackgroundPlate.RegisterPropertyChangedCallback(
                Border.CornerRadiusProperty, (_, _) => UpdateMaterialCorners());
            _materialBorderCallback = BackgroundPlate.RegisterPropertyChangedCallback(
                Border.BorderThicknessProperty, (_, _) => UpdateMaterialCorners());
        }
        else
        {
            _materialBackdrop.UpdateAppearance(material);
        }

        bool isGlass = material.MaterialType == SettingsService.WidgetMaterialTypeGlass;
        MaterialPlate.Opacity = isGlass ? material.SurfaceOpacity : 1;
        MaterialPlate.Visibility = Visibility.Visible;
        GlassReflection.Opacity = material.SurfaceOpacity * (0.35 + material.MaterialIntensity * 0.65);
        GlassReflection.Visibility = isGlass ? Visibility.Visible : Visibility.Collapsed;
        UpdateMaterialCorners();
    }

    private void UpdateMaterialCorners()
    {
        CornerRadius radius = BackgroundPlate.CornerRadius;
        MaterialPlate.CornerRadius = radius;
        GlassReflection.CornerRadius = radius;
        GlassReflection.BorderThickness = BackgroundPlate.BorderThickness;
        if (MaterialPlate.Visibility == Visibility.Visible)
        {
            ShellRoot.CornerRadius = radius;
        }
    }

    private void ReleaseMaterialSurface()
    {
        if (_materialCornerCallback is { } token)
        {
            BackgroundPlate.UnregisterPropertyChangedCallback(Border.CornerRadiusProperty, token);
            _materialCornerCallback = null;
        }
        _materialBackdrop?.Suspend();
        if (_materialBorderCallback is { } borderToken)
        {
            BackgroundPlate.UnregisterPropertyChangedCallback(Border.BorderThicknessProperty, borderToken);
            _materialBorderCallback = null;
        }
    }
}
