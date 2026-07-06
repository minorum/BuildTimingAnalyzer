using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Commands;

/// <summary>
/// Analyzes a pre-existing MSBuild binary log without running a build. Reuses the same reporting
/// pipeline as <see cref="BuildCommand"/>, so CI-produced <c>.binlog</c> artifacts can be analyzed
/// after the fact. Never deletes the input file — it belongs to the user.
/// </summary>
public static class AnalyzeCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var settings = ParseArgs(args);
        if (settings is null) return 1;

        if (settings.BinLogPath is null or { Length: 0 })
        {
            Console.Error.WriteLine("Missing required argument: <file.binlog>");
            Console.Error.WriteLine("Run 'btanalyzer analyze --help' for usage.");
            return 1;
        }
        if (settings.TopN < 1)
        {
            Console.Error.WriteLine("--top must be a positive integer.");
            return 1;
        }

        var binLogPath = Path.GetFullPath(settings.BinLogPath);
        if (!File.Exists(binLogPath))
        {
            Console.Error.WriteLine($"Binary log not found: {binLogPath}");
            return 1;
        }

        Console.WriteLine($"btanalyzer {BuildTimeAnalyzer.VersionInfo.Version}");
        Console.WriteLine();

        var startDir = Path.GetDirectoryName(binLogPath) ?? Directory.GetCurrentDirectory();
        var config = BtaConfig.Load(settings.ConfigPath, startDir);

        return await ReportPipeline.RunAsync(new ReportPipeline.Options
        {
            BinLogPath = binLogPath,
            ProjectOrSolutionPath = binLogPath,
            TopN = settings.TopN,
            Config = config,
            BuildMode = null,
            OutputPath = settings.OutputPath,
            NoOpen = settings.NoOpen,
            ComparePath = settings.ComparePath,
            HistoryPath = settings.HistoryPath,
            FailOn = settings.FailOn,
            BuildExitCode = 0,
            DeleteBinLogWhenDone = false,
        });
    }

    private static AnalyzeCommandSettings? ParseArgs(string[] args)
    {
        var settings = new AnalyzeCommandSettings();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    PrintHelp();
                    return null;

                case "-n" or "--top":
                    if (++i >= args.Length) { Console.Error.WriteLine("Missing value for --top"); return null; }
                    if (!int.TryParse(args[i], out var n)) { Console.Error.WriteLine($"Invalid number: {args[i]}"); return null; }
                    settings.TopN = n;
                    break;

                case "-o" or "--output":
                    if (++i >= args.Length) { Console.Error.WriteLine("Missing value for --output"); return null; }
                    settings.OutputPath = args[i];
                    break;

                case "--config":
                    if (++i >= args.Length) { Console.Error.WriteLine("Missing value for --config"); return null; }
                    settings.ConfigPath = args[i];
                    break;

                case "--compare":
                    if (++i >= args.Length) { Console.Error.WriteLine("Missing value for --compare"); return null; }
                    settings.ComparePath = args[i];
                    break;

                case "--fail-on":
                    if (++i >= args.Length) { Console.Error.WriteLine("Missing value for --fail-on"); return null; }
                    settings.FailOn = args[i];
                    break;

                case "--history":
                    if (++i >= args.Length) { Console.Error.WriteLine("Missing value for --history"); return null; }
                    settings.HistoryPath = args[i];
                    break;

                case "--no-open":
                    settings.NoOpen = true;
                    break;

                default:
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Unknown option: {args[i]}");
                        return null;
                    }
                    settings.BinLogPath = args[i];
                    break;
            }
        }

        return settings;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("USAGE:");
        Console.WriteLine("    btanalyzer analyze <file.binlog> [OPTIONS]");
        Console.WriteLine();
        Console.WriteLine("ARGUMENTS:");
        Console.WriteLine("    <file.binlog>    Path to an existing MSBuild binary log");
        Console.WriteLine();
        Console.WriteLine("OPTIONS:");
        Console.WriteLine("    -n, --top <N>                   Number of top results in the report (default: 20)");
        Console.WriteLine("    -o, --output <PATH>             Output file path (.html, .json, or .md). Default: temp HTML file");
        Console.WriteLine("    --config <PATH>                 Path to a btanalyzer.json (default: discovered near the log)");
        Console.WriteLine("    --compare <BASELINE.json>       Compare against a previously exported JSON report");
        Console.WriteLine("    --fail-on <SPEC>                Exit non-zero on: critical | warning | errors |");
        Console.WriteLine("                                    wallclock:<seconds> | regression:<percent> (comma-separated)");
        Console.WriteLine("    --history <FILE.jsonl>          Append a one-line run summary for trend tracking");
        Console.WriteLine("    --no-open                       Do not launch the default browser after generating the HTML report");
        Console.WriteLine("    -h, --help                      Print help");
    }
}

internal sealed class AnalyzeCommandSettings
{
    public string? BinLogPath { get; set; }
    public int TopN { get; set; } = 20;
    public bool NoOpen { get; set; }
    public string? OutputPath { get; set; }
    public string? ConfigPath { get; set; }
    public string? ComparePath { get; set; }
    public string? FailOn { get; set; }
    public string? HistoryPath { get; set; }
}
