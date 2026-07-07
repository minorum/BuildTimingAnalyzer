using System.Text;
using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Rendering;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Export;

/// <summary>
/// Renders a concise Markdown report. Designed for pasting into PRs/issues and for use as a CI
/// step summary (e.g. appended to <c>$GITHUB_STEP_SUMMARY</c>). Contains the summary line, findings,
/// top consumers, and — when a baseline is supplied — a build-over-build comparison.
/// </summary>
public static class MarkdownReportExporter
{
    public static void Export(
        BuildReport report,
        string outputPath,
        BuildAnalysis? analysis = null,
        BuildComparisonResult? comparison = null)
    {
        File.WriteAllText(outputPath, BuildMarkdown(report, analysis, comparison), Encoding.UTF8);
    }

    public static string BuildMarkdown(
        BuildReport report,
        BuildAnalysis? analysis,
        BuildComparisonResult? comparison = null)
    {
        var sb = new StringBuilder();
        var projectName = Path.GetFileName(report.ProjectOrSolutionPath);
        if (string.IsNullOrEmpty(projectName)) projectName = report.ProjectOrSolutionPath;

        sb.Append("# Build Timing Report — ").AppendLine(projectName);
        sb.AppendLine();

        var status = report.Succeeded ? "OK" : "FAILED";
        sb.Append("**Build ").Append(status).Append("** in ").Append(Fmt(report.TotalDuration));
        if (report.WarningCount > 0) sb.Append(" · ").Append(report.WarningCount).Append(" warning(s)");
        if (report.ErrorCount > 0) sb.Append(" · ").Append(report.ErrorCount).Append(" error(s)");
        sb.AppendLine();
        sb.AppendLine();
        sb.Append("Total self time ").Append(Fmt(report.TotalSelfTime))
          .Append(" · achieved parallelism ").Append(report.AchievedParallelism.ToString("F2"))
          .Append("× · ").Append(report.Projects.Count).AppendLine(" project(s)");
        if (report.Context.Configuration is { Length: > 0 } cfg)
        {
            sb.AppendLine();
            sb.Append("Configuration: `").Append(cfg).Append('`');
            if (report.Context.BuildMode is { Length: > 0 } mode) sb.Append(" · ").Append(mode);
            sb.AppendLine();
        }
        sb.AppendLine();

        // ── Findings ──
        sb.AppendLine("## Findings");
        sb.AppendLine();
        var findings = analysis?.Findings ?? [];
        if (findings.Count == 0)
        {
            sb.AppendLine("_No findings._");
        }
        else
        {
            foreach (var f in findings)
            {
                var label = f.Severity switch
                {
                    FindingSeverity.Critical => "CRITICAL",
                    FindingSeverity.Warning => "WARNING",
                    _ => "INFO",
                };
                sb.Append("### [").Append(label).Append("] ").AppendLine(f.Title);
                sb.Append("- **Measured:** ").AppendLine(f.Measured);
                if (!string.IsNullOrEmpty(f.LikelyExplanation))
                    sb.Append("- **Likely:** ").AppendLine(f.LikelyExplanation);
                sb.Append("- **Investigate:** ").AppendLine(f.InvestigationSuggestion);
                sb.AppendLine();
            }
        }

        // ── Top consumers ──
        sb.AppendLine("## Top consumers");
        sb.AppendLine();
        if (report.Projects.Count == 0)
        {
            sb.AppendLine("_No project timing data._");
        }
        else
        {
            sb.AppendLine("| Project | Self time | % Self | Dominant |");
            sb.AppendLine("|---|--:|--:|---|");
            foreach (var p in report.Projects.Take(10))
            {
                sb.Append("| ").Append(Cell(p.Name))
                  .Append(" | ").Append(Fmt(p.SelfTime))
                  .Append(" | ").Append(p.SelfPercent.ToString("F1")).Append('%')
                  .Append(" | ").Append(Cell(DominantCategory(p)))
                  .AppendLine(" |");
            }
        }
        sb.AppendLine();

        // ── Comparison ──
        if (comparison is not null)
        {
            AppendComparison(sb, comparison);
        }

        return sb.ToString();
    }

    private static void AppendComparison(StringBuilder sb, BuildComparisonResult c)
    {
        sb.AppendLine("## Comparison vs baseline");
        sb.AppendLine();
        sb.AppendLine("| Metric | Δ | Δ % |");
        sb.AppendLine("|---|--:|--:|");
        sb.Append("| Wall-clock | ").Append(SignedMs(c.WallClockDeltaMs))
          .Append(" | ").Append(Signed(c.WallClockDeltaPercent)).AppendLine("% |");
        sb.Append("| Total self time | ").Append(SignedMs(c.TotalSelfTimeDeltaMs))
          .Append(" | ").Append(Signed(c.TotalSelfTimeDeltaPercent)).AppendLine("% |");
        sb.Append("| Warnings | ").Append(Signed(c.WarningDelta)).AppendLine(" | |");
        sb.Append("| Errors | ").Append(Signed(c.ErrorDelta)).AppendLine(" | |");
        sb.AppendLine();

        if (c.Regressions.Count > 0)
        {
            sb.AppendLine("### Top regressions");
            sb.AppendLine();
            sb.AppendLine("| Project | Baseline | Current | Δ |");
            sb.AppendLine("|---|--:|--:|--:|");
            foreach (var d in c.Regressions)
            {
                sb.Append("| ").Append(Cell(d.Name))
                  .Append(" | ").Append(FmtMs(d.BaselineMs))
                  .Append(" | ").Append(FmtMs(d.CurrentMs))
                  .Append(" | ").Append(SignedMs(d.DeltaMs)).Append(" (").Append(Signed(d.DeltaPercent)).Append("%)")
                  .AppendLine(" |");
            }
            sb.AppendLine();
        }
    }

    private static string DominantCategory(ProjectTiming p)
    {
        if (p.CategoryBreakdown.Count == 0) return "—";
        var top = p.CategoryBreakdown.OrderByDescending(kv => kv.Value.TotalMilliseconds).First();
        return top.Value.TotalMilliseconds <= 0 ? "—" : ConsoleReportRenderer.CategoryLabel(top.Key);
    }

    private static string Cell(string s) => s.Replace("|", "\\|");
    private static string Fmt(TimeSpan ts) => ConsoleReportRenderer.FormatDuration(ts);
    private static string FmtMs(long ms) => ConsoleReportRenderer.FormatDuration(TimeSpan.FromMilliseconds(ms));
    private static string SignedMs(long ms) => (ms >= 0 ? "+" : "-") + FmtMs(Math.Abs(ms));
    private static string Signed(double v) => (v >= 0 ? "+" : "") + v.ToString("F1");
    private static string Signed(int v) => (v >= 0 ? "+" : "") + v.ToString();
}
