namespace PowerModeWinUI;

/// <summary>
/// Formats the main status card value for 关屏 / 睡眠 after a custom preset applies.
/// Order matches ApplyStatus: display-off first, sleep second.
/// Custom presets force STANDBYIDLE=0 and VIDEOIDLE=DisplayOffSeconds.
/// </summary>
internal static class CustomProfileSleepDisplay
{
    public static string Format(int displayOffSeconds) =>
        $"{displayOffSeconds}s / 0s";
}
