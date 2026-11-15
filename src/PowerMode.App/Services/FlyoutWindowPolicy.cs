namespace PowerModeWinUI;

internal readonly record struct PixelRect(int X, int Y, int Width, int Height);

internal static class FlyoutWindowPolicy
{
    public const int ContentWidth = 360;
    public const int EdgeMargin = 12;
    public const int MinimumContentHeight = 200;

    public static int ToPixels(double logical, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1;
        return (int)Math.Round(logical * scale, MidpointRounding.AwayFromZero);
    }

    public static int ClampHeight(double logicalHeight, double scale, PixelRect workArea)
    {
        var maximum = Math.Max(1, workArea.Height - ToPixels(EdgeMargin, scale) * 2);
        var minimum = Math.Min(ToPixels(MinimumContentHeight, scale), maximum);
        var desired = double.IsFinite(logicalHeight) ? ToPixels(logicalHeight, scale) : minimum;
        return Math.Clamp(desired, minimum, maximum);
    }

    public static PixelRect AnchorBottomRight(
        PixelRect workArea,
        int width,
        int height,
        double scale)
    {
        var margin = ToPixels(EdgeMargin, scale);
        return new(
            workArea.X + workArea.Width - width - margin,
            workArea.Y + workArea.Height - height - margin,
            width,
            height);
    }

    public static PixelRect KeepInside(
        PixelRect workArea,
        int x,
        int y,
        int width,
        int height) =>
        new(
            Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.X + workArea.Width - width)),
            Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Y + workArea.Height - height)),
            width,
            height);

    public static bool ShouldAutoHide(bool isPinned, bool foregroundBelongsToApp) =>
        !isPinned && !foregroundBelongsToApp;
}

internal enum TrayClickAction
{
    None,
    Toggle,
    Show
}

/// <summary>
/// Shell sends button-up, double-click, button-up for a double click, and a click on the tray
/// icon first deactivates (auto-hides) the flyout. Without filtering, both would immediately
/// undo the user's intent.
/// </summary>
internal sealed class TrayClickFilter
{
    public const long AutoHideGraceMilliseconds = 400;

    private bool _swallowNextButtonUp;
    private long? _autoHiddenAt;

    public void NotifyAutoHidden(long milliseconds) => _autoHiddenAt = milliseconds;

    public TrayClickAction OnDoubleClick(long milliseconds)
    {
        _swallowNextButtonUp = true;
        return TrayClickAction.Show;
    }

    public TrayClickAction OnLeftButtonUp(long milliseconds)
    {
        if (_swallowNextButtonUp)
        {
            _swallowNextButtonUp = false;
            return TrayClickAction.None;
        }

        if (_autoHiddenAt is { } hiddenAt)
        {
            _autoHiddenAt = null;
            if (milliseconds - hiddenAt < AutoHideGraceMilliseconds)
                return TrayClickAction.None;
        }

        return TrayClickAction.Toggle;
    }
}
