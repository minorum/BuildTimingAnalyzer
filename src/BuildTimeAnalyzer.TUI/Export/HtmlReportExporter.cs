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
  body { background: var(--bg); color: var(--text); font-family: 'Segoe UI', system-ui, sans-serif; padding: 32px; max-width: 1400px; margin: 0 auto; }
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
  <div class="stat">
    <div class="label">Warnings</div>
    <div class="value muted">{{report.WarningCount}} total</div>
    <div class="sub">{{report.AttributedWarningCount}} attributed &middot; {{report.UnattributedWarningCount}} unattributed</div>
    {{FormatTopWarningCategoriesHtml(report)}}
  </div>
</div>
""");

        // ── Most actionable content surfaces first ──
        AppendTopSuspects(sb, analysis);
        AppendAnalysisSection(sb, analysis);
        AppendWhyIsThisSlow(sb, report);

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

        // Graph health
        if (report.Graph.Nodes.Count > 0)
        {
            var h = report.Graph.Health;
            sb.AppendLine("<h2>Dependency Graph Health</h2>");
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
        }

        // Dependency hubs
        if (report.Graph.TopHubs.Count > 0)
        {
            sb.AppendLine("<h2>Dependency Hubs</h2>");
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
        }

        // Cycle status — always rendered when the graph has nodes
        if (report.Graph.Nodes.Count > 0)
        {
            sb.AppendLine("<h2>Dependency Cycle Check</h2>");
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
        }

        // Critical path validation status — always rendered when the graph has nodes
        if (report.Graph.Nodes.Count > 0)
        {
            var v = report.CriticalPathValidation;
            var badgeClass = v.Accepted ? "accepted" : "rejected";
            var badgeText = v.Accepted ? "Accepted" : "Rejected";
            sb.AppendLine("<h2>Critical Path Validation</h2>");
            sb.AppendLine("<div class=\"context-grid\">");
            sb.AppendLine($"<div><span class=\"k\">Graph usable:</span> <span class=\"v\">{(v.GraphWasUsable ? "yes" : "no")}</span></div>");
            sb.AppendLine($"<div><span class=\"k\">CPM total:</span> <span class=\"v\">{Esc(ConsoleReportRenderer.FormatDuration(v.ComputedTotal))}</span></div>");
            sb.AppendLine($"<div><span class=\"k\">Wall clock:</span> <span class=\"v\">{Esc(ConsoleReportRenderer.FormatDuration(v.WallClock))}</span></div>");
            sb.AppendLine($"<div><span class=\"k\">Status:</span> <span class=\"v\"><span class=\"badge {badgeClass}\">{badgeText}</span></span></div>");
            sb.AppendLine("</div>");
            sb.AppendLine($"<p class=\"note\">{Esc(v.Reason)}</p>");
        }

        // Critical path — only when validation passed
        if (hasCriticalPath)
        {
            sb.AppendLine("<h2>Critical Path Estimate</h2>");
            sb.AppendLine("<p class=\"note\">Model-based, not a scheduler trace. Derived from the observed ProjectReference DAG and measured self times. Accuracy depends on whether dependency extraction and exclusive timing are capturing your build correctly.</p>");
            sb.AppendLine("<table><thead><tr><th class=\"right\">Step</th><th>Project</th><th>Kind (heuristic)</th><th class=\"right\">Self Time</th><th class=\"right\">% Self</th></tr></thead><tbody>");
            int step = 1;
            foreach (var p in report.CriticalPath)
            {
                sb.AppendLine($"""
<tr class="critical-path">
  <td class="right rank">{step++}</td>
  <td class="red"><strong>{Esc(p.Name)}</strong></td>
  <td class="muted">{Esc(ProjectKindHeuristic.Label(p.KindHeuristic))}</td>
  <td class="right"><strong>{Esc(ConsoleReportRenderer.FormatDuration(p.SelfTime))}</strong></td>
  <td class="right muted">{p.SelfPercent:F1}%</td>
</tr>
""");
            }
            sb.AppendLine("</tbody></table>");
            sb.AppendLine($"<p class=\"note\">Path total: {Esc(ConsoleReportRenderer.FormatDuration(report.CriticalPathTotal))} of {Esc(ConsoleReportRenderer.FormatDuration(report.TotalDuration))} wall clock.</p>");

            var testBenchOnPath = report.CriticalPath
                .Where(p => p.KindHeuristic == ProjectKind.Test || p.KindHeuristic == ProjectKind.Benchmark)
                .ToList();
            if (testBenchOnPath.Count > 0)
            {
                var names = string.Join(", ", testBenchOnPath.Select(p => Esc(p.Name)));
                sb.AppendLine($"<p class=\"note\" style=\"color:var(--yellow)\">Note: {testBenchOnPath.Count} test/benchmark project(s) on this path ({names}). If your goal is faster <em>production</em> builds (CI / dev inner loop without running tests), exclude these from the optimisation target — their cost only matters when they actually run.</p>");
            }
        }

        // Category totals
        if (report.CategoryTotals.Count > 0)
        {
            var totalSelfMs = report.CategoryTotals.Sum(kv => kv.Value.TotalMilliseconds);
            if (totalSelfMs > 0)
            {
                sb.AppendLine("<h2>Self Time by Category</h2>");
                sb.AppendLine("<table><thead><tr><th>Category</th><th class=\"right\">Self Time</th><th class=\"right\">% Self</th><th>Share</th></tr></thead><tbody>");
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

                sb.AppendLine("<h2>Source Generator Cost (Solution-Wide)</h2>");
                sb.AppendLine("<p class=\"note\">Generators aggregated across all projects. Cost is CPU-summed — a generator running in 20 projects in parallel contributes up to 20x its per-project time. The <em>% of Self</em> column compares against total project self time, so values above ~5% are meaningful.</p>");
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
                sb.AppendLine("<p class=\"note\"><strong>Known SDK-default generators:</strong> <code>Microsoft.Interop.ComInterfaceGenerator</code> and <code>Microsoft.Interop.JSImportGenerator</code> ship with the .NET SDK and run in every project regardless of whether any <code>[GeneratedComInterface]</code> or <code>[JSImport]</code> attributes are present — they cannot be disabled without modifying SDK targets. <code>Microsoft.Gen.Metrics</code> and <code>Microsoft.Gen.Logging</code> come from <code>Microsoft.Extensions.Telemetry</code>; projects that do not use <code>[LoggerMessage]</code> or <code>[Meter]</code> source generation should not reference that package.</p>");
            }
        }

        // Analyzer / Generator Reports (per-project, collapsed by default)
        if (report.AnalyzerReports.Count > 0)
        {
            sb.AppendLine("<h2>Per-Project Analyzer &amp; Generator Breakdown</h2>");
            sb.AppendLine("<p class=\"note\">Per-project detail from -p:ReportAnalyzer=true. Times are CPU-summed (may exceed Csc wall time on multi-core). Treat them as relative cost signals between projects, not absolute compiler-proper measurements.</p>");
            sb.AppendLine("<details><summary class=\"muted\" style=\"cursor:pointer; padding:8px 0\">Show per-project breakdown</summary>");
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

        // Reference overhead
        if (report.ReferenceOverhead is { } overhead)
        {
            sb.AppendLine("<h2>Reference Overhead</h2>");
            sb.AppendLine("<p class=\"note\">Aggregated reference-related work across the solution (ResolveAssemblyReferences, ProcessFrameworkReferences, _HandlePackageFileConflicts, etc.).</p>");
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
        }

        // Warnings vs Build Cost
        var warningProjects = report.Projects
            .Where(p => p.WarningCount > 0)
            .OrderByDescending(p => p.WarningCount)
            .ToList();
        if (warningProjects.Count > 0 && report.WarningCount >= 5)
        {
            var totalAttributedWarn = warningProjects.Sum(p => p.WarningCount);

            sb.AppendLine("<h2>Warnings vs Build Cost</h2>");
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

            sb.AppendLine("<h2>Warning Categories</h2>");
            sb.AppendLine("<p class=\"note\">Grouped by code prefix. CS = C# compiler (nullable, deprecation, etc.), CA = Roslyn analyzers, NETSDK = SDK, NU = NuGet, MSB = MSBuild. The full code of the top 3 sources in each category is shown so you know what to target first.</p>");
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
        }

        // Span outliers
        if (report.SpanOutliers.Count > 0)
        {
            sb.AppendLine("<h2>Span vs Self Outliers</h2>");
            sb.AppendLine("<p class=\"note\">Rule: Span ≥ 5s, Span/SelfTime ≥ 5x, Span − SelfTime ≥ 3s. The pattern has several possible causes (dependency waiting, SDK orchestration, reference work, static-web-assets, test/benchmark shape, incremental effects) — timing alone does not identify which. Cross-reference with Dependency Hubs and category composition.</p>");
            sb.AppendLine("<table><thead><tr><th class=\"right\">#</th><th>Project</th><th>Kind (heuristic)</th><th class=\"right\">Span</th><th class=\"right\">Self Time</th><th class=\"right\">Ratio</th></tr></thead><tbody>");
            int r = 1;
            foreach (var p in report.SpanOutliers.Take(15))
            {
                var ratio = p.SelfTime.TotalMilliseconds > 0 ? p.Span.TotalMilliseconds / p.SelfTime.TotalMilliseconds : 0;
                sb.AppendLine($"""
<tr>
  <td class="right rank">{r++}</td>
  <td class="yellow">{Esc(p.Name)}</td>
  <td class="muted">{Esc(ProjectKindHeuristic.Label(p.KindHeuristic))}</td>
  <td class="right"><strong>{Esc(ConsoleReportRenderer.FormatDuration(p.Span))}</strong></td>
  <td class="right muted">{Esc(ConsoleReportRenderer.FormatDuration(p.SelfTime))}</td>
  <td class="right">{ratio:F1}x</td>
</tr>
""");
            }
            sb.AppendLine("</tbody></table>");
        }

        // Project Count Tax
        if (report.Projects.Count > 0)
        {
            var tax = report.ProjectCountTax;
            sb.AppendLine("<h2>Project Count Tax Indicators</h2>");
            sb.AppendLine("<p class=\"note\">Candidate signals that the solution pays graph/orchestration cost disproportionate to local work. Each indicator is a pattern to investigate, not proof of a problem.</p>");
            sb.AppendLine("<div class=\"context-grid\">");
            sb.AppendLine($"<div><span class=\"k\">References &gt; compile:</span> <span class=\"v\">{tax.ReferencesExceedCompileCount} of {tax.TotalProjects}</span></div>");
            sb.AppendLine($"<div><span class=\"k\">References are the majority of self time:</span> <span class=\"v\">{tax.ReferencesMajorityCount} of {tax.TotalProjects}</span></div>");
            sb.AppendLine($"<div><span class=\"k\">Tiny self / huge span:</span> <span class=\"v\">{tax.TinySelfHugeSpanCount} of {tax.TotalProjects}</span></div>");
            sb.AppendLine("</div>");

            if (tax.PerKindStats.Count > 0)
            {
                sb.AppendLine("<p class=\"note\">Per-kind medians (name-based heuristic — not authoritative).</p>");
                sb.AppendLine("<table><thead><tr><th>Kind</th><th class=\"right\">Count</th><th class=\"right\">Median Self</th><th class=\"right\">Median Span</th><th class=\"right\">Median Span/Self</th></tr></thead><tbody>");
                foreach (var s in tax.PerKindStats)
                {
                    var ratio = s.MedianSpanToSelfRatio > 0 ? $"{s.MedianSpanToSelfRatio:F1}x" : "n/a";
                    sb.AppendLine($"""
<tr>
  <td class="muted">{Esc(ProjectKindHeuristic.Label(s.Kind))}</td>
  <td class="right">{s.Count}</td>
  <td class="right">{Esc(ConsoleReportRenderer.FormatDuration(s.MedianSelfTime))}</td>
  <td class="right">{Esc(ConsoleReportRenderer.FormatDuration(s.MedianSpan))}</td>
  <td class="right">{ratio}</td>
</tr>
""");
                }
                sb.AppendLine("</tbody></table>");
            }
        }

        // Timeline
        if (report.Projects.Count > 0 && report.TotalDuration.TotalMilliseconds > 0)
        {
            sb.AppendLine("<h2>Build Timeline</h2>");
            var subtitle = hasCriticalPath
                ? "Wall-clock Span per project. Red bars are on the critical path estimate; grey bars are not."
                : "Wall-clock Span per project.";
            sb.AppendLine($"<p class=\"note\">{subtitle}</p>");
            sb.AppendLine("<div class=\"analysis\" style=\"overflow-x:auto\">");
            var totalMs = report.TotalDuration.TotalMilliseconds;
            var timelineProjects = report.Projects.OrderBy(p => p.StartOffset).ToList();
            foreach (var p in timelineProjects)
            {
                var leftPct = p.StartOffset.TotalMilliseconds / totalMs * 100;
                var widthPct = Math.Max(0.5, (p.EndOffset - p.StartOffset).TotalMilliseconds / totalMs * 100);
                var onCritical = hasCriticalPath && criticalSet.Contains(p.FullPath);
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
            sb.AppendLine("<div class=\"timeline-row\" style=\"margin-top:6px; color:var(--muted); font-size:0.75rem\">");
            sb.AppendLine("<span class=\"name\"></span>");
            sb.AppendLine("<div class=\"track\" style=\"background:transparent; display:flex; justify-content:space-between\">");
            for (int i = 0; i <= 4; i++)
                sb.AppendLine($"<span>{Esc(ConsoleReportRenderer.FormatDuration(TimeSpan.FromMilliseconds(totalMs * i / 4)))}</span>");
            sb.AppendLine("</div>");
            sb.AppendLine("<span class=\"dur\"></span>");
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
        }

        // Projects table with drill-down + category composition
        sb.AppendLine("<h2>Top Projects by Self Time</h2>");
        sb.AppendLine("<p class=\"note\">Self Time = genuinely exclusive work. Span = wall-clock first-to-last activity (display only).</p>");
        sb.AppendLine("<table><thead><tr><th class=\"right\">#</th><th>Project</th><th class=\"right\">Self Time</th><th class=\"right\">Span</th><th class=\"right\">% Self</th><th>Share</th><th class=\"right\">Errors</th><th class=\"right\">Warnings</th></tr></thead><tbody>");

        int projRank = 1;
        foreach (var p in report.Projects.Take(topN))
        {
            var barClass = p.SelfPercent > 50 ? "bar-high" : p.SelfPercent > 20 ? "bar-mid" : "bar-low";
            var barPct = Math.Min(100, p.SelfPercent);
            var icon = p.Succeeded ? "" : "<span class=\"red\">! </span>";
            var errCell = p.ErrorCount > 0 ? $"<span class=\"red\">{p.ErrorCount}</span>" : "<span class=\"muted\">0</span>";
            var warnCell = p.WarningCount > 0 ? $"<span class=\"yellow\">{p.WarningCount}</span>" : "<span class=\"muted\">0</span>";
            var rowClass = (hasCriticalPath && criticalSet.Contains(p.FullPath)) ? " class=\"critical-path\"" : "";
            var kindBadge = p.KindHeuristic != ProjectKind.Other
                ? $"<span class=\"kind-badge\">{Esc(ProjectKindHeuristic.Label(p.KindHeuristic))}</span>"
                : "";

            sb.AppendLine($"""
<tr{rowClass}>
  <td class="right rank">{projRank++}</td>
  <td>{icon}{Esc(p.Name)}{kindBadge}</td>
  <td class="right"><strong>{Esc(ConsoleReportRenderer.FormatDuration(p.SelfTime))}</strong></td>
  <td class="right muted">{Esc(ConsoleReportRenderer.FormatDuration(p.Span))}</td>
  <td class="right muted">{p.SelfPercent:F1}%</td>
  <td><div class="bar-wrap"><div class="bar-fill {barClass}" style="width:{barPct:F1}%"></div></div></td>
  <td class="right">{errCell}</td>
  <td class="right">{warnCell}</td>
</tr>
""");

            // Category composition row
            if (p.CategoryBreakdown.Count > 0)
            {
                var composition = FormatCategoryComposition(p);
                if (composition.Length > 0)
                {
                    sb.AppendLine($"""
<tr class="composition">
  <td></td>
  <td colspan="7"><em>composition:</em> {Esc(composition)}</td>
</tr>
""");
                }
            }

            // Target drill-down
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

            int r = 1;
            foreach (var t in report.TopTargets)
            {
                sb.AppendLine($"""
<tr>
  <td class="right rank">{r++}</td>
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

        // Top Tasks (task-level breakdown)
        if (report.TopTasks.Count > 0)
        {
            sb.AppendLine("<h2>Top Tasks by Self Time</h2>");
            sb.AppendLine("<p class=\"note\">Task-level drill-down below targets. Shows which tasks inside targets are doing the work.</p>");
            sb.AppendLine("<table><thead><tr><th class=\"right\">#</th><th>Task</th><th>Target</th><th>Project</th><th class=\"right\">Self Time</th></tr></thead><tbody>");
            int taskRank = 1;
            foreach (var t in report.TopTasks.Take(20))
            {
                sb.AppendLine($"""
<tr>
  <td class="right rank">{taskRank++}</td>
  <td><strong>{Esc(t.TaskName)}</strong></td>
  <td class="muted">{Esc(t.TargetName)}</td>
  <td class="muted">{Esc(t.ProjectName)}</td>
  <td class="right"><strong>{Esc(ConsoleReportRenderer.FormatDuration(t.SelfTime))}</strong></td>
</tr>
""");
            }
            sb.AppendLine("</tbody></table>");
        }

        // Potentially custom targets
        var customTargets = report.PotentiallyCustomTargets.Where(t => t.SelfTime.TotalSeconds >= 1).Take(5).ToList();
        if (customTargets.Count > 0)
        {
            sb.AppendLine("<h2>Potentially Custom Targets</h2>");
            sb.AppendLine("<p class=\"note\">Targets that did not match any known SDK pattern and took at least 1s. Often actionable optimization hotspots.</p>");
            sb.AppendLine("<table><thead><tr><th class=\"right\">#</th><th>Target</th><th>Project</th><th class=\"right\">Self Time</th></tr></thead><tbody>");
            int r = 1;
            foreach (var t in customTargets)
            {
                sb.AppendLine($"""
<tr>
  <td class="right rank">{r++}</td>
  <td class="orange"><strong>{Esc(t.Name)}</strong></td>
  <td class="muted">{Esc(t.ProjectName)}</td>
  <td class="right"><strong>{Esc(ConsoleReportRenderer.FormatDuration(t.SelfTime))}</strong></td>
</tr>
""");
            }
            sb.AppendLine("</tbody></table>");
        }

        sb.AppendLine($"<footer>Generated by BuildTimeAnalyzer on {DateTime.Now:yyyy-MM-dd HH:mm:ss}</footer>");
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

    private static void AppendTopSuspects(StringBuilder sb, BuildAnalysis? analysis)
    {
        if (analysis is null) return;
        // Promote Critical and Warning findings, ranked by upper-bound impact then severity.
        var ranked = analysis.Findings
            .Where(f => f.Severity != FindingSeverity.Info)
            .OrderByDescending(f => f.Severity == FindingSeverity.Critical ? 1 : 0)
            .ThenByDescending(f => f.UpperBoundImpactPercent ?? 0)
            .Take(3)
            .ToList();
        if (ranked.Count == 0) return;

        sb.AppendLine("<h2>Top Suspects</h2>");
        sb.AppendLine("<p class=\"note\">Highest-impact findings at a glance. Full Analysis section below has the evidence and next steps.</p>");
        sb.AppendLine("<div class=\"analysis\">");
        foreach (var f in ranked)
        {
            var sevColor = f.Severity == FindingSeverity.Critical ? "var(--red)" : "var(--yellow)";
            var impact = f.UpperBoundImpactPercent is { } pct ? $" <span class=\"muted\">· up to {pct:F1}% of total self time</span>" : "";
            sb.AppendLine($"""
<div style="margin: 6px 0; padding-left: 10px; border-left: 3px solid {sevColor}">
  <strong>{Esc(f.Title)}</strong>{impact}
</div>
""");
        }
        sb.AppendLine("</div>");
    }

    private static void AppendAnalysisSection(StringBuilder sb, BuildAnalysis? analysis)
    {
        if (analysis is null || analysis.Findings.Count == 0) return;

        sb.AppendLine("<h2>Analysis</h2>");
        sb.AppendLine("<p class=\"note\">Findings are structured as <strong>Measured</strong> (facts), <strong>Likely explanation</strong> (heuristic hypothesis when present), and <strong>Investigate</strong> (next step).</p>");
        sb.AppendLine("<div class=\"analysis\">");
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
  <div><span class="num">{f.Number}.</span><span class="title">{Esc(f.Title)}</span></div>
  <div class="layer layer-measured"><span class="layer-label">Measured</span><span class="layer-value">{Esc(f.Measured)}</span></div>
""");
            if (!string.IsNullOrEmpty(f.LikelyExplanation))
                sb.AppendLine($"  <div class=\"layer layer-likely\"><span class=\"layer-label\">Likely</span><span class=\"layer-value\">{Esc(f.LikelyExplanation)}</span></div>");
            sb.AppendLine($"  <div class=\"layer layer-investigate\"><span class=\"layer-label\">Investigate</span><span class=\"layer-value\">{Esc(f.InvestigationSuggestion)}</span></div>");
            var confLabel = f.Confidence switch { FindingConfidence.High => "high", FindingConfidence.Medium => "medium", _ => "low" };
            var confColor = f.Confidence switch { FindingConfidence.High => "green", FindingConfidence.Medium => "yellow", _ => "muted" };
            var impactText = f.UpperBoundImpactPercent is { } pct ? $" &nbsp; · &nbsp; Upper bound: up to {pct:F1}% of total self time" : "";
            sb.AppendLine($"  <div class=\"evidence\">Confidence: <span class=\"{confColor}\">{confLabel}</span>{impactText} &nbsp; · &nbsp; Evidence: {Esc(f.Evidence)} &nbsp; · &nbsp; Threshold: {Esc(f.ThresholdName)}</div>");
            sb.AppendLine("</div>");
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

    private static void AppendWhyIsThisSlow(StringBuilder sb, BuildReport report)
    {
        if (report.ProjectDiagnoses.Count == 0) return;

        sb.AppendLine("<h2>Why Is This Slow?</h2>");
        sb.AppendLine("<p class=\"note\">Factual one-paragraph synthesis per top project. No interpretation beyond measured data.</p>");
        sb.AppendLine("<div class=\"analysis\">");
        foreach (var d in report.ProjectDiagnoses)
        {
            var badges = new List<string>();
            if (d.OnCriticalPath) badges.Add("<span class=\"cat-badge cat-references\">critical path</span>");
            if (d.IsSpanOutlier) badges.Add("<span class=\"cat-badge cat-uncategorized\">span outlier</span>");
            var badgeHtml = badges.Count > 0 ? " " + string.Join(" ", badges) : "";
            sb.AppendLine($"""
<div class="finding severity-info" style="border-left-color:var(--cyan)">
  <div><strong>{Esc(d.ProjectName)}</strong> — {Esc(ConsoleReportRenderer.FormatDuration(d.SelfTime))} ({d.SelfPercent:F1}%){badgeHtml}</div>
  <div class="detail" style="margin-top:8px">{Esc(d.Summary)}</div>
</div>
""");
        }
        sb.AppendLine("</div>");
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
