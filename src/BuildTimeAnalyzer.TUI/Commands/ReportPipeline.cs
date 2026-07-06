using System.Diagnostics;
using System.Runtime.InteropServices;
using BuildTimeAnalyzer.Export;
using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Rendering;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Commands;

/// <summary>
/// Shared post-binlog stages (parse → analyze → compare → export → open → gate → cleanup) used by
/// both <see cref="BuildCommand"/> and <see cref="AnalyzeCommand"/>. Keeping this in one place means
/// the two entry points can never drift, and every external interaction is wrapped so a failure
/// produces a friendly message and exit code instead of a stack-trace crash.
/// </summary>
internal static class ReportPipeline
{
    internal sealed record Options
    {
        public required string BinLogPath { get; init; }
        public required string ProjectOrSolutionPath { get; init; }
        public required int TopN { get; init; }
        public required BtaConfig Config { get; init; }
        public string? BuildMode { get; init; }
        public string? OutputPath { get; init; }
        public bool NoOpen { get; init; }
        public string? ComparePath { get; init; }
        public string? HistoryPath { get; init; }
        public string? FailOn { get; init; }
        public int BuildExitCode { get; init; }
        public bool DeleteBinLogWhenDone { get; init; }
    }

    public static async Task<int> RunAsync(Options opts)
    {
        // Configure the heavy-package set from config before any parsing (package resolution runs
        // deep inside LogAnalyzer, so this must happen first).
        if (opts.Config.HeavyPackages.Count > 0)
            ProjectPackageResolver.ConfigureHeavyPackages(opts.Config.HeavyPackages);

        // ── Guard the binary log ────────────────────────────────────
        if (!File.Exists(opts.BinLogPath))
        {
            if (opts.BuildExitCode != 0)
                Console.Error.WriteLine($"Build failed (exit {opts.BuildExitCode}) and produced no binary log; nothing to analyze.");
            else
                Console.Error.WriteLine($"No binary log found at: {opts.BinLogPath}");
            return opts.BuildExitCode != 0 ? opts.BuildExitCode : 1;
        }
        if (new FileInfo(opts.BinLogPath).Length == 0)
        {
            Console.Error.WriteLine($"Binary log is empty: {opts.BinLogPath}");
            if (opts.DeleteBinLogWhenDone) await TryDeleteBinLog(opts.BinLogPath);
            return opts.BuildExitCode != 0 ? opts.BuildExitCode : 1;
        }

        // ── Parse ───────────────────────────────────────────────────
        var parseThrobber = new Throbber("Parsing binary log");
        BuildReport report;
        try
        {
            var analyzer = new LogAnalyzer(opts.TopN);
            report = await analyzer.AnalyzeAsync(opts.BinLogPath, opts.ProjectOrSolutionPath);
        }
        catch (Exception ex)
        {
            await parseThrobber.StopAsync("failed");
            Console.Error.WriteLine($"Failed to parse binary log: {ex.Message}");
            if (opts.DeleteBinLogWhenDone) await TryDeleteBinLog(opts.BinLogPath);
            return opts.BuildExitCode != 0 ? opts.BuildExitCode : 1;
        }
        await parseThrobber.StopAsync();

        if (opts.BuildMode is not null)
        {
            report = report with { Context = report.Context with { BuildMode = opts.BuildMode } };
        }

        // ── Automated analysis ──────────────────────────────────────
        BuildAnalysis analysis;
        await using (new Throbber($"Analysing {report.Projects.Count} project(s)"))
        {
            analysis = BuildAnalyzer.Analyze(report, opts.Config.Thresholds);
        }

        var criticalCount = analysis.Findings.Count(f => f.Severity == FindingSeverity.Critical);
        if (analysis.Findings.Count > 0)
        {
            var tag = criticalCount > 0
                ? $"{analysis.Findings.Count} finding(s), {criticalCount} critical"
                : $"{analysis.Findings.Count} finding(s)";
            Console.WriteLine($"    {tag}");
        }

        // ── Comparison against a baseline JSON report ───────────────
        BuildComparisonResult? comparison = null;
        if (opts.ComparePath is { Length: > 0 })
        {
            var baseline = BuildSnapshot.ReadFromJsonReport(opts.ComparePath);
            if (baseline is null)
                Console.Error.WriteLine($"    (Could not read comparison baseline: {opts.ComparePath})");
            else
            {
                comparison = BuildComparison.Compare(baseline, BuildSnapshot.FromReport(report));
                PrintComparison(comparison);
            }
        }

        // ── Export ──────────────────────────────────────────────────
        var (outputPath, outputFormat) = ResolveOutputPath(opts.OutputPath);
        var exportFailed = false;
        await using (new Throbber($"Generating {outputFormat.ToUpperInvariant()} report"))
        {
            try
            {
                switch (outputFormat)
                {
                    case "html": HtmlReportExporter.Export(report, outputPath, analysis); break;
                    case "json": JsonReportExporter.Export(report, outputPath, analysis); break;
                    case "md": MarkdownReportExporter.Export(report, outputPath, analysis, comparison); break;
                }
            }
            catch (Exception ex)
            {
                exportFailed = true;
                Console.Error.WriteLine($"Failed to write report to {outputPath}: {ex.Message}");
            }
        }
        if (!exportFailed) Console.WriteLine($"    Saved to: {outputPath}");

        // ── History (best-effort) ───────────────────────────────────
        if (opts.HistoryPath is { Length: > 0 })
        {
            BuildHistory.Append(opts.HistoryPath, report, analysis, DateTime.UtcNow);
            Console.WriteLine($"    Appended run to history: {opts.HistoryPath}");
        }

        // ── Open in browser (HTML only) ─────────────────────────────
        if (!exportFailed && outputFormat == "html")
        {
            if (ShouldOpenBrowser(opts.NoOpen))
            {
                if (!TryOpenInBrowser(outputPath))
                    Console.WriteLine("    (Could not launch browser automatically. Open the file above manually.)");
                else
                    Console.WriteLine("    Opened in default browser");
            }
            else
            {
                Console.WriteLine("    (Browser launch skipped. Open the file manually or rerun without --no-open.)");
            }
        }

        // ── Summary line ────────────────────────────────────────────
        PrintSummaryLine(report, analysis);

        // ── CI gate ─────────────────────────────────────────────────
        var gate = FailOnPolicy.Evaluate(opts.FailOn, report, analysis, comparison);
        if (gate.Tripped)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("--fail-on tripped:");
            foreach (var reason in gate.Reasons)
                Console.Error.WriteLine($"  - {reason}");
        }

        // ── Cleanup ─────────────────────────────────────────────────
        if (opts.DeleteBinLogWhenDone)
            await TryDeleteBinLog(opts.BinLogPath);

        if (opts.BuildExitCode != 0) return opts.BuildExitCode;
        if (exportFailed) return 1;
        if (gate.Tripped) return 2;
        return 0;
    }

    private static void PrintComparison(BuildComparisonResult c)
    {
        Console.WriteLine();
        Console.WriteLine("Comparison vs baseline:");
        Console.WriteLine($"  Wall-clock:      {SignedMs(c.WallClockDeltaMs)} ({Signed(c.WallClockDeltaPercent)}%)");
        Console.WriteLine($"  Total self time: {SignedMs(c.TotalSelfTimeDeltaMs)} ({Signed(c.TotalSelfTimeDeltaPercent)}%)");
        Console.WriteLine($"  Warnings: {Signed(c.WarningDelta)} | Errors: {Signed(c.ErrorDelta)}");
        foreach (var d in c.Regressions.Take(5))
            Console.WriteLine($"  ↑ {d.Name}: {SignedMs(d.DeltaMs)} ({Signed(d.DeltaPercent)}%)");
    }

    // .md/.markdown → markdown, .json → json, everything else → html.
    private static (string path, string format) ResolveOutputPath(string? explicitOutput)
    {
        if (explicitOutput is { Length: > 0 })
        {
            var explicitPath = Path.GetFullPath(explicitOutput);
            var format = Path.GetExtension(explicitPath).ToLowerInvariant() switch
            {
                ".json" => "json",
                ".md" or ".markdown" => "md",
                _ => "html",
            };
            return (explicitPath, format);
        }

        var name = $"btanalyzer-{DateTime.Now:yyyyMMdd-HHmmss}.html";
        return (Path.Combine(Path.GetTempPath(), name), "html");
    }

    private static bool ShouldOpenBrowser(bool noOpen)
    {
        if (noOpen) return false;
        if (Console.IsOutputRedirected) return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))) return false;
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
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                return true;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) { Process.Start("open", path); return true; }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) { Process.Start("xdg-open", path); return true; }
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

    private static async Task TryDeleteBinLog(string binLogPath)
    {
        if (!File.Exists(binLogPath)) return;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Delete(binLogPath);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(200);
            }
            catch (UnauthorizedAccessException)
            {
                return; // can't delete — leave it, not worth crashing over
            }
        }
    }

    private static string Fmt(TimeSpan ts) => ConsoleReportRenderer.FormatDuration(ts);
    private static string FmtMs(long ms) => ConsoleReportRenderer.FormatDuration(TimeSpan.FromMilliseconds(Math.Abs(ms)));
    private static string SignedMs(long ms) => (ms >= 0 ? "+" : "-") + FmtMs(ms);
    private static string Signed(double v) => (v >= 0 ? "+" : "") + v.ToString("F1");
    private static string Signed(int v) => (v >= 0 ? "+" : "") + v.ToString();
}
