using System.Diagnostics;
using System.Runtime.InteropServices;
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

        Console.WriteLine($"btanalyzer {BuildTimeAnalyzer.VersionInfo.Version}");
        Console.WriteLine();

        // ── 1. Run dotnet build with binary logging ─────────────────
        var runner = new BuildRunner();
        var extra = settings.ExtraArgs?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    ?? Array.Empty<string>();

        BuildRunResult buildResult;
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
        try
        {
            buildResult = await runner.RunAsync(
                projectPath, settings.Configuration, settings.Incremental,
                controller, extra, CancellationToken.None);
        }
        finally
        {
            await controller.StopAsync();
            await throbber.StopAsync();
        }

        var exitCode = buildResult.ExitCode;
        var binLogPath = buildResult.BinLogPath;

        if (exitCode != 0 && buildResult.CapturedOutputTail.Count > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Build failed (exit {exitCode}). Last {buildResult.CapturedOutputTail.Count} line(s) of build output:");
            foreach (var line in buildResult.CapturedOutputTail)
                Console.Error.WriteLine($"  {line}");
        }

        // ── 2. Parse the binary log ────────────────────────────────
        BuildReport? report;
        await using (var t = new Throbber("Parsing binary log"))
        {
            var analyzer = new LogAnalyzer(settings.TopN);
            report = await analyzer.AnalyzeAsync(binLogPath, projectPath);
        }

        if (report is not { } finalReport)
        {
            Console.Error.WriteLine("Failed to produce a report.");
            return exitCode;
        }

        finalReport = finalReport with
        {
            Context = finalReport.Context with
            {
                BuildMode = settings.Incremental ? "incremental" : "full (--no-incremental)",
            },
        };

        // ── 3. Run automated analysis ──────────────────────────────
        BuildAnalysis analysis;
        await using (var t = new Throbber($"Analysing {finalReport.Projects.Count} project(s)"))
        {
            analysis = BuildAnalyzer.Analyze(finalReport);
        }

        var findingCount = analysis.Findings.Count;
        var criticalCount = analysis.Findings.Count(f => f.Severity == FindingSeverity.Critical);
        if (findingCount > 0)
        {
            var tag = criticalCount > 0 ? $"{findingCount} finding(s), {criticalCount} critical" : $"{findingCount} finding(s)";
            Console.WriteLine($"    {tag}");
        }

        // ── 4. Decide output format and path ───────────────────────
        var (outputPath, outputFormat) = ResolveOutputPath(settings);

        await using (var t = new Throbber($"Generating {outputFormat.ToUpperInvariant()} report"))
        {
            switch (outputFormat)
            {
                case "html":
                    HtmlReportExporter.Export(finalReport, outputPath, settings.TopN, analysis);
                    break;
                case "json":
                    JsonReportExporter.Export(finalReport, outputPath, analysis);
                    break;
            }
        }

        Console.WriteLine($"    Saved to: {outputPath}");

        // ── 5. Open in browser (HTML only, opt-out + environment-aware) ─
        if (outputFormat == "html" && ShouldOpenBrowser(settings))
        {
            if (!TryOpenInBrowser(outputPath))
                Console.WriteLine("    (Could not launch browser automatically. Open the file above manually.)");
            else
                Console.WriteLine("    Opened in default browser");
        }
        else if (outputFormat == "html")
        {
            Console.WriteLine("    (Browser launch skipped. Open the file manually or rerun without --no-open.)");
        }

        // ── 6. Summary line with top finding ────────────────────────
        PrintSummaryLine(finalReport, analysis);

        // ── 7. Cleanup binlog ──────────────────────────────────────
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
            Console.WriteLine();
            Console.WriteLine($"Binary log kept at: {binLogPath}");
        }

        return exitCode;
    }

    private static (string path, string format) ResolveOutputPath(BuildCommandSettings settings)
    {
        if (settings.OutputPath is { Length: > 0 })
        {
            var explicitPath = Path.GetFullPath(settings.OutputPath);
            var ext = Path.GetExtension(explicitPath).ToLowerInvariant();
            var format = ext switch
            {
                ".json" => "json",
                ".html" or ".htm" => "html",
                _ => "html",
            };
            return (explicitPath, format);
        }

        // Default: temp HTML file with timestamp. Predictable, easy to clean up manually.
        var name = $"btanalyzer-{DateTime.Now:yyyyMMdd-HHmmss}.html";
        return (Path.Combine(Path.GetTempPath(), name), "html");
    }

    private static bool ShouldOpenBrowser(BuildCommandSettings settings)
    {
        if (settings.NoOpen) return false;
        if (Console.IsOutputRedirected) return false;
        // Common CI environment variable set by GitHub Actions, GitLab CI, CircleCI, Azure DevOps, etc.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))) return false;
        // Headless Linux (no X11)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return false;
        }
        return true;
    }

    private static bool TryOpenInBrowser(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
                return true;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", path);
                return true;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", path);
                return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private static void PrintSummaryLine(BuildReport report, BuildAnalysis analysis)
    {
        Console.WriteLine();
        var status = report.Succeeded ? "OK" : "FAILED";
        Console.Write($"Build {status} in {Fmt(report.TotalDuration)}");
        if (report.WarningCount > 0) Console.Write($" | {report.WarningCount} warning(s)");
        if (report.ErrorCount > 0) Console.Write($" | {report.ErrorCount} error(s)");
        Console.WriteLine();

        var topFinding = analysis.Findings
            .OrderBy(f => f.Severity switch
            {
                FindingSeverity.Critical => 0,
                FindingSeverity.Warning => 1,
                _ => 2,
            })
            .FirstOrDefault();
        if (topFinding is not null)
        {
            var label = topFinding.Severity switch
            {
                FindingSeverity.Critical => "CRITICAL",
                FindingSeverity.Warning => "WARNING",
                _ => "INFO",
            };
            Console.WriteLine($"Top finding [{label}]: {topFinding.Title}");
        }
    }

    private static string Fmt(TimeSpan ts) => Rendering.ConsoleReportRenderer.FormatDuration(ts);

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
        Console.WriteLine("    -o, --output <PATH>             Output file path (.html or .json). Default: temp HTML file");
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
}
