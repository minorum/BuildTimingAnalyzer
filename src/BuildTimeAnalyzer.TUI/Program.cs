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

if (args is not ["build", ..])
{
    Console.Error.WriteLine($"Unknown command: {args[0]}");
    Console.Error.WriteLine("Run 'btanalyzer --help' for usage.");
    return 1;
}

return await BuildCommand.RunAsync(args[1..]);

static void PrintHelp()
{
    Console.WriteLine($"btanalyzer {BuildTimeAnalyzer.VersionInfo.Version}");
    Console.WriteLine("CLI tool that analyzes MSBuild binary logs to identify build performance bottlenecks.");
    Console.WriteLine();
    Console.WriteLine("USAGE:");
    Console.WriteLine("    btanalyzer <COMMAND> [OPTIONS]");
    Console.WriteLine();
    Console.WriteLine("COMMANDS:");
    Console.WriteLine("    build    Build and analyze a project or solution");
    Console.WriteLine();
    Console.WriteLine("OPTIONS:");
    Console.WriteLine("    -h, --help       Print help");
    Console.WriteLine("    -v, --version    Print version");
    Console.WriteLine();
    Console.WriteLine("EXAMPLES:");
    Console.WriteLine("    btanalyzer build");
    Console.WriteLine("    btanalyzer build . -c Release -n 10");
    Console.WriteLine("    btanalyzer build MyApp.sln -o report.html");
}
