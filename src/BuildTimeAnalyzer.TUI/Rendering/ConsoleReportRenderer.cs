using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Rendering;

public static class ConsoleReportRenderer
{
    public static void Render(BuildReport report, int topN)
    {
        RenderSummary(report);
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
