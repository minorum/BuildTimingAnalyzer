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
        var statusText = report.Succeeded ? "✓ Build Succeeded" : "✗ Build Failed";

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
    --blue: #58a6ff; --cyan: #39d353;
  }
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body { background: var(--bg); color: var(--text); font-family: 'Segoe UI', system-ui, sans-serif; padding: 32px; }
  h1 { font-size: 1.6rem; margin-bottom: 4px; }
  h2 { font-size: 1.2rem; color: var(--blue); margin: 32px 0 12px; }
  .badge { display: inline-block; padding: 4px 14px; border-radius: 999px; font-weight: 700; font-size: 0.9rem; }
  .badge.success { background: #1a3a1e; color: var(--green); }
  .badge.fail    { background: #3a1a1a; color: var(--red); }
  .summary-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 12px; margin: 20px 0; }
  .stat { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 14px 18px; }
  .stat .label { font-size: 0.75rem; color: var(--muted); text-transform: uppercase; letter-spacing: .05em; }
  .stat .value { font-size: 1.4rem; font-weight: 700; margin-top: 4px; }
  table { width: 100%; border-collapse: collapse; background: var(--surface); border-radius: 8px; overflow: hidden; }
  th { background: #21262d; color: var(--muted); font-size: 0.8rem; text-transform: uppercase; letter-spacing: .05em; padding: 10px 14px; text-align: left; border-bottom: 1px solid var(--border); }
  th.right, td.right { text-align: right; }
  td { padding: 9px 14px; border-bottom: 1px solid var(--border); font-size: 0.9rem; }
  tr:last-child td { border-bottom: none; }
  tr:hover td { background: #1c2128; }
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
  .rank  { color: var(--muted); font-size: 0.8rem; }
  footer { margin-top: 40px; color: var(--muted); font-size: 0.78rem; }
  .analysis { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 20px 24px; margin-top: 8px; }
  .analysis h3 { color: var(--blue); margin-bottom: 16px; font-size: 1rem; }
  .finding { margin-bottom: 16px; }
  .finding .num { font-weight: 700; margin-right: 6px; }
  .finding .title { font-weight: 700; }
  .finding .detail { color: var(--muted); margin-top: 4px; font-size: 0.88rem; }
  .severity-critical .num, .severity-critical .title { color: var(--red); }
  .severity-warning .num, .severity-warning .title { color: var(--yellow); }
  .severity-info .num, .severity-info .title { color: var(--blue); }
  .recommendation { margin: 8px 0; font-size: 0.92rem; }
  .recommendation .num { color: var(--green); font-weight: 700; margin-right: 6px; }
</style>
</head>
<body>
<h1>Build Timing Report</h1>
<p style="color:var(--muted); margin-top:4px; margin-bottom:16px">{{Esc(report.ProjectOrSolutionPath)}}</p>
<span class="badge {{statusClass}}">{{statusText}}</span>

<div class="summary-grid">
  <div class="stat"><div class="label">Total Duration</div><div class="value">{{Esc(ConsoleReportRenderer.FormatDuration(report.TotalDuration))}}</div></div>
  <div class="stat"><div class="label">Started</div><div class="value" style="font-size:1rem">{{report.StartTime:HH:mm:ss}}</div></div>
  <div class="stat"><div class="label">Projects</div><div class="value">{{report.Projects.Count}}</div></div>
  <div class="stat"><div class="label">Errors</div><div class="value {{(report.ErrorCount > 0 ? "red" : "")}}">{{report.ErrorCount}}</div></div>
  <div class="stat"><div class="label">Warnings</div><div class="value {{(report.WarningCount > 0 ? "yellow" : "")}}">{{report.WarningCount}}</div></div>
</div>
""");

        // Projects table
        sb.AppendLine("<h2>⏱ Top Projects by Duration</h2>");
        sb.AppendLine("<table><thead><tr><th class=\"right\">#</th><th>Project</th><th class=\"right\">Duration</th><th class=\"right\">% of Build</th><th>Share</th><th class=\"right\">Errors</th><th class=\"right\">Warnings</th></tr></thead><tbody>");

        int rank = 1;
        foreach (var p in report.Projects.Take(topN))
        {
            var barClass = p.Percentage > 50 ? "bar-high" : p.Percentage > 20 ? "bar-mid" : "bar-low";
            var barPct = Math.Min(100, p.Percentage);
            var icon = p.Succeeded ? "" : "<span class=\"red\">✗ </span>";
            var errCell = p.ErrorCount > 0 ? $"<span class=\"red\">{p.ErrorCount}</span>" : "<span class=\"muted\">0</span>";
            var warnCell = p.WarningCount > 0 ? $"<span class=\"yellow\">{p.WarningCount}</span>" : "<span class=\"muted\">0</span>";

            sb.AppendLine($"""
<tr>
  <td class="right rank">{rank++}</td>
  <td>{icon}{Esc(p.Name)}</td>
  <td class="right"><strong>{Esc(ConsoleReportRenderer.FormatDuration(p.Duration))}</strong></td>
  <td class="right muted">{p.Percentage:F1}%</td>
  <td><div class="bar-wrap"><div class="bar-fill {barClass}" style="width:{barPct:F1}%"></div></div></td>
  <td class="right">{errCell}</td>
  <td class="right">{warnCell}</td>
</tr>
""");
        }
        sb.AppendLine("</tbody></table>");

        // Targets table
        if (report.TopTargets.Count > 0)
        {
            sb.AppendLine("<h2>🎯 Slowest Targets</h2>");
            sb.AppendLine("<table><thead><tr><th class=\"right\">#</th><th>Target</th><th>Project</th><th class=\"right\">Duration</th><th class=\"right\">% of Build</th><th>Share</th></tr></thead><tbody>");

            rank = 1;
            foreach (var t in report.TopTargets)
            {
                var barClass = t.Percentage > 30 ? "bar-high" : t.Percentage > 10 ? "bar-mid" : "bar-low";
                var barPct = Math.Min(100, t.Percentage);

                sb.AppendLine($"""
<tr>
  <td class="right rank">{rank++}</td>
  <td class="cyan">{Esc(t.Name)}</td>
  <td class="muted">{Esc(t.ProjectName)}</td>
  <td class="right"><strong>{Esc(ConsoleReportRenderer.FormatDuration(t.Duration))}</strong></td>
  <td class="right muted">{t.Percentage:F1}%</td>
  <td><div class="bar-wrap"><div class="bar-fill {barClass}" style="width:{barPct:F1}%"></div></div></td>
</tr>
""");
            }
            sb.AppendLine("</tbody></table>");
        }

        // Analysis section
        if (analysis is { Findings.Count: > 0 })
        {
            sb.AppendLine("<h2>📊 Analysis</h2>");
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
</div>
""");
            }

            if (analysis.Recommendations.Count > 0)
            {
                sb.AppendLine("<h3 style=\"margin-top:24px\">Recommendations</h3>");
                foreach (var r in analysis.Recommendations)
                {
                    sb.AppendLine($"<div class=\"recommendation\"><span class=\"num\">{r.Number}.</span>{Esc(r.Text)}</div>");
                }
            }
            sb.AppendLine("</div>");
        }

        sb.AppendLine($"<footer>Generated by BuildTimeAnalyzer on {DateTime.Now:yyyy-MM-dd HH:mm:ss}</footer>");
        sb.AppendLine("</body></html>");

        return sb.ToString();
    }

    private static string Esc(string? s) =>
        (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
