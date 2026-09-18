using System.Runtime.InteropServices;

namespace PowerModeWinUI;

/// <summary>
/// Seam over the native power APIs so <see cref="NativePowerStateReader"/> can be unit
/// tested without touching the real machine. All values mirror the GUIDs the PowerShell
/// JSON engine path uses, so native reads are byte-for-byte comparable to engine reads.
/// </summary>
internal interface IPowerNativeApi
{
    bool TryGetActiveScheme(out Guid schemeGuid);
    bool TryReadAcValue(Guid scheme, Guid subgroup, Guid setting, out int value);
    bool TryReadDcValue(Guid scheme, Guid subgroup, Guid setting, out int value);
    PowerSourceKind ReadPowerSource();
    bool? ReadWifiDisabled();
}

/// <summary>
/// Reads power state directly through powrprof/GetSystemPowerStatus, avoiding the
/// ~2.7s PowerShell cold start that dominates <see cref="PowerModeBackend.ReadStateAsync"/>.
/// </summary>
internal sealed class NativePowerStateReader
{
    // Exact GUID strings copied from the PowerShell engine JSON path so native and
    // engine reads resolve the same settings for any scheme.
    internal static readonly Guid SchemeSaver = Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a");
    internal static readonly Guid SchemeBalanced = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    internal static readonly Guid SchemeHigh = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    internal static readonly Guid CpuSubgroup = Guid.Parse("54533251-82be-4824-96c1-47b60b740d00");
    internal static readonly Guid CpuMaximum = Guid.Parse("bc5038f7-23e0-4960-96da-33abaf5935ec");
    internal static readonly Guid CpuMinimum = Guid.Parse("893dee8e-2bef-41e0-89c6-b55d0929964c");
    internal static readonly Guid BoostMode = Guid.Parse("be337238-0d82-4146-a960-4f3749d470c7");
    internal static readonly Guid VideoSubgroup = Guid.Parse("7516b95f-f776-4464-8c53-06167f40cc99");
    internal static readonly Guid Brightness = Guid.Parse("aded5e82-b909-4619-9949-f5d71dac0bcb");
    internal static readonly Guid DisplayTimeout = Guid.Parse("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    internal static readonly Guid SleepSubgroup = Guid.Parse("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    internal static readonly Guid SleepTimeout = Guid.Parse("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    internal static readonly Guid HibernateTimeout = Guid.Parse("9d7815a6-7ee4-497e-8888-515a05f02364");

    private readonly IPowerNativeApi _api;

    public NativePowerStateReader(IPowerNativeApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public NativePowerStateReader() : this(new WindowsPowerNativeApi())
    {
    }

    /// <summary>
    /// Returns the current power state, or null when the active scheme cannot be read
    /// (callers should fall back to the engine so a native failure is never a regression).
    /// </summary>
    public PowerModeState? TryRead()
    {
        if (!_api.TryGetActiveScheme(out var scheme))
            return null;

        var cpuMaxAc = ReadAc(scheme, CpuSubgroup, CpuMaximum);
        var cpuMaxDc = ReadDc(scheme, CpuSubgroup, CpuMaximum);

        return new PowerModeState(
            ActiveSchemeId: scheme,
            ActiveSchemeName: DescribeScheme(scheme),
            DetectedMode: DetectMode(scheme, cpuMaxAc),
            PowerSource: _api.ReadPowerSource(),
            DiscreteGpuPowerWatts: null,
            CpuMaximumAcPercent: cpuMaxAc,
            CpuMaximumDcPercent: cpuMaxDc,
            CpuMinimumAcPercent: ReadAc(scheme, CpuSubgroup, CpuMinimum),
            CpuMinimumDcPercent: ReadDc(scheme, CpuSubgroup, CpuMinimum),
            ProcessorBoostModeAc: ReadAc(scheme, CpuSubgroup, BoostMode),
            ProcessorBoostModeDc: ReadDc(scheme, CpuSubgroup, BoostMode),
            BrightnessAcPercent: ReadAc(scheme, VideoSubgroup, Brightness),
            BrightnessDcPercent: ReadDc(scheme, VideoSubgroup, Brightness),
            DisplayTimeoutAcSeconds: ReadAc(scheme, VideoSubgroup, DisplayTimeout),
            DisplayTimeoutDcSeconds: ReadDc(scheme, VideoSubgroup, DisplayTimeout),
            SleepTimeoutAcSeconds: ReadAc(scheme, SleepSubgroup, SleepTimeout),
            SleepTimeoutDcSeconds: ReadDc(scheme, SleepSubgroup, SleepTimeout),
            HibernateTimeoutAcSeconds: ReadAc(scheme, SleepSubgroup, HibernateTimeout),
            HibernateTimeoutDcSeconds: ReadDc(scheme, SleepSubgroup, HibernateTimeout),
            WifiDisabled: _api.ReadWifiDisabled());
    }

    private int? ReadAc(Guid scheme, Guid subgroup, Guid setting) =>
        _api.TryReadAcValue(scheme, subgroup, setting, out var value) ? value : null;

    private int? ReadDc(Guid scheme, Guid subgroup, Guid setting) =>
        _api.TryReadDcValue(scheme, subgroup, setting, out var value) ? value : null;

    internal static string DescribeScheme(Guid scheme)
    {
        if (scheme == SchemeSaver) return "Power Saver (Hermes Remote Optimized)";
        if (scheme == SchemeBalanced) return "Balanced";
        if (scheme == SchemeHigh) return "High Performance";
        return $"Unknown ({scheme:D})";
    }

    internal static PowerModePreset? DetectMode(Guid scheme, int? cpuMaxAc) => scheme switch
    {
        var s when s == SchemeSaver => cpuMaxAc is { } cpu && cpu <= 30
            ? PowerModePreset.Saver
            : PowerModePreset.Remote,
        var s when s == SchemeBalanced => PowerModePreset.Balanced,
        var s when s == SchemeHigh => PowerModePreset.High,
        _ => null
    };
}

internal sealed class WindowsPowerNativeApi : IPowerNativeApi
{
    public bool TryGetActiveScheme(out Guid schemeGuid)
    {
        schemeGuid = Guid.Empty;
        // PowerGetActiveScheme returns a locally-allocated GUID pointer that must be freed.
        if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero)
            return false;

        try
        {
            schemeGuid = Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            LocalFree(pointer);
        }

        return schemeGuid != Guid.Empty;
    }

    public bool TryReadAcValue(Guid scheme, Guid subgroup, Guid setting, out int value)
    {
        uint raw = 0;
        var result = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, ref raw);
        value = unchecked((int)raw);
        return result == 0;
    }

    public bool TryReadDcValue(Guid scheme, Guid subgroup, Guid setting, out int value)
    {
        uint raw = 0;
        var result = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, ref raw);
        value = unchecked((int)raw);
        return result == 0;
    }

    public PowerSourceKind ReadPowerSource()
    {
        if (!GetSystemPowerStatus(out var status))
            return PowerSourceKind.Unknown;

        var onAc = status.ACLineStatus == 1;
        var noBattery = status.BatteryFlag != byte.MaxValue && (status.BatteryFlag & 128) != 0;
        var charging = status.BatteryFlag != byte.MaxValue && (status.BatteryFlag & 8) != 0;

        if (noBattery)
            return PowerSourceKind.Ac;
        if (charging)
            return status.BatteryLifePercent == 100 ? PowerSourceKind.Full : PowerSourceKind.Charging;
        if (onAc)
            return status.BatteryLifePercent == 100 ? PowerSourceKind.Full : PowerSourceKind.Ac;
        if (!onAc)
            return PowerSourceKind.Battery;
        return PowerSourceKind.Unknown;
    }

    public bool? ReadWifiDisabled()
    {
        try
        {
            var adapters = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            var wireless = false;
            var anyUp = false;
            foreach (var adapter in adapters)
            {
                if (adapter.NetworkInterfaceType !=
                    System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
                {
                    continue;
                }

                wireless = true;
                if (adapter.OperationalStatus ==
                    System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    anyUp = true;
                    break;
                }
            }

            return wireless ? !anyUp : null;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    [DllImport("powrprof.dll")]
    private static extern int PowerGetActiveScheme(
        IntPtr userPowerKey,
        out IntPtr activePolicyGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    [DllImport("powrprof.dll")]
    private static extern int PowerReadACValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupGuid,
        ref Guid settingGuid,
        ref uint value);

    [DllImport("powrprof.dll")]
    private static extern int PowerReadDCValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupGuid,
        ref Guid settingGuid,
        ref uint value);
}
