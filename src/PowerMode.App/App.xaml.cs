using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace PowerModeWinUI;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\PowerMode.WinUI3.SingleInstance";
    private Mutex? _instanceMutex;
    public static MainWindow MainWindow { get; private set; } = null!;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            ShowExistingInstance();
            Exit();
            return;
        }

        var startHidden = args.Arguments.Contains("--minimized", StringComparison.OrdinalIgnoreCase)
            || args.Arguments.Contains("--tray", StringComparison.OrdinalIgnoreCase);
        MainWindow = new MainWindow(startHidden);
        MainWindow.Activate();
    }

    private static void ShowExistingInstance()
    {
        var window = FindWindow(null, "PowerMode");
        if (window == IntPtr.Zero) return;
        ShowWindow(window, 9);
        SetForegroundWindow(window);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
