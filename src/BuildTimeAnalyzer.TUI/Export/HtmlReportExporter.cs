using System.Text;
using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Rendering;

namespace BuildTimeAnalyzer.Export;

public static class HtmlReportExporter
{
    public static void Export(BuildReport report, string outputPath, int topN, BuildAnalysis? analysis = null)
    {
        var html = BuildHtml(report, topN, analysis);
        File.WriteAllText(outputPath, html, Encoding.UTF8);
    }

    private static string BuildHtml(BuildReport report, int topN, BuildAnalysis? analysis)
    {
        var sb = new StringBuilder();
        var statusClass = report.Succeeded ? "success" : "fail";
        var statusText = report.Succeeded ? "Build Succeeded" : "Build Failed";
        var criticalSet = new HashSet<string>(
            report.CriticalPath.Select(p => p.FullPath),
            StringComparer.OrdinalIgnoreCase);

        sb.AppendLine($$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1.0"/>
<title>Build Timing Report – {{Esc(Path.GetFileName(report.ProjectOrSolutionPath))}}</title>
<style>
  :root {
    --bg: #0d1117; --surface: #161b22; --border: #30363d;
    --text: #e6edf3; --muted: #8b949e;
    --green: #3fb950; --red: #f85149; --yellow: #d29922;
    --blue: #58a6ff; --cyan: #39d353; --orange: #f78166;
  }
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body { background: var(--bg); color: var(--text); font-family: 'Segoe UI', system-ui, sans-serif; padding: 32px; max-width: 1400px; margin: 0 auto; }
  h1 { font-size: 1.6rem; margin-bottom: 4px; }
  h2 { font-size: 1.2rem; color: var(--blue); margin: 32px 0 12px; }
  p.note { font-size: 0.8rem; color: var(--muted); margin-top: -6px; margin-bottom: 12px; font-style: italic; }
  .badge { display: inline-block; padding: 4px 14px; border-radius: 999px; font-weight: 700; font-size: 0.9rem; }
  .badge.success { background: #1a3a1e; color: var(--green); }
  .badge.fail    { background: #3a1a1a; color: var(--red); }
  .summary-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 12px; margin: 20px 0; }
  .stat { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 14px 18px; }
  .stat .label { font-size: 0.75rem; color: var(--muted); text-transform: uppercase; letter-spacing: .05em; }
  .stat .value { font-size: 1.4rem; font-weight: 700; margin-top: 4px; }
  .context-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 8px 24px; background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 16px 20px; margin: 12px 0 24px; }
  .context-grid .k { color: var(--muted); font-size: 0.82rem; }
  .context-grid .v { font-size: 0.9rem; }
  table { width: 100%; border-collapse: collapse; background: var(--surface); border-radius: 8px; overflow: hidden; }
  th { background: #21262d; color: var(--muted); font-size: 0.8rem; text-transform: uppercase; letter-spacing: .05em; padding: 10px 14px; text-align: left; border-bottom: 1px solid var(--border); }
  th.right, td.right { text-align: right; }
  td { padding: 9px 14px; border-bottom: 1px solid var(--border); font-size: 0.9rem; }
  tr:last-child td { border-bottom: none; }
  tr:hover td { background: #1c2128; }
  tr.critical-path td:first-child { border-left: 3px solid var(--red); padding-left: 11px; }
  .drilldown td { background: #0d1117; font-size: 0.82rem; color: var(--muted); padding-left: 36px; }
  .drilldown td strong { color: var(--text); }
  .bar-wrap { width: 140px; background: #21262d; border-radius: 4px; height: 8px; overflow: hidden; }
  .bar-fill { height: 100%; border-radius: 4px; }
  .bar-high { background: var(--red); }
  .bar-mid  { background: var(--yellow); }
  .bar-low  { background: var(--green); }
  .muted { color: var(--muted); }
  .red   { color: var(--red); }
  .yellow{ color: var(--yellow); }
  .green { color: var(--green); }
  .cyan  { color: var(--cyan); }
  .orange{ color: var(--orange); }
  .rank  { color: var(--muted); font-size: 0.8rem; }
  .cat-badge { display: inline-block; padding: 2px 8px; border-radius: 4px; font-size: 0.72rem; text-transform: uppercase; letter-spacing: .04em; background: #21262d; color: var(--muted); }
  .cat-compile { background: #0b2d1f; color: var(--green); }
  .cat-copy { background: #2d230b; color: var(--yellow); }
  .cat-restore { background: #0b1f2d; color: var(--blue); }
  .cat-uncategorized { background: #2d0b1f; color: var(--orange); }
  footer { margin-top: 40px; color: var(--muted); font-size: 0.78rem; }
  .analysis { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 20px 24px; margin-top: 8px; }
  .analysis h3 { color: var(--blue); margin-bottom: 16px; font-size: 1rem; }
  .finding { margin-bottom: 18px; }
  .finding .num { font-weight: 700; margin-right: 6px; }
  .finding .title { font-weight: 700; }
  .finding .detail { color: var(--muted); margin-top: 4px; font-size: 0.88rem; }
  .finding .evidence { color: var(--muted); margin-top: 4px; font-size: 0.78rem; font-family: Consolas, monospace; }
  .severity-critical .num, .severity-critical .title { color: var(--red); }
  .severity-warning .num, .severity-warning .title { color: var(--yellow); }
  .severity-info .num, .severity-info .title { color: var(--blue); }
  .recommendation { margin: 8px 0; font-size: 0.92rem; }
  .recommendation .num { color: var(--green); font-weight: 700; margin-right: 6px; }
  .timeline-row { display:flex; align-items:center; margin:3px 0; font-size:0.85rem; }
  .timeline-row .name { width:210px; flex-shrink:0; text-align:right; padding-right:12px; color:var(--muted); font-family: Consolas, monospace; font-size: 0.82rem; }
  .timeline-row.critical .name { color: var(--red); font-weight: 700; }
  .timeline-row .track { flex:1; height:18px; position:relative; background:#21262d; border-radius:3px; }
  .timeline-row .dur { width:80px; flex-shrink:0; padding-left:8px; font-size:0.8rem; color:var(--muted); }
</style>
</head>
<body>
<h1>Build Timing Report</h1>
<p style="color:var(--muted); margin-top:4px; margin-bottom:16px">{{Esc(report.ProjectOrSolutionPath)}}</p>
<span class="badge {{statusClass}}">{{statusText}}</span>

<div class="summary-grid">
  <div class="stat"><div class="label">Wall Clock</div><div class="value">{{Esc(ConsoleReportRenderer.FormatDuration(report.TotalDuration))}}</div></div>
  <div class="stat"><div class="label">Started</div><div class="value" style="font-size:1rem">{{report.StartTime:HH:mm:ss}}</div></div>
  <div class="stat"><div class="label">Projects</div><div class="value">{{report.Projects.Count}}</div></div>
  <div class="stat"><div class="label">Errors</div><div class="value {{(report.ErrorCount > 0 ? "red" : "")}}">{{report.ErrorCount}}</div></div>
  <div class="stat"><div class="label">Warnings</div><div class="value {{(report.WarningCount > 0 ? "yellow" : "")}}">{{report.WarningCount}}</div></div>
</div>
""");

        // Build context
        var ctxRows = BuildContextRows(report);
        if (ctxRows.Count > 0)
        {
            sb.AppendLine("<h2>Build Context</h2>");
            sb.AppendLine("<div class=\"context-grid\">");
            foreach (var (k, v) in ctxRows)
                sb.AppendLine($"<div><span class=\"k\">{Esc(k)}:</span> <span class=\"v\">{Esc(v)}</span></div>");
            sb.AppendLine("</div>");
        }

        // Critical path
        if (report.CriticalPath.Count > 0)
        {
            sb.AppendLine("<h2>Critical Path</h2>");
            sb.AppendLine("<p class=\"note\">Estimate of the longest sequential chain implied by the observed project DAG and measured self times. Path total: "
                + $"{Esc(ConsoleReportRenderer.FormatDuration(report.CriticalPathTotal))} of {Esc(ConsoleReportRenderer.FormatDuration(report.TotalDuration))} wall clock.</p>");
            sb.AppendLine("<table><thead><tr><th class=\"right\">Step</th><th>Project</th><th class=\"right\">Self Time</th><th class=\"right\">% Self</th></tr></thead><tbody>");
            int step = 1;
            foreach (var p in report.CriticalPath)
            {
                sb.AppendLine($"""
<tr class="critical-path">
  <td class="right rank">{step}</td>
  <td class="red"><strong>{Esc(p.Name)}</strong></td>
  <td class="right"><strong>{Esc(ConsoleReportRenderer.FormatDuration(p.SelfTime))}</strong></td>
  <td class="right muted">{p.SelfPercent:F1}%</td>
</tr>
""");
                step++;
            }
            sb.AppendLine("</tbody></table>");
        }

        // Timeline with critical path highlighting
        if (report.Projects.Count > 0 && report.TotalDuration.TotalMilliseconds > 0)
        {
            sb.AppendLine("<h2>Build Timeline</h2>");
            sb.AppendLine("<p class=\"note\">Wall-clock Span per project. Red bars are on the critical path; grey bars are not.</p>");
            sb.AppendLine("<div class=\"analysis\" style=\"overflow-x:auto\">");
            var totalMs = report.TotalDuration.TotalMilliseconds;
            var timelineProjects = report.Projects.OrderBy(p => p.StartOffset).ToList();
            foreach (var p in timelineProjects)
            {
                var leftPct = p.StartOffset.TotalMilliseconds / totalMs * 100;
                var widthPct = Math.Max(0.5, (p.EndOffset - p.StartOffset).TotalMilliseconds / totalMs * 100);
                var onCritical = criticalSet.Contains(p.FullPath);
                var barColor = onCritical ? "var(--red)" : "var(--muted)";
                var rowClass = onCritical ? "timeline-row critical" : "timeline-row";
                sb.AppendLine($"""
<div class="{rowClass}">
  <span class="name">{Esc(p.Name)}</span>
  <div class="track">
    <div style="position:absolute; left:{leftPct:F1}%; width:{widthPct:F1}%; height:100%; border-radius:3px; background:{barColor}" title="{Esc(p.Name)}: {Esc(ConsoleReportRenderer.FormatDuration(p.Span))}"></div>
  </div>
  <span class="dur">{Esc(ConsoleReportRenderer.FormatDuration(p.Span))}</span>
</div>
""");
            }
            // Axis
            sb.AppendLine("<div class=\"timeline-row\" style=\"margin-top:6px; color:var(--muted); font-size:0.75rem\">");
            sb.AppendLine("<span class=\"name\"></span>");
            sb.AppendLine("<div class=\"track\" style=\"background:transparent; display:flex; justify-content:space-between\">");
            for (int i = 0; i <= 4; i++)
            {
                var t = ConsoleReportRenderer.FormatDuration(TimeSpan.FromMilliseconds(totalMs * i / 4));
                sb.AppendLine($"<span>{Esc(t)}</span>");
            }
            sb.AppendLine("</div>");
            sb.AppendLine("<span class=\"dur\"></span>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
        }

        // Projects table with drill-down
        sb.AppendLine("<h2>Top Projects by Self Time</h2>");
        sb.AppendLine("<p class=\"note\">Self Time = genuinely exclusive work. Span = wall-clock first-to-last activity (display only).</p>");
        sb.AppendLine("<table><thead><tr><th class=\"right\">#</th><th>Project</th><th class=\"right\">Self Time</th><th class=\"right\">Span</th><th class=\"right\">% Self</th><th>Share</th><th class=\"right\">Errors</th><th class=\"right\">Warnings</th></tr></thead><tbody>");

        int rank = 1;
        foreach (var p in report.Projects.Take(topN))
        {
            var barClass = p.SelfPercent > 50 ? "bar-high" : p.SelfPercent > 20 ? "bar-mid" : "bar-low";
            var barPct = Math.Min(100, p.SelfPercent);
            var icon = p.Succeeded ? "" : "<span class=\"red\">! </span>";
            var errCell = p.ErrorCount > 0 ? $"<span class=\"red\">{p.ErrorCount}</span>" : "<span class=\"muted\">0</span>";
            var warnCell = p.WarningCount > 0 ? $"<span class=\"yellow\">{p.WarningCount}</span>" : "<span class=\"muted\">0</span>";
            var rowClass = criticalSet.Contains(p.FullPath) ? " class=\"critical-path\"" : "";

            sb.AppendLine($"""
<tr{rowClass}>
  <td class="right rank">{rank++}</td>
  <td>{icon}{Esc(p.Name)}</td>
  <td class="right"><strong>{Esc(ConsoleReportRenderer.FormatDuration(p.SelfTime))}</strong></td>
  <td class="right muted">{Esc(ConsoleReportRenderer.FormatDuration(p.Span))}</td>
  <td class="right muted">{p.SelfPercent:F1}%</td>
  <td><div class="bar-wrap"><div class="bar-fill {barClass}" style="width:{barPct:F1}%"></div></div></td>
  <td class="right">{errCell}</td>
  <td class="right">{warnCell}</td>
</tr>
""");

            // Inline drill-down for projects that have target data populated
            foreach (var t in p.Targets)
            {
                sb.AppendLine($"""
<tr class="drilldown">
  <td></td>
  <td colspan="2">&nbsp;&nbsp;↳ <strong>{Esc(t.Name)}</strong> <span class="cat-badge cat-{t.Category.ToString().ToLowerInvariant()}">{Esc(CategoryLabel(t.Category))}</span></td>
  <td class="right">{Esc(ConsoleReportRenderer.FormatDuration(t.SelfTime))}</td>
  <td class="right">{t.SelfPercent:F1}%</td>
  <td colspan="3"></td>
</tr>
""");
            }
        }
        sb.AppendLine("</tbody></table>");

        // Targets table
        if (report.TopTargets.Count > 0)
        {
            sb.AppendLine("<h2>Top Targets by Self Time</h2>");
            sb.AppendLine("<p class=\"note\">Categories are deterministic pattern matches against SDK target names — a grouping hint, not authoritative.</p>");
            sb.AppendLine("<table><thead><tr><th class=\"right\">#</th><th>Target</th><th>Project</th><th>Category</th><th class=\"right\">Self Time</th><th class=\"right\">% Self</th></tr></thead><tbody>");

            rank = 1;
            foreach (var t in report.TopTargets)
            {
                sb.AppendLine($"""
<tr>
  <td class="right rank">{rank++}</td>
  <td class="cyan">{Esc(t.Name)}</td>
  <td class="muted">{Esc(t.ProjectName)}</td>
  <td><span class="cat-badge cat-{t.Category.ToString().ToLowerInvariant()}">{Esc(CategoryLabel(t.Category))}</span></td>
  <td class="right"><strong>{Esc(ConsoleReportRenderer.FormatDuration(t.SelfTime))}</strong></td>
  <td class="right muted">{t.SelfPercent:F1}%</td>
</tr>
""");
            }
            sb.AppendLine("</tbody></table>");
        }

        // Potentially custom targets
        if (report.PotentiallyCustomTargets.Count > 0)
        {
            sb.AppendLine("<h2>Potentially Custom Targets</h2>");
            sb.AppendLine("<p class=\"note\">Targets that did not match any known SDK pattern. Often actionable optimization hotspots — investigate individually.</p>");
            sb.AppendLine("<table><thead><tr><th class=\"right\">#</th><th>Target</th><th>Project</th><th class=\"right\">Self Time</th></tr></thead><tbody>");
            rank = 1;
            foreach (var t in report.PotentiallyCustomTargets.Take(15))
            {
                sb.AppendLine($"""
<tr>
  <td class="right rank">{rank++}</td>
  <td class="orange"><strong>{Esc(t.Name)}</strong></td>
  <td class="muted">{Esc(t.ProjectName)}</td>
  <td class="right"><strong>{Esc(ConsoleReportRenderer.FormatDuration(t.SelfTime))}</strong></td>
</tr>
""");
            }
            sb.AppendLine("</tbody></table>");
        }

        // Analysis section
        if (analysis is { Findings.Count: > 0 })
        {
            sb.AppendLine("<h2>Analysis</h2>");
            sb.AppendLine("<div class=\"analysis\">");
            sb.AppendLine("<h3>Key Findings</h3>");
            foreach (var f in analysis.Findings)
            {
                var sevClass = f.Severity switch
                {
                    FindingSeverity.Critical => "severity-critical",
                    FindingSeverity.Warning => "severity-warning",
                    _ => "severity-info",
                };
                sb.AppendLine($"""
<div class="finding {sevClass}">
  <span class="num">{f.Number}.</span><span class="title">{Esc(f.Title)}</span>
  <div class="detail">{Esc(f.Detail)}</div>
  <div class="evidence">Evidence: {Esc(f.Evidence)} &nbsp; · &nbsp; Threshold: {Esc(f.ThresholdName)}</div>
</div>
""");
            }

            if (analysis.Recommendations.Count > 0)
            {
                sb.AppendLine("<h3 style=\"margin-top:24px\">Recommendations</h3>");
                sb.AppendLine("<p class=\"note\">These are investigations to run, not conclusions. Architecture decisions require more than timing data.</p>");
                foreach (var r in analysis.Recommendations)
                    sb.AppendLine($"<div class=\"recommendation\"><span class=\"num\">{r.Number}.</span>{Esc(r.Text)}</div>");
            }
            sb.AppendLine("</div>");
        }

        sb.AppendLine($"<footer>Generated by BuildTimeAnalyzer on {DateTime.Now:yyyy-MM-dd HH:mm:ss}</footer>");
        sb.AppendLine("</body></html>");

        return sb.ToString();
    }

    private static List<(string Key, string Value)> BuildContextRows(BuildReport report)
    {
        var rows = new List<(string, string)>();
        var ctx = report.Context;
        if (ctx.Configuration is not null) rows.Add(("Configuration", ctx.Configuration));
        if (ctx.SdkVersion is not null) rows.Add((".NET SDK", ctx.SdkVersion));
        if (ctx.MSBuildVersion is not null) rows.Add(("MSBuild", ctx.MSBuildVersion));
        if (ctx.OperatingSystem is not null) rows.Add(("OS", ctx.OperatingSystem));
        if (ctx.Parallelism is not null) rows.Add(("Parallelism", $"{ctx.Parallelism} nodes"));
        if (ctx.RestoreObserved is true) rows.Add(("Restore", "included in this build"));

        var totalTargets = report.ExecutedTargetCount + report.SkippedTargetCount;
        if (totalTargets > 0)
            rows.Add(("Incremental Behavior", $"{report.SkippedTargetCount} of {totalTargets} targets skipped as up-to-date"));

        return rows;
    }

    private static string CategoryLabel(TargetCategory category) => category switch
    {
        TargetCategory.Compile => "compile",
        TargetCategory.SourceGen => "source-gen",
        TargetCategory.StaticWebAssets => "static-web",
        TargetCategory.Copy => "output copy",
        TargetCategory.Restore => "restore",
        TargetCategory.References => "references",
        TargetCategory.Uncategorized => "uncategorized",
        TargetCategory.Other => "internal",
        _ => "unknown",
    };

    private static string Esc(string? s) =>
        (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
