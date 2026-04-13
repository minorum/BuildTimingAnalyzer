using System.Text;
using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Rendering;
using BuildTimeAnalyzer.Services;

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

        var hasCriticalPath = report.CriticalPath.Count > 0 && report.CriticalPathTotal > TimeSpan.Zero;
        var criticalSet = hasCriticalPath
            ? new HashSet<string>(report.CriticalPath.Select(p => p.FullPath), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
  body { background: var(--bg); color: var(--text); font-family: 'IBM Plex Sans', 'Segoe UI', system-ui, sans-serif; padding: 32px; max-width: 1400px; margin: 0 auto; }
  code, pre, .mono { font-family: 'IBM Plex Mono', Consolas, monospace; }
  .summary-line { margin: 12px 0 24px; font-size: 0.98rem; }
  .summary-line .red { font-weight: 700; }
  .bottleneck { margin: 8px 0 14px; padding: 10px 14px; border-left: 3px solid var(--yellow); background: var(--surface); border-radius: 4px; }
  .bottleneck.sev-critical { border-left-color: var(--red); }
  .bottleneck .b-title { font-weight: 700; }
  .bottleneck .b-why { margin-top: 4px; font-size: 0.9rem; color: var(--muted); }
  .bottleneck .b-inspect { margin-top: 4px; font-size: 0.9rem; color: var(--green); }
  .chain-list { list-style: none; padding: 12px 14px; background: var(--surface); border: 1px solid var(--border); border-radius: 6px; margin: 8px 0; display: flex; flex-wrap: wrap; gap: 8px 12px; }
  .chain-list li { font-size: 0.88rem; }
  .chain-list li + li::before { content: "→"; color: var(--muted); margin-right: 8px; }
  .inspect-list { list-style: decimal; padding-left: 24px; margin: 8px 0; }
  .inspect-list li { margin: 6px 0; font-size: 0.9rem; }
  h1 { font-size: 1.6rem; margin-bottom: 4px; }
  h2 { font-size: 1.2rem; color: var(--blue); margin: 32px 0 12px; }
  p.note { font-size: 0.8rem; color: var(--muted); margin-top: -6px; margin-bottom: 12px; font-style: italic; }
  .badge { display: inline-block; padding: 4px 14px; border-radius: 999px; font-weight: 700; font-size: 0.9rem; }
  .badge.success { background: #1a3a1e; color: var(--green); }
  .badge.fail    { background: #3a1a1a; color: var(--red); }
  .badge.accepted { background: #1a3a1e; color: var(--green); }
  .badge.rejected { background: #3a1a1a; color: var(--red); }
  .summary-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 12px; margin: 20px 0; }
  .stat { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 14px 18px; }
  .stat .label { font-size: 0.75rem; color: var(--muted); text-transform: uppercase; letter-spacing: .05em; }
  .stat .value { font-size: 1.4rem; font-weight: 700; margin-top: 4px; }
  .stat .sub { font-size: 0.75rem; color: var(--muted); margin-top: 4px; }
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
  .composition td { background: #0d1117; font-size: 0.82rem; color: var(--muted); padding-left: 36px; }
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
  .cat-references { background: #1f0b2d; color: var(--orange); }
  .cat-uncategorized { background: #2d0b1f; color: var(--orange); }
  .kind-badge { display: inline-block; padding: 1px 6px; border-radius: 3px; font-size: 0.7rem; background: #21262d; color: var(--muted); margin-left: 6px; }
  footer { margin-top: 40px; color: var(--muted); font-size: 0.78rem; }
  .analysis { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 20px 24px; margin-top: 8px; }
  .analysis h3 { color: var(--blue); margin-bottom: 16px; font-size: 1rem; }
  .finding { margin-bottom: 22px; padding-left: 12px; border-left: 3px solid var(--border); }
  .finding .num { font-weight: 700; margin-right: 6px; }
  .finding .title { font-weight: 700; }
  .finding .layer { margin-top: 6px; font-size: 0.86rem; }
  .finding .layer-label { display: inline-block; width: 108px; color: var(--muted); font-size: 0.72rem; text-transform: uppercase; letter-spacing: .05em; vertical-align: top; }
  .finding .layer-value { display: inline-block; width: calc(100% - 120px); }
  .finding .layer-measured .layer-value { color: var(--text); }
  .finding .layer-likely .layer-value { color: var(--muted); font-style: italic; }
  .finding .layer-investigate .layer-value { color: var(--green); }
  .finding .evidence { color: var(--muted); margin-top: 6px; font-size: 0.76rem; font-family: Consolas, monospace; }
  .severity-critical { border-left-color: var(--red); }
  .severity-critical .num, .severity-critical .title { color: var(--red); }
  .severity-warning { border-left-color: var(--yellow); }
  .severity-warning .num, .severity-warning .title { color: var(--yellow); }
  .severity-info { border-left-color: var(--blue); }
  .severity-info .num, .severity-info .title { color: var(--blue); }
  .recommendation { margin: 8px 0; font-size: 0.92rem; }
  .recommendation .num { color: var(--green); font-weight: 700; margin-right: 6px; }
  .timeline-row { display:flex; align-items:center; margin:3px 0; font-size:0.85rem; }
  .timeline-row .name { width:210px; flex-shrink:0; text-align:right; padding-right:12px; color:var(--muted); font-family: Consolas, monospace; font-size: 0.82rem; }
  .timeline-row.critical .name { color: var(--red); font-weight: 700; }
  .timeline-row .track { flex:1; height:18px; position:relative; background:#21262d; border-radius:3px; }
  .timeline-row .dur { width:80px; flex-shrink:0; padding-left:8px; font-size:0.8rem; color:var(--muted); }

  /* Chart canvas wrapper */
  .chart-wrap { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 16px; margin: 12px 0 24px; }
  .chart-wrap canvas { display: block; width: 100%; height: 360px; }
  .chart-legend { margin-top: 10px; display:flex; flex-wrap:wrap; gap: 14px; font-size: 0.78rem; color: var(--muted); }
  .chart-legend .swatch { display:inline-block; width:10px; height:10px; border-radius:2px; margin-right:6px; vertical-align:middle; }

  /* Action card grid */
  .cards-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(420px, 1fr)); gap: 14px; margin: 12px 0 24px; }
  .card { background: var(--surface); border: 1px solid var(--border); border-left: 3px solid var(--border); border-radius: 8px; padding: 14px 18px; }
  .card.sev-critical { border-left-color: var(--red); }
  .card.sev-warning  { border-left-color: var(--yellow); }
  .card.sev-info     { border-left-color: var(--blue); }
  .card .card-head { display:flex; align-items:center; justify-content:space-between; gap:10px; }
  .card .card-title { font-weight: 700; font-size: 0.96rem; flex: 1; }
  .card .sev-badge { font-size: 0.66rem; text-transform: uppercase; letter-spacing: .05em; padding: 2px 8px; border-radius: 3px; font-weight: 700; flex-shrink: 0; }
  .card.sev-critical .sev-badge { background: #3a1a1a; color: var(--red); }
  .card.sev-warning  .sev-badge { background: #2d230b; color: var(--yellow); }
  .card.sev-info     .sev-badge { background: #0b1f2d; color: var(--blue); }
  .card .card-body { margin-top: 8px; font-size: 0.86rem; color: var(--text); }
  .card .card-explain { margin-top: 6px; font-size: 0.82rem; color: var(--muted); font-style: italic; }
  .card .card-action { margin-top: 8px; font-size: 0.84rem; color: var(--green); }
  .card .card-data { margin-top: 10px; font-size: 0.78rem; }
  .card .card-data table { background: transparent; border: 1px solid var(--border); border-radius: 4px; }
  .card .card-data th, .card .card-data td { padding: 4px 8px; font-size: 0.78rem; }
  .card .card-meta { margin-top: 8px; font-size: 0.72rem; color: var(--muted); font-family: 'IBM Plex Mono', Consolas, monospace; }
  .card .saving { margin-top: 8px; padding: 6px 10px; background: #1a2d1f; color: var(--green); border-radius: 4px; font-size: 0.82rem; font-weight: 600; display: inline-block; }

  /* Critical path horizontal chain */
  .cp-chain { display:flex; gap:8px; overflow-x:auto; padding: 12px 4px; border:1px solid var(--border); border-radius:8px; background: var(--surface); }
  .cp-node { flex:0 0 auto; min-width: 140px; padding: 10px 12px; border-radius: 6px; background: #21262d; border-left: 3px solid var(--red); }
  .cp-node.sev-warn { border-left-color: var(--yellow); }
  .cp-node.sev-info { border-left-color: var(--blue); }
  .cp-node .cp-name { font-weight: 700; font-size: 0.84rem; }
  .cp-node .cp-self { font-size: 0.74rem; color: var(--muted); margin-top: 4px; }
  .cp-arrow { flex:0 0 auto; align-self:center; color:var(--muted); }
  .cp-progress { margin-top: 10px; height: 8px; background: #21262d; border-radius: 4px; overflow: hidden; }
  .cp-progress > div { height:100%; background: linear-gradient(90deg, var(--red), var(--orange)); }

  /* Collapsible details with rotating arrow */
  details.section { margin: 12px 0; }
  details.section > summary { cursor: pointer; list-style: none; padding: 8px 0; user-select: none; }
  details.section > summary::-webkit-details-marker { display: none; }
  details.section > summary .arrow { display:inline-block; transition: transform 120ms ease; margin-right: 6px; color: var(--muted); }
  details.section[open] > summary .arrow { transform: rotate(90deg); }
  details.section > summary h2 { display: inline; font-size: 1.2rem; color: var(--blue); margin: 0; }
</style>
</head>
<body>
<h1>Build Report</h1>
<p style="color:var(--muted); margin-top:4px; margin-bottom:8px">{{Esc(report.ProjectOrSolutionPath)}}</p>
<p class="summary-line">
  <span class="{{(report.Succeeded ? "green" : "red")}}">{{statusText}}</span>
  <span class="muted"> · </span>{{Esc(ConsoleReportRenderer.FormatDuration(report.TotalDuration))}} elapsed
  <span class="muted"> · </span>{{report.Projects.Count}} project{{(report.Projects.Count == 1 ? "" : "s")}}
  <span class="muted"> · </span><span class="{{(report.ErrorCount > 0 ? "red" : "muted")}}">{{report.ErrorCount}} error{{(report.ErrorCount == 1 ? "" : "s")}}</span>
  <span class="muted"> · </span><span class="{{(report.WarningCount > 0 ? "yellow" : "muted")}}">{{report.WarningCount}} warning{{(report.WarningCount == 1 ? "" : "s")}}</span>
</p>
""");

        // ── Strict layout: bottlenecks → blocking chain → top consumers → inspect next ──
        AppendTopBottlenecks(sb, analysis);
        AppendBlockingChain(sb, report);
        AppendTopConsumers(sb, report);
        AppendInspectNext(sb, analysis);
        AppendPerProjectBreakdown(sb, report);

        // Build context (collapsed)
        var ctxRows = BuildContextRows(report);
        if (ctxRows.Count > 0)
        {
            sb.AppendLine("<details class=\"section\"><summary><span class=\"arrow\">▶</span><h2>Build Context</h2></summary>");
            sb.AppendLine("<div class=\"context-grid\">");
            foreach (var (k, v) in ctxRows)
                sb.AppendLine($"<div><span class=\"k\">{Esc(k)}:</span> <span class=\"v\">{Esc(v)}</span></div>");
            sb.AppendLine("</div>");
            sb.AppendLine("</details>");
        }

        // Graph health (collapsed)
        if (report.Graph.Nodes.Count > 0)
        {
            var h = report.Graph.Health;
            sb.AppendLine("<details class=\"section\"><summary><span class=\"arrow\">▶</span><h2>Dependency Graph Health</h2></summary>");
            sb.AppendLine("<div class=\"context-grid\">");
            sb.AppendLine($"<div><span class=\"k\">Total projects:</span> <span class=\"v\">{h.TotalProjects}</span></div>");
            sb.AppendLine($"<div><span class=\"k\">Total edges:</span> <span class=\"v\">{h.TotalEdges}</span></div>");
            sb.AppendLine($"<div><span class=\"k\">Nodes with outgoing:</span> <span class=\"v\">{h.NodesWithOutgoing}</span></div>");
            sb.AppendLine($"<div><span class=\"k\">Nodes with incoming:</span> <span class=\"v\">{h.NodesWithIncoming}</span></div>");
            sb.AppendLine($"<div><span class=\"k\">Isolated nodes:</span> <span class=\"v\">{h.IsolatedNodes}</span></div>");
            sb.AppendLine($"<div><span class=\"k\">Longest chain:</span> <span class=\"v\">{report.Graph.LongestChainProjectCount} projects</span></div>");
            sb.AppendLine("</div>");

            if (!report.Graph.IsUsable)
                sb.AppendLine("<p class=\"note\" style=\"color:var(--yellow)\">Graph has too few edges for derived analyses (critical path, etc.). Check whether ProjectReference extraction captured your project references correctly.</p>");
            sb.AppendLine("</details>");
        }

        // Dependency hubs (collapsed)
        if (report.Graph.TopHubs.Count > 0)
        {
            sb.AppendLine("<details class=\"section\"><summary><span class=\"arrow\">▶</span><h2>Dependency Hubs</h2></summary>");
            sb.AppendLine("<p class=\"note\">Sorted by transitive dependents (downstream subtree size). High fan-in is a structural signal, not automatic proof of bottleneck status.</p>");
            sb.AppendLine("<table><thead><tr><th class=\"right\">#</th><th>Project</th><th class=\"right\">Direct Ref by</th><th class=\"right\">Transitive Dependents</th><th class=\"right\">Direct Refs</th></tr></thead><tbody>");
            int rank = 1;
            foreach (var hub in report.Graph.TopHubs.Take(10))
            {
                sb.AppendLine($"""
<tr>
  <td class="right rank">{rank++}</td>
  <td>{Esc(hub.ProjectName)}</td>
  <td class="right">{hub.IncomingCount}</td>
  <td class="right"><strong>{hub.TransitiveDependentsCount}</strong></td>
  <td class="right muted">{hub.OutgoingCount}</td>
</tr>
""");
            }
            sb.AppendLine("</tbody></table>");
            sb.AppendLine("</details>");
        }

        // Cycle status (collapsed)
        if (report.Graph.Nodes.Count > 0)
        {
            sb.AppendLine("<details class=\"section\"><summary><span class=\"arrow\">▶</span><h2>Dependency Cycle Check</h2></summary>");
            sb.AppendLine("<div class=\"analysis\">");
            if (!report.Graph.CycleDetectionRan)
                sb.AppendLine("<div class=\"muted\">Cycle detection did not run.</div>");
            else if (report.Graph.Cycles.Count == 0)
                sb.AppendLine("<div class=\"green\">No project-reference cycles detected.</div>");
            else
            {
                sb.AppendLine($"<div class=\"red\">{report.Graph.Cycles.Count} cycle(s) detected:</div>");
                foreach (var cycle in report.Graph.Cycles.Take(5))
                    sb.AppendLine($"<div class=\"red\" style=\"margin-top:6px\">{Esc(string.Join(" → ", cycle))} → {Esc(cycle[0])}</div>");
            }
            sb.AppendLine("</div>");
            sb.AppendLine("</details>");
        }

        // Critical path validation status (collapsed — the chain at the top is the primary surface)
        if (report.Graph.Nodes.Count > 0)
        {
            var v = report.CriticalPathValidation;
            var badgeClass = v.Accepted ? "accepted" : "rejected";
            var badgeText = v.Accepted ? "Accepted" : "Rejected";
            sb.AppendLine("<details class=\"section\"><summary><span class=\"arrow\">▶</span><h2>Critical Path Validation</h2></summary>");
            sb.AppendLine("<div class=\"context-grid\">");
            sb.AppendLine($"<div><span class=\"k\">Graph usable:</span> <span class=\"v\">{(v.GraphWasUsable ? "yes" : "no")}</span></div>");
            sb.AppendLine($"<div><span class=\"k\">CPM total:</span> <span class=\"v\">{Esc(ConsoleReportRenderer.FormatDuration(v.ComputedTotal))}</span></div>");
            sb.AppendLine($"<div><span class=\"k\">Wall clock:</span> <span class=\"v\">{Esc(ConsoleReportRenderer.FormatDuration(v.WallClock))}</span></div>");
            sb.AppendLine($"<div><span class=\"k\">Status:</span> <span class=\"v\"><span class=\"badge {badgeClass}\">{badgeText}</span></span></div>");
            sb.AppendLine("</div>");
            sb.AppendLine($"<p class=\"note\">{Esc(v.Reason)}</p>");
            sb.AppendLine("</details>");
        }

        // Category totals
        if (report.CategoryTotals.Count > 0)
        {
            var totalSelfMs = report.CategoryTotals.Sum(kv => kv.Value.TotalMilliseconds);
            if (totalSelfMs > 0)
            {
                sb.AppendLine("<details class=\"section\"><summary><span class=\"arrow\">▶</span><h2>Time by Category</h2></summary>");
                sb.AppendLine("<table><thead><tr><th>Category</th><th class=\"right\">Time</th><th class=\"right\">% of total</th><th>Share</th></tr></thead><tbody>");
                foreach (var kv in report.CategoryTotals.OrderByDescending(x => x.Value))
                {
                    var pct = kv.Value.TotalMilliseconds / totalSelfMs * 100;
                    var barClass = pct > 50 ? "bar-high" : pct > 20 ? "bar-mid" : "bar-low";
                    sb.AppendLine($"""
<tr>
  <td><span class="cat-badge cat-{kv.Key.ToString().ToLowerInvariant()}">{Esc(CategoryLabel(kv.Key))}</span></td>
  <td class="right"><strong>{Esc(ConsoleReportRenderer.FormatDuration(kv.Value))}</strong></td>
  <td class="right muted">{pct:F1}%</td>
  <td><div class="bar-wrap"><div class="bar-fill {barClass}" style="width:{Math.Min(100, pct):F1}%"></div></div></td>
</tr>
""");
                }
                sb.AppendLine("</tbody></table>");
                sb.AppendLine("</details>");
            }
        }

        // Source Generator Cost (Solution-Wide) — aggregates generator cost across all projects
        // so it's visible as a single cost story rather than scattered across per-project rows.
        if (report.AnalyzerReports.Count > 0)
        {
            var totalSelfMsForGens = report.Projects.Sum(p => p.SelfTime.TotalMilliseconds);
            var generatorTotals = report.AnalyzerReports
                .SelectMany(r => r.Generators.Select(g => (Report: r, Entry: g)))
                .GroupBy(x => x.Entry.AssemblyName)
                .Select(g => new
                {
                    Name = g.Key,
                    Total = TimeSpan.FromMilliseconds(g.Sum(x => x.Entry.Time.TotalMilliseconds)),
                    Projects = g.Select(x => x.Report.ProjectName).Distinct().Count(),
                })
                .Where(x => x.Total.TotalMilliseconds >= 100)
                .OrderByDescending(x => x.Total)
                .ToList();

            if (generatorTotals.Count > 0)
            {
                var grandTotal = TimeSpan.FromMilliseconds(generatorTotals.Sum(x => x.Total.TotalMilliseconds));
                var grandPct = totalSelfMsForGens > 0 ? grandTotal.TotalMilliseconds / totalSelfMsForGens * 100 : 0;

                sb.AppendLine("<details class=\"section\"><summary><span class=\"arrow\">▶</span><h2>Source generators (solution-wide)</h2></summary>");
                sb.AppendLine("<p class=\"note\">Generators aggregated across all projects. Total summed across threads — may exceed elapsed time on multi-core.</p>");
                sb.AppendLine("<table><thead><tr><th class=\"right\">#</th><th>Generator</th><th class=\"right\">Total Time</th><th class=\"right\">Projects</th><th class=\"right\">% of Self</th></tr></thead><tbody>");
                int gRank = 1;
                foreach (var g in generatorTotals.Take(15))
                {
                    var pct = totalSelfMsForGens > 0 ? g.Total.TotalMilliseconds / totalSelfMsForGens * 100 : 0;
                    var timeClass = g.Total.TotalSeconds > 10 ? "yellow" : "";
                    sb.AppendLine($"""
<tr>
  <td class="right rank">{gRank++}</td>
  <td><strong>{Esc(g.Name)}</strong></td>
  <td class="right {timeClass}"><strong>{Esc(ConsoleReportRenderer.FormatDuration(g.Total))}</strong></td>
  <td class="right muted">{g.Projects}</td>
  <td class="right muted">{pct:F1}%</td>
</tr>
""");
                }
                sb.AppendLine($"""
<tr>
  <td></td>
  <td class="muted"><em>TOTAL (top {Math.Min(15, generatorTotals.Count)})</em></td>
  <td class="right"><strong>{Esc(ConsoleReportRenderer.FormatDuration(grandTotal))}</strong></td>
  <td></td>
  <td class="right muted">{grandPct:F1}%</td>
</tr>
""");
                sb.AppendLine("</tbody></table>");
                sb.AppendLine("</details>");
            }
        }

        // Analyzer / Generator Reports (per-project, collapsed by default)
        if (report.AnalyzerReports.Count > 0)
        {
            sb.AppendLine("<details class=\"section\"><summary><span class=\"arrow\">▶</span><h2>Per-project analyzer &amp; generator breakdown</h2></summary>");
            sb.AppendLine("<p class=\"note\">Per-project detail from -p:ReportAnalyzer=true. Total summed across threads; may exceed elapsed on multi-core.</p>");
            sb.AppendLine("<table><thead><tr><th>Project</th><th class=\"right\">Csc Wall</th><th class=\"right\">Analyzer Time</th><th class=\"right\">Generator Time</th><th class=\"right\">Analyzers</th><th class=\"right\">Generators</th></tr></thead><tbody>");
            foreach (var ar in report.AnalyzerReports.OrderByDescending(a => a.TotalAnalyzerTime + a.TotalGeneratorTime))
            {
                sb.AppendLine($"""
<tr>
  <td>{Esc(ar.ProjectName)}</td>
  <td class="right">{Esc(ConsoleReportRenderer.FormatDuration(ar.CscWallTime))}</td>
  <td class="right {(ar.TotalAnalyzerTime.TotalSeconds > 1 ? "yellow" : "")}">{Esc(ConsoleReportRenderer.FormatDuration(ar.TotalAnalyzerTime))}</td>
  <td class="right {(ar.TotalGeneratorTime.TotalSeconds > 1 ? "yellow" : "")}">{Esc(ConsoleReportRenderer.FormatDuration(ar.TotalGeneratorTime))}</td>
  <td class="right muted">{ar.Analyzers.Count}</td>
  <td class="right muted">{ar.Generators.Count}</td>
</tr>
""");

                // Drill-down: top entries by time. Filter trivial entries (< 100ms)
                // so the breakdown only shows assemblies that meaningfully contribute.
                var topAnalyzers = ar.Analyzers
                    .Where(e => e.Time.TotalMilliseconds >= 100)
                    .OrderByDescending(e => e.Time)
                    .Take(5)
                    .ToList();
                var topGenerators = ar.Generators
                    .Where(e => e.Time.TotalMilliseconds >= 100)
                    .OrderByDescending(e => e.Time)
                    .Take(5)
                    .ToList();

                if (topAnalyzers.Count > 0)
                {
                    var aList = string.Join(", ",
                        topAnalyzers.Select(e =>
                            $"<strong>{Esc(e.AssemblyName)}</strong> {Esc(ConsoleReportRenderer.FormatDuration(e.Time))} <span class=\"muted\">({e.Percent:F0}%)</span>"));
                    sb.AppendLine($"""
<tr class="drilldown">
  <td colspan="6"><em>top analyzers:</em> {aList}</td>
</tr>
""");
                }
                if (topGenerators.Count > 0)
                {
                    var gList = string.Join(", ",
                        topGenerators.Select(e =>
                            $"<strong>{Esc(e.AssemblyName)}</strong> {Esc(ConsoleReportRenderer.FormatDuration(e.Time))} <span class=\"muted\">({e.Percent:F0}%)</span>"));
                    sb.AppendLine($"""
<tr class="drilldown">
  <td colspan="6"><em>top generators:</em> {gList}</td>
</tr>
""");
                }
            }
            sb.AppendLine("</tbody></table>");
            sb.AppendLine("</details>");
        }

        // Reference overhead (collapsed)
        if (report.ReferenceOverhead is { } overhead)
        {
            sb.AppendLine("<details class=\"section\"><summary><span class=\"arrow\">▶</span><h2>Reference resolution overhead</h2></summary>");
            sb.AppendLine("<p class=\"note\">Aggregated reference work (ResolveAssemblyReferences, ProcessFrameworkReferences, _HandlePackageFileConflicts).</p>");
            sb.AppendLine("<div class=\"context-grid\">");
            sb.AppendLine($"<div><span class=\"k\">Total self time in reference work:</span> <span class=\"v\">{Esc(ConsoleReportRenderer.FormatDuration(overhead.TotalSelfTime))}</span></div>");
            sb.AppendLine($"<div><span class=\"k\">Share of total self time:</span> <span class=\"v\">{overhead.SelfPercent:F1}%</span></div>");
            sb.AppendLine($"<div><span class=\"k\">Projects paying the cost:</span> <span class=\"v\">{overhead.PayingProjectsCount} of {overhead.TotalProjectsCount} ({overhead.PayingProjectsPercent:F0}%)</span></div>");
            sb.AppendLine($"<div><span class=\"k\">Median per paying project:</span> <span class=\"v\">{Esc(ConsoleReportRenderer.FormatDuration(overhead.MedianPerPayingProject))}</span></div>");
            sb.AppendLine("</div>");

            if (overhead.TopProjects.Count > 0)
            {
                sb.AppendLine("<table><thead><tr><th class=\"right\">#</th><th>Project</th><th class=\"right\">Reference Self Time</th></tr></thead><tbody>");
                int r = 1;
                foreach (var p in overhead.TopProjects.Take(10))
                {
                    sb.AppendLine($"""
<tr>
  <td class="right rank">{r++}</td>
  <td>{Esc(p.ProjectName)}</td>
  <td class="right"><strong>{Esc(ConsoleReportRenderer.FormatDuration(p.SelfTime))}</strong></td>
</tr>
""");
                }
                sb.AppendLine("</tbody></table>");
            }
            sb.AppendLine("</details>");
        }

        // Warnings vs Build Cost
        var warningProjects = report.Projects
            .Where(p => p.WarningCount > 0)
            .OrderByDescending(p => p.WarningCount)
            .ToList();
        if (warningProjects.Count > 0 && report.WarningCount >= 5)
        {
            var totalAttributedWarn = warningProjects.Sum(p => p.WarningCount);

            sb.AppendLine("<details class=\"section\"><summary><span class=\"arrow\">▶</span><h2>Warnings by Project</h2></summary>");
            sb.AppendLine("<p class=\"note\">Top warning sources alongside their self time. Warnings on expensive projects are higher leverage to fix — they live in code that the build is already paying to compile. Warnings on cheap projects are quality issues but rarely a cost story.</p>");
            sb.AppendLine("<table><thead><tr><th class=\"right\">#</th><th>Project</th><th class=\"right\">Warnings</th><th class=\"right\">% of Attributed</th><th class=\"right\">Self Time</th><th class=\"right\">% Self</th></tr></thead><tbody>");
            int wr = 1;
            foreach (var p in warningProjects.Take(10))
            {
                var sharePct = totalAttributedWarn > 0 ? (double)p.WarningCount / totalAttributedWarn * 100 : 0;
                var costClass = p.SelfPercent >= 5 ? "yellow" : "muted";
                sb.AppendLine($"""
<tr>
  <td class="right rank">{wr++}</td>
  <td>{Esc(p.Name)}</td>
  <td class="right"><strong class="yellow">{p.WarningCount}</strong></td>
  <td class="right muted">{sharePct:F0}%</td>
  <td class="right {costClass}">{Esc(ConsoleReportRenderer.FormatDuration(p.SelfTime))}</td>
  <td class="right muted">{p.SelfPercent:F1}%</td>
</tr>
""");
            }
            sb.AppendLine("</tbody></table>");

            if (report.UnattributedWarningCount > 0)
                sb.AppendLine($"<p class=\"note\">{report.UnattributedWarningCount} additional warning(s) could not be attributed to a specific project (typically SDK/MSBuild infrastructure warnings).</p>");
            sb.AppendLine("</details>");
        }

        // Warnings by Category
        if (report.WarningsByCode.Count > 0)
        {
            var byPrefix = report.WarningsByCode
                .GroupBy(t => t.Prefix)
                .Select(g => new
                {
                    Prefix = g.Key,
                    Count = g.Sum(t => t.Count),
                    TopCodes = g.OrderByDescending(t => t.Count).Take(3).ToList(),
                })
                .OrderByDescending(x => x.Count)
                .ToList();

            sb.AppendLine("<details class=\"section\"><summary><span class=\"arrow\">▶</span><h2>Warning categories</h2></summary>");
            sb.AppendLine("<p class=\"note\">Grouped by code prefix. Top 3 codes per category so you know what to target.</p>");
            sb.AppendLine("<table><thead><tr><th>Category</th><th class=\"right\">Count</th><th>Top codes</th></tr></thead><tbody>");
            foreach (var g in byPrefix)
            {
                var top = string.Join(", ", g.TopCodes.Select(c => $"<strong>{Esc(c.Code)}</strong> <span class=\"muted\">({c.Count})</span>"));
                sb.AppendLine($"""
<tr>
  <td>{Esc(PrefixLabel(g.Prefix))}</td>
  <td class="right"><strong class="yellow">{g.Count}</strong></td>
  <td>{top}</td>
</tr>
""");
            }
            sb.AppendLine("</tbody></table>");
            sb.AppendLine("</details>");
        }

        // Removed per spec:
        //   Span vs Self Outliers — structural observation, not a specific bottleneck.
        //   Project Count Tax Indicators — patterns to investigate, not answers.
        //   Build Timeline — decorative chart; Top Consumers + Blocking Chain cover the signal.
        //   Top Projects by Self Time — duplicated by Top Consumers above.
        //   Top Targets by Self Time — too far from actionable; tasks belong under their project.
        //   Top Tasks by Self Time — merged into Per-Project Breakdown.
        //   Potentially Custom Targets — heuristic with too many false positives to promote.

        sb.AppendLine("<footer style=\"margin-top:40px; color:var(--muted); font-size:0.78rem\">Generated by BuildTimeAnalyzer on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ". <a href=\"#\" style=\"color:var(--muted)\" onclick=\"document.querySelectorAll('details').forEach(d=>d.open=true);return false\">Expand all</a> · <a href=\"#\" style=\"color:var(--muted)\" onclick=\"document.querySelectorAll('details').forEach(d=>d.open=false);return false\">Collapse all</a></footer>");
        sb.AppendLine("</body></html>");

        return sb.ToString();
    }

    private static string FormatCategoryComposition(ProjectTiming p)
    {
        // Normalise against the sum of the breakdown so percentages always add to 100%.
        var totalMs = p.CategoryBreakdown.Sum(kv => kv.Value.TotalMilliseconds);
        if (totalMs <= 0) return "";

        var parts = p.CategoryBreakdown
            .Where(kv => kv.Value.TotalMilliseconds > 0)
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => $"{CategoryLabel(kv.Key)} {kv.Value.TotalMilliseconds / totalMs * 100:F0}%");

        return string.Join(", ", parts);
    }

    private static string FormatTopWarningCategoriesHtml(BuildReport report)
    {
        if (report.WarningsByCode.Count == 0) return "";
        var byPrefix = report.WarningsByCode
            .GroupBy(t => t.Prefix)
            .Select(g => new { Prefix = g.Key, Count = g.Sum(t => t.Count) })
            .OrderByDescending(x => x.Count)
            .Take(3)
            .ToList();
        if (byPrefix.Count == 0) return "";
        var text = string.Join(" · ", byPrefix.Select(x => $"{Esc(x.Prefix)} {x.Count}"));
        return $"<div class=\"sub\">{text}</div>";
    }

    // Cap at 5 per the spec — "Top bottlenecks (3–5 findings maximum)".
    private const int MaxBottlenecks = 5;
    // "Blocking chain — 10 nodes max."
    private const int MaxBlockingChainNodes = 10;
    // "Top time consumers table — 8 projects max."
    private const int MaxTopConsumers = 8;
    // "What to inspect next — 5 items max."
    private const int MaxInspectNext = 5;

    /// <summary>
    /// Top bottlenecks section — strict three-line format per finding:
    /// title with the measured number, one sentence of why, and an inspect line.
    /// </summary>
    private static void AppendTopBottlenecks(StringBuilder sb, BuildAnalysis? analysis)
    {
        if (analysis is null || analysis.Findings.Count == 0) return;

        var ranked = analysis.Findings
            .OrderBy(f => f.Severity switch
            {
                FindingSeverity.Critical => 0,
                FindingSeverity.Warning => 1,
                _ => 2,
            })
            .ThenByDescending(f => f.UpperBoundImpactPercent ?? 0)
            .Take(MaxBottlenecks)
            .ToList();

        sb.AppendLine("<h2>Top bottlenecks</h2>");
        foreach (var f in ranked)
        {
            var sevClass = f.Severity == FindingSeverity.Critical ? "sev-critical" : "";
            // Body = finding's Measured, minus any parts that restate the title. Kept short.
            var why = string.IsNullOrEmpty(f.Measured) ? "" : $"<div class=\"b-why\">{Esc(f.Measured)}</div>";
            sb.AppendLine($"""
<div class="bottleneck {sevClass}">
  <div class="b-title">{Esc(f.Title)}</div>
  {why}
  <div class="b-inspect">→ Inspect: {Esc(f.InvestigationSuggestion)}</div>
</div>
""");
        }
    }

    /// <summary>
    /// Blocking chain — the dependent chain that most limits how quickly the build can finish.
    /// One-phrase reason per node (dominant category), no disclaimers in the main view.
    /// </summary>
    private static void AppendBlockingChain(StringBuilder sb, BuildReport report)
    {
        if (report.CriticalPath.Count == 0 || report.CriticalPathTotal <= TimeSpan.Zero) return;

        var wallMs = report.TotalDuration.TotalMilliseconds;
        var pctOfElapsed = wallMs > 0 ? report.CriticalPathTotal.TotalMilliseconds / wallMs * 100 : 0;

        sb.AppendLine("<h2>Blocking chain</h2>");
        sb.AppendLine("<p class=\"note\">The chain of dependent work that most limits how quickly the build can finish. Estimate — see Critical Path Validation below for confidence.</p>");
        sb.AppendLine("<ol class=\"chain-list\">");
        foreach (var p in report.CriticalPath.Take(MaxBlockingChainNodes))
        {
            var reason = DominantCategoryPhrase(p);
            sb.AppendLine($"<li><strong>{Esc(p.Name)}</strong> — {Esc(ConsoleReportRenderer.FormatDuration(p.SelfTime))} ({p.SelfPercent:F0}%)<span class=\"muted\"> — {Esc(reason)}</span></li>");
        }
        sb.AppendLine("</ol>");
        sb.AppendLine($"<p class=\"note\">Chain total: {Esc(ConsoleReportRenderer.FormatDuration(report.CriticalPathTotal))} of {Esc(ConsoleReportRenderer.FormatDuration(report.TotalDuration))} elapsed ({pctOfElapsed:F0}%).</p>");
    }

    private static string DominantCategoryPhrase(ProjectTiming p)
    {
        if (p.CategoryBreakdown.Count == 0)
            return p.KindHeuristic switch
            {
                ProjectKind.Test => "test project",
                ProjectKind.Benchmark => "benchmark project",
                _ => "—",
            };
        var topCat = p.CategoryBreakdown.OrderByDescending(kv => kv.Value).First().Key;
        return topCat switch
        {
            TargetCategory.Compile => "compile",
            TargetCategory.References => "reference resolution",
            TargetCategory.SourceGen => "source generators",
            TargetCategory.StaticWebAssets => "static web assets",
            TargetCategory.Copy => "output copy",
            TargetCategory.Restore => "restore",
            _ => "other",
        };
    }

    /// <summary>
    /// Top time consumers — table of top 8 projects by time-in-this-project-itself.
    /// </summary>
    private static void AppendTopConsumers(StringBuilder sb, BuildReport report)
    {
        if (report.Projects.Count == 0) return;

        var totalSelfMs = report.Projects.Sum(p => p.SelfTime.TotalMilliseconds);

        sb.AppendLine("<h2>Top time consumers</h2>");
        sb.AppendLine("<p class=\"note\">Time each project spends on its own work, not including time spent waiting on referenced projects.</p>");
        sb.AppendLine("<table><thead><tr><th>Project</th><th class=\"right\">Time</th><th class=\"right\">% of total</th><th>Dominant</th></tr></thead><tbody>");
        foreach (var p in report.Projects.Take(MaxTopConsumers))
        {
            var pct = totalSelfMs > 0 ? p.SelfTime.TotalMilliseconds / totalSelfMs * 100 : 0;
            sb.AppendLine($"""
<tr>
  <td>{Esc(p.Name)}</td>
  <td class="right"><strong>{Esc(ConsoleReportRenderer.FormatDuration(p.SelfTime))}</strong></td>
  <td class="right muted">{pct:F1}%</td>
  <td class="muted">{Esc(DominantCategoryPhrase(p))}</td>
</tr>
""");
        }
        sb.AppendLine("</tbody></table>");
    }

    /// <summary>
    /// Inspect next — short ranked list derived from the top findings' inspect targets.
    /// Up to 5 entries, one sentence each, each naming a specific artifact.
    /// </summary>
    private static void AppendInspectNext(StringBuilder sb, BuildAnalysis? analysis)
    {
        if (analysis is null || analysis.Findings.Count == 0) return;

        var ranked = analysis.Findings
            .OrderBy(f => f.Severity switch
            {
                FindingSeverity.Critical => 0,
                FindingSeverity.Warning => 1,
                _ => 2,
            })
            .ThenByDescending(f => f.UpperBoundImpactPercent ?? 0)
            .Select(f => f.InvestigationSuggestion)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .Take(MaxInspectNext)
            .ToList();
        if (ranked.Count == 0) return;

        sb.AppendLine("<h2>Inspect next</h2>");
        sb.AppendLine("<ol class=\"inspect-list\">");
        foreach (var s in ranked)
            sb.AppendLine($"<li>{Esc(s)}</li>");
        sb.AppendLine("</ol>");
    }

    /// <summary>
    /// Per-project breakdown — only top 3 projects; max 5 tasks each; excludes cascade targets
    /// whose time is work done in other projects (not the project they appear under).
    /// </summary>
    private static void AppendPerProjectBreakdown(StringBuilder sb, BuildReport report)
    {
        if (report.Projects.Count == 0) return;

        sb.AppendLine("<h2>Per-project breakdown</h2>");
        sb.AppendLine("<p class=\"note\">Top 3 projects by time. Tasks that report aggregated work from other projects (clean cascade, reference framework lookup) are excluded — they'd attribute the wrong cost.</p>");

        foreach (var p in report.Projects.Take(3))
        {
            sb.AppendLine($"<h3 style=\"margin-top:16px; font-size:1rem\">{Esc(p.Name)} — {Esc(ConsoleReportRenderer.FormatDuration(p.SelfTime))} ({p.SelfPercent:F1}%)</h3>");

            var tasks = report.TopTasks
                .Where(t => string.Equals(t.ProjectName, p.Name, StringComparison.OrdinalIgnoreCase))
                .Where(t => !IsCascadeTarget(t.TargetName))
                .Take(5)
                .ToList();
            if (tasks.Count > 0)
            {
                sb.AppendLine("<table style=\"margin-bottom:6px\"><thead><tr><th>Task</th><th>Target</th><th class=\"right\">Time</th></tr></thead><tbody>");
                foreach (var t in tasks)
                    sb.AppendLine($"<tr><td><strong>{Esc(t.TaskName)}</strong></td><td class=\"muted\">{Esc(t.TargetName)}</td><td class=\"right\">{Esc(ConsoleReportRenderer.FormatDuration(t.SelfTime))}</td></tr>");
                sb.AppendLine("</tbody></table>");
            }

            var diagnosis = report.ProjectDiagnoses.FirstOrDefault(d =>
                string.Equals(d.ProjectName, p.Name, StringComparison.OrdinalIgnoreCase));
            if (diagnosis?.Packages is not null)
                AppendPackagePanel(sb, diagnosis.Packages);
        }
    }

    private static readonly HashSet<string> CascadeTargetNames = new(StringComparer.Ordinal)
    {
        // These targets execute work across multiple projects; showing them as a single project's
        // slowest task would be factually misleading (the time is work done in the referenced projects).
        "CleanReferencedProjects",
        "_GetProjectReferenceTargetFrameworkProperties",
        "_GetCopyToOutputDirectoryItemsFromTransitiveProjectReferences",
        "_GetProjectReferenceTargetFrameworkPropertiesFromSolution",
    };

    private static bool IsCascadeTarget(string targetName) =>
        CascadeTargetNames.Contains(targetName);

    private static void AppendPackagePanel(StringBuilder sb, ProjectPackages? packages)
    {
        if (packages is null || packages.Quality == ProjectDataQuality.NoCsproj) return;
        if (packages.DirectPackages.Count == 0 && packages.TransitivePackages.Count == 0 && packages.ProjectReferences.Count == 0)
            return;

        var quality = packages.Quality == ProjectDataQuality.CsprojOnly
            ? "<span class=\"muted\" title=\"project.assets.json not found — transitive packages unavailable\">⚠ direct only</span>"
            : "";
        var summaryText = $"Package References (direct: {packages.DirectPackages.Count}, transitive: {packages.TransitivePackages.Count}, project: {packages.ProjectReferences.Count}) {quality}";

        sb.AppendLine($"""
<details style="margin-top:10px">
  <summary class="muted" style="cursor:pointer; font-size:0.85rem">{summaryText}</summary>
  <div style="margin-top:8px; font-size:0.85rem">
""");

        if (packages.DirectPackages.Count > 0)
        {
            sb.AppendLine("<table style=\"margin-bottom:6px\"><thead><tr><th>Direct Package</th><th>Version</th><th></th></tr></thead><tbody>");
            foreach (var pkg in packages.DirectPackages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
            {
                var heavyBadge = pkg.IsKnownHeavy ? " <span class=\"cat-badge cat-uncategorized\" title=\"Known heavy package (large transitive graph or source generators)\">heavy</span>" : "";
                sb.AppendLine($"<tr><td><strong>{Esc(pkg.Id)}</strong>{heavyBadge}</td><td class=\"muted\">{Esc(pkg.Version ?? "")}</td><td></td></tr>");
            }
            sb.AppendLine("</tbody></table>");
        }

        if (packages.ProjectReferences.Count > 0)
        {
            var refs = string.Join(", ", packages.ProjectReferences.Select(Esc));
            sb.AppendLine($"<div style=\"margin-bottom:6px\"><span class=\"muted\">Project references:</span> {refs}</div>");
        }

        if (packages.TransitivePackages.Count > 0)
        {
            var shown = packages.TransitivePackages
                .Where(t => t.IsKnownHeavy || string.IsNullOrEmpty(t.ParentPackage) == false)
                .OrderByDescending(t => t.IsKnownHeavy)
                .ThenBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
                .Take(25)
                .ToList();

            if (shown.Count > 0)
            {
                sb.AppendLine("<details><summary class=\"muted\" style=\"cursor:pointer\">Transitive (top " + shown.Count + " of " + packages.TransitivePackages.Count + ")</summary>");
                sb.AppendLine("<table style=\"margin-top:6px\"><thead><tr><th>Transitive</th><th>Version</th><th>Pulled In By</th></tr></thead><tbody>");
                foreach (var pkg in shown)
                {
                    var heavyBadge = pkg.IsKnownHeavy ? " <span class=\"cat-badge cat-uncategorized\">heavy</span>" : "";
                    var parent = pkg.ParentPackage is null ? "<span class=\"muted\">—</span>" : $"<span class=\"muted\">← {Esc(pkg.ParentPackage)}</span>";
                    sb.AppendLine($"<tr><td class=\"muted\">{Esc(pkg.Id)}{heavyBadge}</td><td class=\"muted\">{Esc(pkg.Version ?? "")}</td><td>{parent}</td></tr>");
                }
                sb.AppendLine("</tbody></table>");
                sb.AppendLine("</details>");
            }
        }

        sb.AppendLine("  </div>");
        sb.AppendLine("</details>");
    }

    private static List<(string Key, string Value)> BuildContextRows(BuildReport report)
    {
        var rows = new List<(string, string)>();
        var ctx = report.Context;
        if (ctx.Configuration is not null) rows.Add(("Configuration", ctx.Configuration));
        if (ctx.BuildMode is not null) rows.Add(("Build Mode", ctx.BuildMode));
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

    private static string PrefixLabel(string prefix) => prefix switch
    {
        "CS" => "CS — C# compiler",
        "CA" => "CA — Roslyn analyzers",
        "IDE" => "IDE — IDE analyzers",
        "NETSDK" => "NETSDK — .NET SDK",
        "NU" => "NU — NuGet",
        "MSB" => "MSB — MSBuild",
        "SA" => "SA — StyleCop",
        "VB" => "VB — Visual Basic compiler",
        _ => prefix,
    };

    private static string Esc(string? s) =>
        (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
