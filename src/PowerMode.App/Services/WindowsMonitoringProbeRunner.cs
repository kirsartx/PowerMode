using System.Globalization;
using System.Xml.Linq;

namespace PowerModeWinUI;

internal sealed class WindowsMonitoringProbeRunner(IProcessRunner processRunner)
    : IMonitoringProbeRunner
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan BatteryReportTimeout = TimeSpan.FromSeconds(30);
    private readonly IProcessRunner _processRunner =
        processRunner ?? throw new ArgumentNullException(nameof(processRunner));

    public async Task<NvidiaTelemetry?> QueryNvidiaAsync(CancellationToken token)
    {
        var result = await _processRunner.RunAsync(
            new ProcessExecutionRequest(
                "nvidia-smi.exe",
                ["--query-gpu=power.draw,temperature.gpu,utilization.gpu", "--format=csv,noheader,nounits"],
                ProbeTimeout),
            token).ConfigureAwait(false);
        if (!result.Succeeded)
            return null;

        double totalPower = 0;
        double? maximumTemperature = null;
        double? maximumUtilization = null;
        var hasPower = false;
        foreach (var line in result.StandardOutput.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length < 3)
                continue;
            if (TryParseNumber(fields[0], out var power) && power is >= 0 and < 10_000)
            {
                totalPower += power;
                hasPower = true;
            }
            if (TryParseNumber(fields[1], out var temperature) && temperature is > 0 and <= 150)
                maximumTemperature = MaxNullable(maximumTemperature, temperature);
            if (TryParseNumber(fields[2], out var utilization) && utilization is >= 0 and <= 100)
                maximumUtilization = MaxNullable(maximumUtilization, utilization);
        }

        if (!hasPower && maximumTemperature is null && maximumUtilization is null)
            return null;
        return new NvidiaTelemetry(
            hasPower ? Math.Round(totalPower, 2) : null,
            maximumTemperature,
            maximumUtilization);
    }

    public async Task<double?> QueryTemperatureAsync(CancellationToken token)
    {
        const string command =
            "$ErrorActionPreference='SilentlyContinue';" +
            "Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature | " +
            "ForEach-Object { (($_.CurrentTemperature / 10.0) - 273.15).ToString(" +
            "[Globalization.CultureInfo]::InvariantCulture) }";
        var result = await _processRunner.RunAsync(
            new ProcessExecutionRequest(
                "powershell.exe",
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
                ProbeTimeout),
            token).ConfigureAwait(false);
        if (!result.Succeeded)
            return null;

        double? maximum = null;
        foreach (var line in result.StandardOutput.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseNumber(line.Trim(), out var temperature) &&
                temperature is > 0 and <= 150)
            {
                maximum = MaxNullable(maximum, temperature);
            }
        }
        return maximum is null ? null : Math.Round(maximum.Value, 1);
    }

    public async Task<BatteryHealthTelemetry> QueryBatteryHealthAsync(
        CancellationToken token)
    {
        var reportPath = Path.Combine(
            Path.GetTempPath(),
            $"PowerMode-battery-{Guid.NewGuid():N}.xml");
        try
        {
            var result = await _processRunner.RunAsync(
                new ProcessExecutionRequest(
                    "powercfg.exe",
                    ["/batteryreport", "/xml", "/output", reportPath],
                    ProbeTimeout),
                token).ConfigureAwait(false);
            return result.Succeeded && File.Exists(reportPath)
                ? ParseBatteryHealth(reportPath)
                : BatteryHealthTelemetry.Empty;
        }
        finally
        {
            try
            {
                File.Delete(reportPath);
            }
            catch
            {
                // A stale temporary report is harmless and can be reclaimed by Windows.
            }
        }
    }

    public async Task<string> GenerateBatteryReportAsync(
        string outputPath,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var result = await _processRunner.RunAsync(
            new ProcessExecutionRequest(
                "powercfg.exe",
                ["/batteryreport", "/output", fullPath],
                BatteryReportTimeout),
            token).ConfigureAwait(false);
        if (result.Cancelled && token.IsCancellationRequested)
            throw new OperationCanceledException(token);
        if (result.TimedOut)
            throw new TimeoutException("Windows battery report generation timed out.");
        if (!result.Succeeded || !File.Exists(fullPath))
        {
            var diagnostic = result.StartError ?? result.StandardError.Trim();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(diagnostic)
                    ? "Windows could not generate a battery report."
                    : diagnostic);
        }
        return fullPath;
    }

    private static BatteryHealthTelemetry ParseBatteryHealth(string reportPath)
    {
        try
        {
            var document = XDocument.Load(reportPath, LoadOptions.None);
            var batteries = document.Descendants()
                .Where(element => element.Name.LocalName == "Battery")
                .ToArray();
            if (batteries.Length == 0)
                return BatteryHealthTelemetry.Empty;

            long designTotal = 0;
            long fullChargeTotal = 0;
            var hasDesign = false;
            var hasFullCharge = false;
            int? maximumCycleCount = null;
            foreach (var battery in batteries)
            {
                var design = ParseLongElement(battery, "DesignCapacity");
                var fullCharge = ParseLongElement(battery, "FullChargeCapacity");
                var cycleCount = ParseIntElement(battery, "CycleCount");
                if (design is > 0)
                {
                    designTotal += design.Value;
                    hasDesign = true;
                }
                if (fullCharge is > 0)
                {
                    fullChargeTotal += fullCharge.Value;
                    hasFullCharge = true;
                }
                if (cycleCount is >= 0)
                    maximumCycleCount = Math.Max(maximumCycleCount ?? 0, cycleCount.Value);
            }

            long? designCapacity = hasDesign ? designTotal : null;
            long? fullChargeCapacity = hasFullCharge ? fullChargeTotal : null;
            double? health = designCapacity is > 0 && fullChargeCapacity is not null
                ? Math.Round(fullChargeCapacity.Value * 100d / designCapacity.Value, 1)
                : null;
            return new BatteryHealthTelemetry(
                designCapacity,
                fullChargeCapacity,
                health,
                maximumCycleCount);
        }
        catch
        {
            return BatteryHealthTelemetry.Empty;
        }
    }

    private static long? ParseLongElement(XElement parent, string name) =>
        long.TryParse(
            parent.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private static int? ParseIntElement(XElement parent, string name) =>
        int.TryParse(
            parent.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private static bool TryParseNumber(string value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
        double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number);

    private static double? MaxNullable(double? first, double? second) =>
        first is null ? second : second is null ? first : Math.Max(first.Value, second.Value);
}
