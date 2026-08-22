using System.Text.Json;
using System.Text.Json.Nodes;

namespace PowerMode.TestHost;

public static class Marker
{
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            return 64;
        }

        if (args[0].StartsWith("/", StringComparison.Ordinal))
        {
            return RunFakePowerCfg(args);
        }

        switch (args[0])
        {
            case "echo":
                Console.Out.Write(args.ElementAtOrDefault(1) ?? string.Empty);
                Console.Error.Write(args.ElementAtOrDefault(2) ?? string.Empty);
                return 0;

            case "exit":
                return int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);

            case "sleep":
                await Task.Delay(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));
                return 0;

            case "spawn-child":
                using (var child = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false,
                        ArgumentList = { "sleep", args[1] }
                    })!)
                {
                    Console.Out.WriteLine(child.Id);
                    await child.WaitForExitAsync();
                }

                return 0;

            default:
                return 65;
        }
    }

    private static int RunFakePowerCfg(string[] args)
    {
        var statePath = Environment.GetEnvironmentVariable(
            "POWERMODE_TEST_POWER_STATE");
        if (string.IsNullOrWhiteSpace(statePath))
        {
            Console.Error.WriteLine("POWERMODE_TEST_POWER_STATE is required.");
            return 2;
        }

        var state = JsonNode.Parse(File.ReadAllText(statePath))?.AsObject()
            ?? throw new InvalidDataException("Fake power state is invalid.");
        var values = state["values"]?.AsObject()
            ?? throw new InvalidDataException("Fake power values are missing.");
        var command = args[0].ToLowerInvariant();
        var failSetting = Environment.GetEnvironmentVariable(
            "POWERMODE_TEST_FAIL_SETTING");
        var setting = args.Length > 3 ? args[3] : command;
        if (!string.IsNullOrWhiteSpace(failSetting) &&
            (string.Equals(failSetting, setting, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(failSetting, command, StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"simulated failure for {setting}");
            return 1;
        }

        switch (command)
        {
            case "/getactivescheme":
                Console.WriteLine($"Power Scheme GUID: {state["activeSchemeId"]}");
                return 0;

            case "/setactive":
                state["activeSchemeId"] = args[1].ToLowerInvariant();
                SaveState(statePath, state);
                return 0;

            case "/query":
                var guid = args[1].ToLowerInvariant();
                var subgroup = args[2];
                var settingName = args[3];
                var ac = GetValue(values, Key(guid, subgroup, settingName, "ac"));
                var dc = GetValue(values, Key(guid, subgroup, settingName, "dc"));
                Console.WriteLine($"Power Scheme GUID: {guid}");
                Console.WriteLine($"  Subgroup GUID: {subgroup}");
                Console.WriteLine($"    Power Setting GUID: {settingName}");
                Console.WriteLine($"      Current AC Power Setting Index: 0x{ac:X}");
                Console.WriteLine($"      Current DC Power Setting Index: 0x{dc:X}");
                return 0;

            case "/setacvalueindex":
                SetValue(values, args[1], args[2], args[3], "ac", args[4]);
                SaveState(statePath, state);
                return 0;

            case "/setdcvalueindex":
                SetValue(values, args[1], args[2], args[3], "dc", args[4]);
                SaveState(statePath, state);
                return 0;

            case "/change":
            case "/hibernate":
                return 0;

            default:
                Console.Error.WriteLine($"unsupported fake powercfg command: {command}");
                return 2;
        }
    }

    private static int GetValue(
        JsonObject values,
        string key) =>
        values[key]?.GetValue<int>() ?? 0;

    private static void SetValue(
        JsonObject values,
        string guid,
        string subgroup,
        string setting,
        string powerSource,
        string value)
    {
        values[Key(guid, subgroup, setting, powerSource)] =
            int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Key(
        string guid,
        string subgroup,
        string setting,
        string powerSource) =>
        $"{guid.ToLowerInvariant()}|{subgroup.ToLowerInvariant()}|{setting.ToLowerInvariant()}|{powerSource}";

    private static void SaveState(string path, JsonObject state)
    {
        File.WriteAllText(
            path,
            state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
