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
}
