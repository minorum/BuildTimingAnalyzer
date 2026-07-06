using BuildTimeAnalyzer.Rendering;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Commands;

public static class BuildCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var settings = ParseArgs(args);
        if (settings is null) return 1;

        if (settings.TopN < 1)
        {
            Console.Error.WriteLine("--top must be a positive integer.");
            return 1;
        }

        var projectPath = settings.ProjectPath is { Length: > 0 }
            ? Path.GetFullPath(settings.ProjectPath)
            : Directory.GetCurrentDirectory();

        Console.WriteLine($"btanalyzer {BuildTimeAnalyzer.VersionInfo.Version}");
        Console.WriteLine();

        var config = BtaConfig.Load(settings.ConfigPath, projectPath);

        // Quote-aware split so an extra arg containing spaces (e.g. -p:DefineConstants="A B") is
        // preserved as one token rather than shattered on every space.
        var extra = CommandLineArgs.Split(settings.ExtraArgs);

        // Caller owns the binlog path so it can always be cleaned up — even if the build is
        // cancelled before BuildRunner returns.
        var binLogPath = Path.Combine(Path.GetTempPath(), $"build-{Guid.NewGuid():N}.binlog");

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += onCancel;

        // ── 1. Run dotnet build with binary logging ─────────────────
        var runner = new BuildRunner();
        var controller = new BuildOutputController();
        var throbber = new Throbber("Running build with binary logging (Ctrl+E to toggle build output)");
        controller.Toggled += isOn =>
        {
            if (isOn)
            {
                throbber.Pause();
                Console.WriteLine();
                Console.WriteLine("── build output (Ctrl+E to hide) ──");
            }
            else
            {
                Console.WriteLine("── build output hidden (Ctrl+E to show) ──");
                throbber.Resume();
            }
        };
        controller.StartListening();

        BuildRunResult buildResult;
        try
        {
            buildResult = await runner.RunAsync(
                projectPath, binLogPath, settings.Configuration, settings.Incremental,
                controller, extra, cts.Token);
        }
        catch (OperationCanceledException)
        {
            await controller.StopAsync();
            await throbber.StopAsync("cancelled");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Build cancelled. Cleaning up…");
            TryDeleteQuietly(binLogPath);
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            await controller.StopAsync();
            await throbber.StopAsync();
        }

        var exitCode = buildResult.ExitCode;

        if (exitCode != 0 && buildResult.CapturedOutputTail.Count > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Build failed (exit {exitCode}). Last {buildResult.CapturedOutputTail.Count} line(s) of build output:");
            foreach (var line in buildResult.CapturedOutputTail)
                Console.Error.WriteLine($"  {line}");
        }

        // ── 2. Parse, analyze, export, gate (shared pipeline) ───────
        var pipelineExit = await ReportPipeline.RunAsync(new ReportPipeline.Options
        {
            BinLogPath = binLogPath,
            ProjectOrSolutionPath = projectPath,
            TopN = settings.TopN,
            Config = config,
            BuildMode = settings.Incremental ? "incremental" : "full (--no-incremental)",
            OutputPath = settings.OutputPath,
            NoOpen = settings.NoOpen,
            ComparePath = settings.ComparePath,
            HistoryPath = settings.HistoryPath,
            FailOn = settings.FailOn,
            BuildExitCode = exitCode,
            DeleteBinLogWhenDone = !settings.KeepLog,
        });

        if (settings.KeepLog && File.Exists(binLogPath))
        {
            Console.WriteLine();
            Console.WriteLine($"Binary log kept at: {binLogPath}");
        }

        return pipelineExit;
    }

    private static void TryDeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    private static BuildCommandSettings? ParseArgs(string[] args)
    {
        var settings = new BuildCommandSettings();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    PrintHelp();
                    return null;

                case "-c" or "--configuration":
                    if (++i >= args.Length) { Console.Error.WriteLine("Missing value for --configuration"); return null; }
                    settings.Configuration = args[i];
                    break;

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

                case "--keep-log":
                    settings.KeepLog = true;
                    break;

                case "--incremental":
                    settings.Incremental = true;
                    break;

                case "--no-open":
                    settings.NoOpen = true;
                    break;

                case "--args":
                    if (++i >= args.Length) { Console.Error.WriteLine("Missing value for --args"); return null; }
                    settings.ExtraArgs = args[i];
                    break;

                default:
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Unknown option: {args[i]}");
                        return null;
                    }
                    settings.ProjectPath = args[i];
                    break;
            }
        }

        return settings;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("USAGE:");
        Console.WriteLine("    btanalyzer build [project] [OPTIONS]");
        Console.WriteLine();
        Console.WriteLine("ARGUMENTS:");
        Console.WriteLine("    [project]    Path to project or solution (default: current directory)");
        Console.WriteLine();
        Console.WriteLine("OPTIONS:");
        Console.WriteLine("    -c, --configuration <CONFIG>    Build configuration (default: Debug)");
        Console.WriteLine("    -n, --top <N>                   Number of top results in the report (default: 20)");
        Console.WriteLine("    -o, --output <PATH>             Output file path (.html, .json, or .md). Default: temp HTML file");
        Console.WriteLine("    --config <PATH>                 Path to a btanalyzer.json (default: discovered near the project)");
        Console.WriteLine("    --compare <BASELINE.json>       Compare against a previously exported JSON report");
        Console.WriteLine("    --fail-on <SPEC>                Exit non-zero on: critical | warning | errors |");
        Console.WriteLine("                                    wallclock:<seconds> | regression:<percent> (comma-separated)");
        Console.WriteLine("    --history <FILE.jsonl>          Append a one-line run summary for trend tracking");
        Console.WriteLine("    --no-open                       Do not launch the default browser after generating the HTML report");
        Console.WriteLine("    --incremental                   Allow incremental build (default: --no-incremental for reproducibility)");
        Console.WriteLine("    --keep-log                      Keep the .binlog file after analysis");
        Console.WriteLine("    --args <ARGS>                   Additional arguments for dotnet build");
        Console.WriteLine("    -h, --help                      Print help");
    }
}

internal sealed class BuildCommandSettings
{
    public string? ProjectPath { get; set; }
    public string Configuration { get; set; } = "Debug";
    public int TopN { get; set; } = 20;
    public bool KeepLog { get; set; }
    public bool Incremental { get; set; } = false;
    public bool NoOpen { get; set; } = false;
    public string? OutputPath { get; set; }
    public string? ExtraArgs { get; set; }
    public string? ConfigPath { get; set; }
    public string? ComparePath { get; set; }
    public string? FailOn { get; set; }
    public string? HistoryPath { get; set; }
}
