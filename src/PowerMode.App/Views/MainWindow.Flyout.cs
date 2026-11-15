using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace PowerModeWinUI;

public sealed partial class MainWindow
{
    private const string WindowRegistryPath = @"Software\PowerModeSwitcher\Window";
    private const int AutoHideDelayMilliseconds = 120;
    private readonly TrayClickFilter _trayClickFilter = new();
    private bool _isPinned = true;
    private bool _flyoutResizeQueued;
    private int _autoHideGeneration;

    private void ConfigureWindow()
    {
        _isPinned = ReadPinned();
        ApplyWindowChrome();
        FlyoutNative.RoundCorners(WinRT.Interop.WindowNative.GetWindowHandle(this));
        ResizeToContent(FlyoutWindowPolicy.MinimumContentHeight);
        if (_isPinned)
            RestorePinnedPosition();
        else
            AnchorFlyout(null);
        Activated += Flyout_Activated;
    }

    private void ApplyWindowChrome()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            presenter = OverlappedPresenter.Create();
            AppWindow.SetPresenter(presenter);
        }

        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = _isPinned;
        presenter.IsAlwaysOnTop = !_isPinned;
        presenter.SetBorderAndTitleBar(true, _isPinned);
        ExtendsContentIntoTitleBar = _isPinned;
        SetTitleBar(_isPinned ? HeaderDragRegion : null);
        AppWindow.IsShownInSwitchers = _isPinned;
        Grid.SetRow(HeaderActions, _isPinned ? 1 : 0);
        ApplyPinPresentation();
    }

    private void ApplyPinPresentation()
    {
        PinIcon.Glyph = _isPinned ? "\uE77A" : "\uE718";
        var text = T(_isPinned ? "Unpin" : "Pin");
        AutomationProperties.SetName(PinButton, text);
        ToolTipService.SetToolTip(PinButton, text);
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        WritePinned(_isPinned);
        ApplyWindowChrome();
        UpdateFlyoutSize();
        if (_isPinned)
            KeepPinnedWindowInside();
        else
            AnchorFlyout(null);
        Activate();
    }

    private void ToggleFlyoutFromTray()
    {
        var covered = _isPinned && !FlyoutNative.IsForeground(_hwnd);
        if (AppWindow.IsVisible && !covered)
            HideFlyout();
        else
            ShowFlyout(fromTray: true);
    }

    private void ShowFlyout(bool fromTray)
    {
        if (!_isPinned)
        {
            UpdateFlyoutSize();
            AnchorFlyout(fromTray ? DisplayAreaAtCursor() : null);
        }

        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        FlyoutNative.SetForegroundWindow(_hwnd);
    }

    private void HideFlyout() => AppWindow.Hide();

    private async void Flyout_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated || _isPinned)
        {
            _autoHideGeneration++;
            return;
        }

        var generation = ++_autoHideGeneration;
        await Task.Delay(AutoHideDelayMilliseconds);
        if (!FlyoutWindowPolicy.ShouldCompleteAutoHide(
                generation,
                _autoHideGeneration,
                _isPinned,
                AppWindow.IsVisible,
                FlyoutNative.ForegroundBelongsToCurrentProcess()))
            return;

        HideFlyout();
        _trayClickFilter.NotifyAutoHidden(Environment.TickCount64);
    }

    private void FlyoutSection_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_flyoutResizeQueued)
            return;
        _flyoutResizeQueued = true;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                _flyoutResizeQueued = false;
                UpdateFlyoutSize();
            });
    }

    private void UpdateFlyoutSize()
    {
        var padding = RootGrid.Padding;
        var logicalHeight =
            padding.Top + padding.Bottom +
            HeaderGrid.ActualHeight +
            FlyoutContent.DesiredSize.Height +
            FooterBorder.ActualHeight +
            RootGrid.RowSpacing * 2;
        if (!ResizeToContent(logicalHeight))
            return;

        if (_isPinned)
            KeepPinnedWindowInside();
        else
            AnchorFlyout(null);
    }

    private bool ResizeToContent(double logicalHeight)
    {
        var scale = CurrentScale();
        var width = FlyoutWindowPolicy.ToPixels(FlyoutWindowPolicy.ContentWidth, scale);
        var height = FlyoutWindowPolicy.ClampHeight(logicalHeight, scale, CurrentWorkArea(null));
        var client = AppWindow.ClientSize;
        if (client.Width == width && client.Height == height)
            return false;
        // ResizeClient reserves a system caption even when content is extended into the
        // title bar, so size the outer frame from the measured frame thickness instead.
        var outer = AppWindow.Size;
        AppWindow.Resize(new SizeInt32(
            width + Math.Max(0, outer.Width - client.Width),
            height + Math.Max(0, outer.Height - client.Height)));
        return true;
    }

    private void AnchorFlyout(DisplayArea? area)
    {
        var size = AppWindow.Size;
        var bounds = FlyoutWindowPolicy.AnchorBottomRight(
            CurrentWorkArea(area),
            size.Width,
            size.Height,
            CurrentScale());
        AppWindow.Move(new PointInt32(bounds.X, bounds.Y));
    }

    private void KeepPinnedWindowInside()
    {
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        var bounds = FlyoutWindowPolicy.KeepInside(
            CurrentWorkArea(null),
            position.X,
            position.Y,
            size.Width,
            size.Height);
        if (bounds.X != position.X || bounds.Y != position.Y)
            AppWindow.Move(new PointInt32(bounds.X, bounds.Y));
    }

    private void RestorePinnedPosition()
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowRegistryPath);
        if (key?.GetValue("X") is int x && key.GetValue("Y") is int y)
        {
            var area = DisplayArea.GetFromPoint(new PointInt32(x + 40, y + 40), DisplayAreaFallback.Primary);
            var size = AppWindow.Size;
            var bounds = FlyoutWindowPolicy.KeepInside(ToPixelRect(area.WorkArea), x, y, size.Width, size.Height);
            AppWindow.Move(new PointInt32(bounds.X, bounds.Y));
            return;
        }

        var work = CurrentWorkArea(null);
        var current = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            work.X + Math.Max(0, (work.Width - current.Width) / 2),
            work.Y + Math.Max(0, (work.Height - current.Height) / 2)));
    }

    private double CurrentScale()
    {
        if (RootGrid.XamlRoot is { RasterizationScale: > 0 } root)
            return root.RasterizationScale;
        var dpi = FlyoutNative.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        return dpi > 0 ? dpi / 96d : 1d;
    }

    private PixelRect CurrentWorkArea(DisplayArea? area)
    {
        area ??= DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        return ToPixelRect(area.WorkArea);
    }

    private static DisplayArea DisplayAreaAtCursor()
    {
        FlyoutNative.GetCursorPos(out var point);
        return DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Primary);
    }

    private static PixelRect ToPixelRect(RectInt32 rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private static bool ReadPinned()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowRegistryPath);
            return key?.GetValue("Pinned") is not int pinned || pinned != 0;
        }
        catch
        {
            return true;
        }
    }

    private static void WritePinned(bool pinned)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(WindowRegistryPath);
            key.SetValue("Pinned", pinned ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
            // Pin state is a convenience; failing to persist it must not block the toggle.
        }
    }

    private static class FlyoutNative
    {
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwcpRound = 2;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point { public int X, Y; }

        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        internal static bool IsForeground(IntPtr hwnd) => GetForegroundWindow() == hwnd;

        internal static bool ForegroundBelongsToCurrentProcess()
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return false;
            GetWindowThreadProcessId(foreground, out var processId);
            return processId == (uint)Environment.ProcessId;
        }

        internal static void RoundCorners(IntPtr hwnd)
        {
            var preference = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
    }
}
