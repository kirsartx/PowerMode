namespace PowerModeWinUI;

internal sealed record CapabilityControlPresentationState(
    bool IsVisible,
    bool IsEnabled,
    string? ToolTip,
    string HelpText,
    bool ApplyExtraOpacityDimming);

internal static class CapabilityControlPresentation
{
    public static CapabilityControlPresentationState Map(FeaturePresentation presentation) =>
        new(
            presentation.IsVisible,
            presentation.IsEnabled,
            string.IsNullOrEmpty(presentation.Reason) ? null : presentation.Reason,
            presentation.Reason,
            ApplyExtraOpacityDimming: false);
}
