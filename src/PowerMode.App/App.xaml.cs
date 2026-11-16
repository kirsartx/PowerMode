using Microsoft.UI.Xaml;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace PowerModeWinUI;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\PowerMode.WinUI3.SingleInstance";
    private const int SwRestore = 9;
    private Mutex? _instanceMutex;
    private MemoryMappedFile? _windowMapping;
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
        PublishMainWindow();
        MainWindow.Activate();
    }

    private void PublishMainWindow()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);
        if (handle == IntPtr.Zero)
            return;
        try
        {
            _windowMapping = SingleInstanceWindowBridge.Publish(handle);
            if (_windowMapping is null)
                return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The first instance still runs. A later launch cannot wake this window.
        }
    }

    private static void ShowExistingInstance()
    {
        if (!SingleInstanceWindowBridge.TryRead(out var published) ||
            !SingleInstanceWindowSelector.TrySelect(
                published,
                (uint)Environment.ProcessId,
                SingleInstanceWindowBridge.BelongsToProcess,
                out var windowHandle))
        {
            return;
        }

        var window = new IntPtr(windowHandle);
        ShowWindow(window, SwRestore);
        SetForegroundWindow(window);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
