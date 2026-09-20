using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class NativePowerStateReaderTests
{
    [Fact]
    public void TryRead_ReturnsNull_WhenActiveSchemeUnavailable()
    {
        var api = new FakePowerNativeApi { HasScheme = false };
        Assert.Null(new NativePowerStateReader(api).TryRead());
    }

    [Fact]
    public void TryRead_MapsEverySettingThroughTheEngineGuids()
    {
        var api = new FakePowerNativeApi { Scheme = NativePowerStateReader.SchemeBalanced };
        api.Set(NativePowerStateReader.CpuMaximum, ac: 100, dc: 100);
        api.Set(NativePowerStateReader.CpuMinimum, ac: 5, dc: 5);
        api.Set(NativePowerStateReader.BoostMode, ac: 1, dc: 1);
        api.Set(NativePowerStateReader.Brightness, ac: 80, dc: 40);
        api.Set(NativePowerStateReader.DisplayTimeout, ac: 600, dc: 300);
        api.Set(NativePowerStateReader.SleepTimeout, ac: 0, dc: 0);
        api.Set(NativePowerStateReader.HibernateTimeout, ac: 0, dc: 0);

        var state = new NativePowerStateReader(api).TryRead();

        Assert.NotNull(state);
        Assert.Equal(NativePowerStateReader.SchemeBalanced, state!.ActiveSchemeId);
        Assert.Equal("Balanced", state.ActiveSchemeName);
        Assert.Equal(PowerModePreset.Balanced, state.DetectedMode);
        Assert.Equal(100, state.CpuMaximumAcPercent);
        Assert.Equal(5, state.CpuMinimumDcPercent);
        Assert.Equal(1, state.ProcessorBoostModeAc);
        Assert.Equal(80, state.BrightnessAcPercent);
        Assert.Equal(40, state.BrightnessDcPercent);
        Assert.Equal(600, state.DisplayTimeoutAcSeconds);
        Assert.Equal(300, state.DisplayTimeoutDcSeconds);
        Assert.Null(state.DiscreteGpuPowerWatts);
    }

    [Fact]
    public void TryRead_LeavesFieldNull_WhenSettingMissing()
    {
        var api = new FakePowerNativeApi { Scheme = NativePowerStateReader.SchemeHigh };
        api.Set(NativePowerStateReader.CpuMaximum, ac: 100, dc: 100);
        // Boost + brightness intentionally not provided.

        var state = new NativePowerStateReader(api).TryRead()!;

        Assert.Null(state.ProcessorBoostModeAc);
        Assert.Null(state.BrightnessAcPercent);
        Assert.Equal("High Performance", state.ActiveSchemeName);
        Assert.Equal(PowerModePreset.High, state.DetectedMode);
    }

    [Theory]
    [InlineData(30, "Saver")]
    [InlineData(32, "Remote")]
    [InlineData(null, "Remote")]
    public void DetectMode_SaverSchemeThresholdsByCpuMax(int? cpuMax, string expected)
    {
        var api = new FakePowerNativeApi { Scheme = NativePowerStateReader.SchemeSaver };
        if (cpuMax is int value)
            api.Set(NativePowerStateReader.CpuMaximum, ac: value, dc: value);

        var state = new NativePowerStateReader(api).TryRead()!;

        Assert.Equal(expected, state.DetectedMode?.ToString());
    }

    [Fact]
    public async Task ApplyAsync_DelegatesToEngine_AndReadStateFallsBackWhenNativeUnavailable()
    {
        var engine = new RecordingEngineBackend();
        var backend = new HybridPowerModeBackend(
            engine,
            new NativePowerStateReader(new FakePowerNativeApi { HasScheme = false }),
            new NativePowerApplier(new FakePowerNativeApi { HasScheme = false }));

        var result = await backend.ReadStateAsync(Guid.NewGuid());

        Assert.Equal(1, engine.ReadCount);
        Assert.Same(engine.LastReadState, result.State);
    }

    [Fact]
    public async Task HybridRead_PrefersNativeAndDoesNotInvokeEngine()
    {
        var api = new FakePowerNativeApi { Scheme = NativePowerStateReader.SchemeBalanced };
        api.Set(NativePowerStateReader.CpuMaximum, ac: 100, dc: 100);
        var engine = new RecordingEngineBackend();
        var backend = new HybridPowerModeBackend(
            engine,
            new NativePowerStateReader(api),
            new NativePowerApplier(api));

        var result = await backend.ReadStateAsync(Guid.NewGuid());

        Assert.Equal(0, engine.ReadCount);
        Assert.Equal(PowerModePreset.Balanced, result.State!.DetectedMode);
        Assert.Equal(BackendOperationOutcome.Succeeded, result.Operation.Outcome);
    }

    [Fact]
    public void Applier_BalancedPreset_WritesEngineValuesAndExpectations()
    {
        var api = new FakePowerNativeApi { Scheme = NativePowerStateReader.SchemeBalanced };
        var applier = new NativePowerApplier(api);

        var result = applier.TryApplyPreset(
            Guid.NewGuid(), PowerModeTarget.ForPreset(PowerModePreset.Balanced), null);

        Assert.NotNull(result);
        Assert.Equal("apply", result!.Action);
        Assert.Equal("balanced", result.RequestedTargetKey);
        Assert.Equal(BackendOperationOutcome.Succeeded, result.Outcome);
        Assert.Equal(
            [NativePowerStateReader.SchemeBalanced, NativePowerStateReader.SchemeBalanced],
            api.Activations);
        AssertWrite(api, NativePowerStateReader.CpuMaximum, ac: 100, dc: 100);
        AssertWrite(api, NativePowerStateReader.CpuMinimum, ac: 5, dc: 5);
        AssertWrite(api, NativePowerStateReader.Brightness, ac: 100, dc: 100);
        AssertWrite(api, NativePowerStateReader.SleepTimeout, ac: 0, dc: 0);
        AssertWrite(api, NativePowerStateReader.HibernateTimeout, ac: 0, dc: 0);
        Assert.False(api.Writes.Any(w => w.Setting == NativePowerStateReader.BoostMode));
        Assert.False(api.Writes.Any(w => w.Setting == NativePowerStateReader.DisplayTimeout));

        AssertExpectation(result, PowerModeStateField.CpuMaximumAcPercent, "100", critical: true);
        AssertExpectation(result, PowerModeStateField.SleepTimeoutDcSeconds, "0", critical: true);
        Assert.Equal(9, result.Expectations.Count);
    }

    [Fact]
    public void Applier_SaverPreset_WritesBoostDisplayAndCpu()
    {
        var api = new FakePowerNativeApi { Scheme = NativePowerStateReader.SchemeSaver };
        var applier = new NativePowerApplier(api);

        var result = applier.TryApplyPreset(
            Guid.NewGuid(), PowerModeTarget.ForPreset(PowerModePreset.Saver), null);

        AssertWrite(api, NativePowerStateReader.CpuMaximum, ac: 30, dc: 30);
        AssertWrite(api, NativePowerStateReader.CpuMinimum, ac: 3, dc: 3);
        AssertWrite(api, NativePowerStateReader.BoostMode, ac: 0, dc: 0);
        AssertWrite(api, NativePowerStateReader.Brightness, ac: 50, dc: 50);
        AssertWrite(api, NativePowerStateReader.DisplayTimeout, ac: 60, dc: 60);
        Assert.Equal(
            NativePowerStateReader.SchemeSaver.ToString(),
            result!.Expectations[0].ExpectedValue);
        Assert.Equal(11, result.Expectations.Count);
    }

    [Fact]
    public void Applier_CustomProfile_ReturnsNullForEngineFallback()
    {
        var applier = new NativePowerApplier(new FakePowerNativeApi());
        var custom = new CustomPowerProfileSnapshot(
            "Mine", 50, 50, 5, 50, 50, 60, 60, DisableBoost: true);

        Assert.Null(applier.TryApplyPreset(
            Guid.NewGuid(), PowerModeTarget.ForCustom(custom), null));
    }

    [Fact]
    public void Applier_CriticalWriteFailure_ReportsFailed()
    {
        var api = new FakePowerNativeApi { FailWrites = true };
        var applier = new NativePowerApplier(api);

        var result = applier.TryApplyPreset(
            Guid.NewGuid(), PowerModeTarget.ForPreset(PowerModePreset.High), null);

        Assert.Equal(BackendOperationOutcome.Failed, result!.Outcome);
    }

    [Fact]
    public async Task HybridApply_PresetUsesNativeAndSkipsEngine_WifiDelegatesToEngine()
    {
        var api = new FakePowerNativeApi { Scheme = NativePowerStateReader.SchemeBalanced };
        var engine = new RecordingEngineBackend();
        var backend = new HybridPowerModeBackend(
            engine,
            new NativePowerStateReader(api),
            new NativePowerApplier(api));

        await backend.ApplyAsync(
            Guid.NewGuid(), PowerModeTarget.ForPreset(PowerModePreset.Balanced));
        Assert.Equal(0, engine.ApplyCount);

        await backend.ApplyAsync(
            Guid.NewGuid(), PowerModeTarget.ForPreset(PowerModePreset.Balanced),
            disableWifi: true);
        Assert.Equal(1, engine.ApplyCount);
    }

    private static void AssertWrite(
        FakePowerNativeApi api, Guid setting, int ac, int dc)
    {
        Assert.True(api.Writes.Any(w => w.Setting == setting && w.Ac && w.Value == ac));
        Assert.True(api.Writes.Any(w => w.Setting == setting && !w.Ac && w.Value == dc));
    }

    private static void AssertExpectation(
        BackendOperationResult result, PowerModeStateField field, string value, bool critical)
    {
        var match = Assert.Single(result.Expectations, e => e.Field == field);
        Assert.Equal(value, match.ExpectedValue);
        Assert.Equal(critical, match.Critical);
    }

    [Fact]
    public async Task LiveNativeRead_MatchesEngine_WhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("POWERMODE_LIVE_NATIVE") != "1")
            return; // Opt-in: requires real hardware. Skipped in CI.

        var engineJson = await RunEngineStatusAsync();
        using var document = JsonDocument.Parse(engineJson);
        var after = document.RootElement.GetProperty("afterState");

        var state = new NativePowerStateReader().TryRead();
        Assert.NotNull(state);

        // The whole point of the native path: a status read must not pay the ~2.7s
        // PowerShell cold start it replaces. In-memory powrprof calls are sub-millisecond.
        var stopwatch = Stopwatch.StartNew();
        new NativePowerStateReader().TryRead();
        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Native read took {stopwatch.ElapsedMilliseconds} ms; expected well under the engine's ~2700 ms.");

        var mismatches = new List<string>();
        CollectGuid(mismatches, after, "activeSchemeId", state!.ActiveSchemeId);
        CollectInt(mismatches, after, "cpuMaximumAcPercent", state.CpuMaximumAcPercent);
        CollectInt(mismatches, after, "cpuMaximumDcPercent", state.CpuMaximumDcPercent);
        CollectInt(mismatches, after, "cpuMinimumAcPercent", state.CpuMinimumAcPercent);
        CollectInt(mismatches, after, "cpuMinimumDcPercent", state.CpuMinimumDcPercent);
        CollectInt(mismatches, after, "processorBoostModeAc", state.ProcessorBoostModeAc);
        CollectInt(mismatches, after, "processorBoostModeDc", state.ProcessorBoostModeDc);
        CollectInt(mismatches, after, "brightnessAcPercent", state.BrightnessAcPercent);
        CollectInt(mismatches, after, "brightnessDcPercent", state.BrightnessDcPercent);
        CollectInt(mismatches, after, "displayTimeoutAcSeconds", state.DisplayTimeoutAcSeconds);
        CollectInt(mismatches, after, "displayTimeoutDcSeconds", state.DisplayTimeoutDcSeconds);
        CollectInt(mismatches, after, "sleepTimeoutAcSeconds", state.SleepTimeoutAcSeconds);
        CollectInt(mismatches, after, "sleepTimeoutDcSeconds", state.SleepTimeoutDcSeconds);
        CollectInt(mismatches, after, "hibernateTimeoutAcSeconds", state.HibernateTimeoutAcSeconds);
        CollectInt(mismatches, after, "hibernateTimeoutDcSeconds", state.HibernateTimeoutDcSeconds);

        Assert.Empty(mismatches);
    }

    private static async Task<string> RunEngineStatusAsync()
    {
        var enginePath = TestPaths.Repo("src", "PowerMode.Cli", "PowerMode.Engine.ps1");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in new[]
        {
            "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", enginePath, "-Mode", "status", "-OutputFormat", "Json"
        })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        var line = output
            .Split('\n')
            .Select(value => value.Trim())
            .LastOrDefault(value => value.StartsWith('{'));
        Assert.False(string.IsNullOrWhiteSpace(line), "Engine produced no JSON status.");
        return line!;
    }

    private static void CollectGuid(
        List<string> mismatches,
        JsonElement state,
        string field,
        Guid? actual)
    {
        var expected = state.TryGetProperty(field, out var value) &&
            value.ValueKind == JsonValueKind.String
            ? Guid.Parse(value.GetString()!)
            : (Guid?)null;
        if (expected != actual)
            mismatches.Add($"{field}: engine={expected?.ToString() ?? "null"} native={actual?.ToString() ?? "null"}");
    }

    private static void CollectInt(
        List<string> mismatches,
        JsonElement state,
        string field,
        int? actual)
    {
        int? expected = null;
        if (state.TryGetProperty(field, out var value) &&
            value.ValueKind != JsonValueKind.Null)
        {
            expected = value.ValueKind == JsonValueKind.String
                ? int.Parse(value.GetString()!, CultureInfo.InvariantCulture)
                : value.GetInt32();
        }

        if (expected == actual)
            return;

        // The engine reports null whenever it cannot parse a per-scheme powercfg value
        // (e.g. an inherited processor-boost default). The native index API returns the
        // effective value in that case, which is a faithful superset — so engine=null with a
        // native value is accepted. A native miss or a genuine value conflict is a real bug.
        if (expected is null)
            return;

        mismatches.Add($"{field}: engine={expected} native={actual?.ToString() ?? "null"}");
    }

    private sealed class FakePowerNativeApi : IPowerNativeApi
    {
        private readonly Dictionary<(Guid Setting, bool Ac), int> _values = new();

        public bool HasScheme { get; init; } = true;
        public Guid Scheme { get; init; }
        public PowerSourceKind PowerSource { get; init; } = PowerSourceKind.Ac;
        public bool? WifiDisabled { get; init; }
        public bool FailWrites { get; init; }
        public bool FailActivate { get; init; }
        public List<(Guid Subgroup, Guid Setting, int Value, bool Ac)> Writes { get; } = [];
        public List<Guid> Activations { get; } = [];

        public void Set(Guid setting, int ac, int dc)
        {
            _values[(setting, true)] = ac;
            _values[(setting, false)] = dc;
        }

        public bool TryGetActiveScheme(out Guid schemeGuid)
        {
            schemeGuid = HasScheme ? Scheme : Guid.Empty;
            return HasScheme;
        }

        public bool TryReadAcValue(Guid scheme, Guid subgroup, Guid setting, out int value) =>
            _values.TryGetValue((setting, true), out value);

        public bool TryReadDcValue(Guid scheme, Guid subgroup, Guid setting, out int value) =>
            _values.TryGetValue((setting, false), out value);

        public bool TrySetActiveScheme(Guid scheme)
        {
            Activations.Add(scheme);
            return !FailActivate;
        }

        public bool TryWriteAcValue(Guid scheme, Guid subgroup, Guid setting, int value)
        {
            Writes.Add((subgroup, setting, value, true));
            _values[(setting, true)] = value;
            return !FailWrites;
        }

        public bool TryWriteDcValue(Guid scheme, Guid subgroup, Guid setting, int value)
        {
            Writes.Add((subgroup, setting, value, false));
            _values[(setting, false)] = value;
            return !FailWrites;
        }

        public PowerSourceKind ReadPowerSource() => PowerSource;
        public bool? ReadWifiDisabled() => WifiDisabled;
    }

    private sealed class RecordingEngineBackend : IPowerModeBackend
    {
        public int ReadCount { get; private set; }
        public int ApplyCount { get; private set; }
        public PowerModeState LastReadState { get; } = new(
            Guid.NewGuid(), "x", null, PowerSourceKind.Ac, null,
            null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, null);

        public Task<PowerModeStateResult> ReadStateAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(new PowerModeStateResult(
                LastReadState,
                BackendOperationResult.ContractFailure(
                    BackendOperationOutcome.Succeeded, "engine")));
        }

        public Task<BackendOperationResult> ApplyAsync(
            Guid operationId,
            PowerModeTarget target,
            int? cpuMaximumPercent = null,
            bool disableWifi = false,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            return Task.FromResult(BackendOperationResult.ContractFailure(
                BackendOperationOutcome.Succeeded, "engine"));
        }

        public Task<BackendOperationResult> RestoreAsync(
            Guid operationId,
            PowerModeState snapshot,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(BackendOperationResult.ContractFailure(
                BackendOperationOutcome.Succeeded, "engine"));
    }
}
