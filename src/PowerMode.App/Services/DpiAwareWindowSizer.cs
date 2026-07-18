using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace PowerModeWinUI;

internal static class DpiAwareWindowSizer
{
    private const double DefaultDpi = 96d;
    private const int ShowMaximized = 3;
    private const string RegistryPath = @"Software\PowerModeSwitcher\Window";

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(nint windowHandle, ref WindowPlacement placement);

    internal static void Resize(
        Window window,
        int logicalWidth,
        int logicalHeight,
        int minimumLogicalWidth = 0,
        int minimumLogicalHeight = 0,
        bool center = false)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var dpi = GetDpiForWindow(handle);
        var scale = dpi > 0 ? dpi / DefaultDpi : 1d;
        int ToPixels(int value) => (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);

        var width = ToPixels(logicalWidth);
        var height = ToPixels(logicalHeight);
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary);
        if (area is not null)
        {
            var margin = ToPixels(16);
            width = Math.Min(width, Math.Max(1, area.WorkArea.Width - margin * 2));
            height = Math.Min(height, Math.Max(1, area.WorkArea.Height - margin * 2));
        }

        window.AppWindow.Resize(new SizeInt32(width, height));
        if (center && area is not null)
        {
            window.AppWindow.Move(new PointInt32(
                area.WorkArea.X + Math.Max(0, (area.WorkArea.Width - width) / 2),
                area.WorkArea.Y + Math.Max(0, (area.WorkArea.Height - height) / 2)));
        }

        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            if (minimumLogicalWidth > 0)
                presenter.PreferredMinimumWidth = ToPixels(minimumLogicalWidth);
            if (minimumLogicalHeight > 0)
                presenter.PreferredMinimumHeight = ToPixels(minimumLogicalHeight);
        }
    }

    internal static bool TryRestore(
        Window window,
        int minimumLogicalWidth,
        int minimumLogicalHeight)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key is null ||
                key.GetValue("X") is not int savedX ||
                key.GetValue("Y") is not int savedY ||
                key.GetValue("LogicalWidth") is not int logicalWidth ||
                key.GetValue("LogicalHeight") is not int logicalHeight)
                return false;

            logicalWidth = Math.Max(minimumLogicalWidth, logicalWidth);
            logicalHeight = Math.Max(minimumLogicalHeight, logicalHeight);

            var savedPoint = new PointInt32(savedX + 40, savedY + 40);
            var area = DisplayArea.GetFromPoint(savedPoint, DisplayAreaFallback.Primary);
            if (area is null) return false;

            var work = area.WorkArea;
            var pointIsVisible =
                savedPoint.X >= work.X && savedPoint.X < work.X + work.Width &&
                savedPoint.Y >= work.Y && savedPoint.Y < work.Y + work.Height;

            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (pointIsVisible)
                window.AppWindow.Move(new PointInt32(savedX, savedY));

            var dpi = GetDpiForWindow(handle);
            var scale = dpi > 0 ? dpi / DefaultDpi : 1d;
            int ToPixels(int value) => (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);
            var margin = ToPixels(16);
            var width = Math.Min(ToPixels(logicalWidth), Math.Max(1, work.Width - margin * 2));
            var height = Math.Min(ToPixels(logicalHeight), Math.Max(1, work.Height - margin * 2));

            var x = pointIsVisible
                ? Math.Clamp(savedX, work.X, Math.Max(work.X, work.X + work.Width - width))
                : work.X + Math.Max(0, (work.Width - width) / 2);
            var y = pointIsVisible
                ? Math.Clamp(savedY, work.Y, Math.Max(work.Y, work.Y + work.Height - height))
                : work.Y + Math.Max(0, (work.Height - height) / 2);

            window.AppWindow.Move(new PointInt32(x, y));
            window.AppWindow.Resize(new SizeInt32(width, height));

            if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = ToPixels(minimumLogicalWidth);
                presenter.PreferredMinimumHeight = ToPixels(minimumLogicalHeight);
                if (key.GetValue("Maximized") is int maximized && maximized == 1)
                    presenter.Maximize();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static void SavePlacement(Window window)
    {
        try
        {
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            if (!GetWindowPlacement(handle, ref placement)) return;

            var dpi = GetDpiForWindow(handle);
            var scale = dpi > 0 ? dpi / DefaultDpi : 1d;
            var width = Math.Max(1, placement.NormalPosition.Right - placement.NormalPosition.Left);
            var height = Math.Max(1, placement.NormalPosition.Bottom - placement.NormalPosition.Top);

            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue("X", placement.NormalPosition.Left, RegistryValueKind.DWord);
            key.SetValue("Y", placement.NormalPosition.Top, RegistryValueKind.DWord);
            key.SetValue("LogicalWidth", (int)Math.Round(width / scale), RegistryValueKind.DWord);
            key.SetValue("LogicalHeight", (int)Math.Round(height / scale), RegistryValueKind.DWord);
            key.SetValue("Maximized", placement.ShowCommand == ShowMaximized ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
            // Window placement is a convenience; closing must never fail because it cannot be saved.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinimumPosition;
        public NativePoint MaximumPosition;
        public NativeRect NormalPosition;
    }
}
