using BuildTimeAnalyzer.Commands;

if (args is ["-h" or "--help"] or [])
{
    PrintHelp();
    return 0;
}

if (args is ["-v" or "--version"])
{
    Console.WriteLine(BuildTimeAnalyzer.VersionInfo.Version);
    return 0;
}

try
{
    switch (args[0])
    {
        case "build":
            return await BuildCommand.RunAsync(args[1..]);
        case "analyze":
            return await AnalyzeCommand.RunAsync(args[1..]);
        case "init":
            return await ConfigCommand.RunInitAsync(args[1..]);
        case "config":
            return await ConfigCommand.RunPrintAsync(args[1..]);
        default:
            Console.Error.WriteLine($"Unknown command: {args[0]}");
            Console.Error.WriteLine("Run 'btanalyzer --help' for usage.");
            return 1;
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex)
{
    // Last-resort boundary: turn any unhandled error into a clean message + exit code instead of a
    // raw crash. (AOT build has stack traces disabled, so the message is what the user would see.)
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine($"btanalyzer {BuildTimeAnalyzer.VersionInfo.Version}");
    Console.WriteLine("CLI tool that analyzes MSBuild binary logs to identify build performance bottlenecks.");
    Console.WriteLine();
    Console.WriteLine("USAGE:");
    Console.WriteLine("    btanalyzer <COMMAND> [OPTIONS]");
    Console.WriteLine();
    Console.WriteLine("COMMANDS:");
    Console.WriteLine("    build      Build and analyze a project or solution");
    Console.WriteLine("    analyze    Analyze an existing .binlog without building");
    Console.WriteLine("    init       Write a default btanalyzer.json");
    Console.WriteLine("    config     Print the effective configuration");
    Console.WriteLine();
    Console.WriteLine("OPTIONS:");
    Console.WriteLine("    -h, --help       Print help");
    Console.WriteLine("    -v, --version    Print version");
    Console.WriteLine();
    Console.WriteLine("EXAMPLES:");
    Console.WriteLine("    btanalyzer build");
    Console.WriteLine("    btanalyzer build . -c Release -n 10");
    Console.WriteLine("    btanalyzer build MyApp.sln -o report.html");
    Console.WriteLine("    btanalyzer analyze build.binlog -o report.md");
    Console.WriteLine("    btanalyzer build --compare baseline.json --fail-on regression:10");
}
