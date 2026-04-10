using BuildTimeAnalyzer.Export;
using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Rendering;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Commands;

public static class BuildCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var settings = ParseArgs(args);
        if (settings is null) return 1;

        var projectPath = settings.ProjectPath is { Length: > 0 }
            ? Path.GetFullPath(settings.ProjectPath)
            : Directory.GetCurrentDirectory();

        ConsoleReportRenderer.WriteHeader("MSBuild Timing Analyzer");

        // 1. Run dotnet build
        var runner = new BuildRunner();
        var extra = settings.ExtraArgs?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    ?? Array.Empty<string>();

        var (exitCode, binLogPath) = await runner.RunAsync(projectPath, settings.Configuration, settings.Incremental, extra, CancellationToken.None);

        // 2. Analyze the binary log
        Console.WriteLine("Analyzing binary log...");
        var analyzer = new LogAnalyzer(settings.TopN);
        var report = await analyzer.AnalyzeAsync(binLogPath, projectPath);

        if (report is not { } finalReport)
        {
            Console.Error.WriteLine("Failed to produce a report.");
            return exitCode;
        }

        // Overlay the build mode — the runner knows how we invoked dotnet build, the analyzer doesn't
        finalReport = finalReport with
        {
            Context = finalReport.Context with
            {
                BuildMode = settings.Incremental ? "incremental" : "full (--no-incremental)",
            },
        };

        // 3. Run analysis first so the renderer can place findings near the top
        var analysis = BuildAnalyzer.Analyze(finalReport);

        // 4. Render into a buffer, then display via pager (falls back to plain output when
        //    stdout is redirected or the content fits on one screen).
        Console.WriteLine();
        var usePager = !settings.NoPager;
        if (usePager)
        {
            var buffer = new StringWriter();
            var previousOut = Console.Out;
            Console.SetOut(buffer);
            try
            {
                ConsoleReportRenderer.WriteHeader("MSBuild Timing Analyzer");
                ConsoleReportRenderer.Render(finalReport, analysis, settings.TopN);
            }
            finally
            {
                Console.SetOut(previousOut);
            }
            var lines = buffer.ToString().TrimEnd('\r', '\n').Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            ConsolePager.Display(lines);
        }
        else
        {
            ConsoleReportRenderer.WriteHeader("MSBuild Timing Analyzer");
            ConsoleReportRenderer.Render(finalReport, analysis, settings.TopN);
        }

        // 4. Optional export
        if (settings.OutputPath is { Length: > 0 })
        {
            var ext = Path.GetExtension(settings.OutputPath).ToLowerInvariant();
            switch (ext)
            {
                case ".html":
                    HtmlReportExporter.Export(finalReport, settings.OutputPath, settings.TopN, analysis);
                    Console.WriteLine($"HTML report saved to {Path.GetFullPath(settings.OutputPath)}");
                    break;

                case ".json":
                    JsonReportExporter.Export(finalReport, settings.OutputPath, analysis);
                    Console.WriteLine($"JSON report saved to {Path.GetFullPath(settings.OutputPath)}");
                    break;

                default:
                    Console.Error.WriteLine($"Unknown export format '{ext}'. Supported: .html, .json");
                    break;
            }
        }

        // 5. Cleanup
        if (!settings.KeepLog && File.Exists(binLogPath))
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.Delete(binLogPath);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await Task.Delay(200);
                }
            }
        }
        else if (settings.KeepLog)
        {
            Console.WriteLine($"Binary log kept at: {binLogPath}");
        }

        return exitCode;
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

                case "--keep-log":
                    settings.KeepLog = true;
                    break;

                case "--incremental":
                    settings.Incremental = true;
                    break;

                case "--no-pager":
                    settings.NoPager = true;
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
        Console.WriteLine("    -n, --top <N>                   Number of top results (default: 20)");
        Console.WriteLine("    -o, --output <PATH>             Export report (.html or .json)");
        Console.WriteLine("    --incremental                   Allow incremental build (default: --no-incremental for reproducibility)");
        Console.WriteLine("    --no-pager                      Disable the interactive pager, stream output directly");
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
    public bool NoPager { get; set; } = false;
    public string? OutputPath { get; set; }
    public string? ExtraArgs { get; set; }
}
