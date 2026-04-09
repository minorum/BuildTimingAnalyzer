using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Rendering;

public static class ConsoleReportRenderer
{
    public static void Render(BuildReport report, int topN)
    {
        RenderSummary(report);
        Console.WriteLine();
        RenderTimeline(report);
        Console.WriteLine();
        RenderProjectsTable(report, topN);
        Console.WriteLine();
        RenderTargetsTable(report);
    }

    public static void WriteHeader(string title)
    {
        var line = new string('-', Math.Max(0, 80 - title.Length - 3));
        Console.WriteLine($"-- {title} {line}");
        Console.WriteLine();
    }

    // ──────────────────────────── Summary ────────────────────────────

    private static void RenderSummary(BuildReport report)
    {
        var status = report.Succeeded ? "Build Succeeded" : "Build Failed";
        var project = Path.GetFileName(report.ProjectOrSolutionPath);

        Console.WriteLine($"  Status     {status}");
        Console.WriteLine($"  Project    {project}");
        Console.WriteLine($"  Started    {report.StartTime:HH:mm:ss}");
        Console.WriteLine($"  Duration   {FormatDuration(report.TotalDuration)}");
        Console.WriteLine($"  Errors     {report.ErrorCount}");
        Console.WriteLine($"  Warnings   {report.WarningCount}");
        Console.WriteLine($"  Projects   {report.Projects.Count}");
    }

    // ──────────────────────────── Projects ───────────────────────────

    private static void RenderProjectsTable(BuildReport report, int topN)
    {
        var projects = report.Projects.Take(topN).ToList();
        if (projects.Count == 0) return;

        Console.WriteLine("  Top Projects by Duration");
        Console.WriteLine();

        // Calculate column widths
        var nameWidth = Math.Max(7, projects.Max(p => p.Name.Length));
        nameWidth = Math.Min(nameWidth, 30);

        Console.WriteLine($"  {"#",-4} {"Project".PadRight(nameWidth)}  {"Duration",10}  {"% Build",8}  {"Share",-20}  {"Err",4}  {"Warn",4}");
        Console.WriteLine($"  {new string('-', 4)} {new string('-', nameWidth)}  {new string('-', 10)}  {new string('-', 8)}  {new string('-', 20)}  {new string('-', 4)}  {new string('-', 4)}");

        int rank = 1;
        foreach (var p in projects)
        {
            var barWidth = 20;
            var filled = (int)Math.Round(p.Percentage / 100.0 * barWidth);
            var bar = new string('#', filled) + new string('.', barWidth - filled);
            var name = p.Name.Length > nameWidth ? p.Name[..nameWidth] : p.Name;
            var status = p.Succeeded ? " " : "!";

            Console.WriteLine($"  {rank,-4} {status}{name.PadRight(nameWidth - 1)}  {FormatDuration(p.Duration),10}  {p.Percentage,7:F1}%  {bar,-20}  {p.ErrorCount,4}  {p.WarningCount,4}");
            rank++;
        }
    }

    // ──────────────────────────── Targets ────────────────────────────

    private static void RenderTargetsTable(BuildReport report)
    {
        if (report.TopTargets.Count == 0) return;

        Console.WriteLine("  Slowest Targets");
        Console.WriteLine();

        var nameWidth = Math.Max(6, report.TopTargets.Max(t => t.Name.Length));
        nameWidth = Math.Min(nameWidth, 30);
        var projWidth = Math.Max(7, report.TopTargets.Max(t => t.ProjectName.Length));
        projWidth = Math.Min(projWidth, 25);

        Console.WriteLine($"  {"#",-4} {"Target".PadRight(nameWidth)}  {"Project".PadRight(projWidth)}  {"Duration",10}  {"% Build",8}  {"Share",-20}");
        Console.WriteLine($"  {new string('-', 4)} {new string('-', nameWidth)}  {new string('-', projWidth)}  {new string('-', 10)}  {new string('-', 8)}  {new string('-', 20)}");

        int rank = 1;
        foreach (var t in report.TopTargets)
        {
            var barWidth = 20;
            var filled = (int)Math.Round(t.Percentage / 100.0 * barWidth);
            var bar = new string('#', filled) + new string('.', barWidth - filled);
            var name = t.Name.Length > nameWidth ? t.Name[..nameWidth] : t.Name;
            var proj = t.ProjectName.Length > projWidth ? t.ProjectName[..projWidth] : t.ProjectName;

            Console.WriteLine($"  {rank,-4} {name.PadRight(nameWidth)}  {proj.PadRight(projWidth)}  {FormatDuration(t.Duration),10}  {t.Percentage,7:F1}%  {bar,-20}");
            rank++;
        }
    }

    // ──────────────────────────── Timeline ─────────────────────────────

    private static void RenderTimeline(BuildReport report)
    {
        if (report.Projects.Count == 0) return;

        Console.WriteLine("  Build Timeline");
        Console.WriteLine();

        var totalMs = report.TotalDuration.TotalMilliseconds;
        if (totalMs <= 0) return;

        // Sort by start offset for a natural timeline view
        var projects = report.Projects.OrderBy(p => p.StartOffset).ToList();

        var nameWidth = Math.Max(7, projects.Max(p => p.Name.Length));
        nameWidth = Math.Min(nameWidth, 25);
        const int barWidth = 50;
        var pad = "".PadRight(nameWidth);

        // Time axis — place labels at evenly spaced ticks
        var tickCount = 5;
        var tickSpacing = barWidth / (tickCount - 1);
        var labels = new string[tickCount];
        for (int i = 0; i < tickCount; i++)
            labels[i] = FormatDuration(TimeSpan.FromMilliseconds(totalMs * i / (tickCount - 1)));

        // Right-align the last label so it doesn't overflow
        var axisChars = new char[barWidth];
        Array.Fill(axisChars, ' ');
        for (int i = 0; i < tickCount - 1; i++)
        {
            var pos = i * tickSpacing;
            for (int j = 0; j < labels[i].Length && pos + j < barWidth; j++)
                axisChars[pos + j] = labels[i][j];
        }
        // Last label right-aligned to end of bar
        var last = labels[^1];
        var lastStart = barWidth - last.Length;
        for (int j = 0; j < last.Length; j++)
            axisChars[lastStart + j] = last[j];

        Console.WriteLine($"  {pad}  {new string(axisChars)}");

        var tickChars = new char[barWidth];
        Array.Fill(tickChars, '-');
        for (int i = 0; i < tickCount; i++)
        {
            var pos = Math.Min(i * tickSpacing, barWidth - 1);
            tickChars[pos] = '|';
        }
        Console.WriteLine($"  {pad}  {new string(tickChars)}");

        // Render each project bar
        foreach (var p in projects)
        {
            var startPos = (int)Math.Round(p.StartOffset.TotalMilliseconds / totalMs * barWidth);
            var endPos = (int)Math.Round(p.EndOffset.TotalMilliseconds / totalMs * barWidth);
            startPos = Math.Clamp(startPos, 0, barWidth - 1);
            endPos = Math.Clamp(endPos, startPos + 1, barWidth);

            var bar = new char[barWidth];
            Array.Fill(bar, ' ');
            for (int i = startPos; i < endPos; i++)
                bar[i] = '#';

            var name = p.Name.Length > nameWidth ? p.Name[..nameWidth] : p.Name;
            Console.WriteLine($"  {name.PadRight(nameWidth)}  {new string(bar)}  {FormatDuration(p.EndOffset - p.StartOffset)}");
        }

        Console.WriteLine();
    }

    // ──────────────────────────── Analysis ───────────────────────────

    public static void RenderAnalysis(BuildAnalysis analysis)
    {
        if (analysis.Findings.Count == 0) return;

        Console.WriteLine();
        WriteHeader("Analysis");

        Console.WriteLine("  Key Findings");
        Console.WriteLine();

        foreach (var f in analysis.Findings)
        {
            var severity = f.Severity switch
            {
                FindingSeverity.Critical => "CRITICAL",
                FindingSeverity.Warning => "WARNING",
                _ => "INFO",
            };
            Console.WriteLine($"  {f.Number}. [{severity}] {f.Title}");
            Console.WriteLine($"     {f.Detail}");
            Console.WriteLine();
        }

        if (analysis.Recommendations.Count > 0)
        {
            Console.WriteLine("  Recommendations");
            Console.WriteLine();
            foreach (var r in analysis.Recommendations)
            {
                Console.WriteLine($"  {r.Number}. {r.Text}");
            }
            Console.WriteLine();
        }
    }

    // ──────────────────────────── Helpers ────────────────────────────

    public static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 1) return FormattableString.Invariant($"{ts.TotalMilliseconds:F0}ms");
        if (ts.TotalMinutes < 1) return FormattableString.Invariant($"{ts.TotalSeconds:F2}s");
        return FormattableString.Invariant($"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s");
    }
}
