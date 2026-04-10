using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Rendering;

public static class ConsoleReportRenderer
{
    /// <summary>
    /// Render the full report in user-oriented order:
    /// summary -> headline -> analysis -> top offenders -> why -> topology -> supporting detail.
    /// Sections earn their place via signal checks; empty/low-signal sections are omitted.
    /// </summary>
    public static void Render(BuildReport report, BuildAnalysis analysis, int topN)
    {
        RenderSummary(report);
        Console.WriteLine();

        RenderHeadline(report, analysis);
        Console.WriteLine();

        if (analysis.Findings.Count > 0)
        {
            RenderAnalysis(analysis);
        }

        Console.WriteLine();
        RenderBuildContext(report);

        Console.WriteLine();
        RenderProjectsTable(report, topN);

        if (HasInterestingTargetBreakdown(report))
        {
            Console.WriteLine();
            RenderTargetsTable(report);
        }

        if (HasInterestingCategoryTotals(report))
        {
            Console.WriteLine();
            RenderCategoryTotals(report);
        }

        if (report.ReferenceOverhead is not null)
        {
            Console.WriteLine();
            RenderReferenceOverhead(report);
        }

        if (HasInterestingGraph(report))
        {
            Console.WriteLine();
            RenderDependencyGraphSection(report);
        }

        if (HasInterestingTimeline(report))
        {
            Console.WriteLine();
            RenderTimeline(report);
        }

        if (report.SpanOutliers.Count > 0)
        {
            Console.WriteLine();
            RenderSpanOutliers(report);
        }

        if (HasInterestingProjectCountTax(report))
        {
            Console.WriteLine();
            RenderProjectCountTax(report);
        }

        if (HasNonTrivialCustomTargets(report))
        {
            Console.WriteLine();
            RenderPotentiallyCustomTargets(report);
        }
    }

    public static void WriteHeader(string title)
    {
        var line = new string('-', Math.Max(0, 80 - title.Length - 3));
        Console.WriteLine($"-- {title} {line}");
        Console.WriteLine();
    }

    // ──────────────────────────── Signal gates ────────────────────────────
    // Sections must have something to say. Gating combines signal checks with
    // solution-size heuristics — never purely threshold-based.

    private static bool HasInterestingGraph(BuildReport r) =>
        r.Graph.Nodes.Count >= 2 && r.Graph.Health.TotalEdges >= 1;

    private static bool HasInterestingTimeline(BuildReport r)
    {
        if (r.Projects.Count < 3) return false;
        if (r.TotalDuration.TotalMilliseconds <= 0) return false;
        // If all projects roughly overlap the full wall clock, the timeline shows nothing useful
        var totalMs = r.TotalDuration.TotalMilliseconds;
        var allBigSpans = r.Projects.All(p => p.Span.TotalMilliseconds >= totalMs * 0.9);
        if (allBigSpans && r.Projects.Count <= 4) return false;
        return true;
    }

    private static bool HasInterestingCategoryTotals(BuildReport r) =>
        r.CategoryTotals.Count(kv => kv.Value.TotalMilliseconds > 0) >= 3;

    private static bool HasInterestingProjectCountTax(BuildReport r)
    {
        var tax = r.ProjectCountTax;
        // Indicators are only meaningful if we have a solution worth indexing, OR if any
        // indicator already flagged something notable.
        if (r.Projects.Count >= 10) return true;
        return tax.ReferencesMajorityCount > 0 ||
               tax.TinySelfHugeSpanCount > 0 ||
               tax.ReferencesExceedCompileCount > 0;
    }

    private static bool HasInterestingTargetBreakdown(BuildReport r) =>
        r.TopTargets.Count >= 3;

    private static bool HasNonTrivialCustomTargets(BuildReport r) =>
        r.PotentiallyCustomTargets.Any(t => t.SelfTime.TotalSeconds >= 1);

    // ──────────────────────────── Summary ────────────────────────────

    private static void RenderSummary(BuildReport report)
    {
        var status = report.Succeeded ? "Build Succeeded" : "Build Failed";
        var project = Path.GetFileName(report.ProjectOrSolutionPath);

        Console.WriteLine($"  Status     {status}");
        Console.WriteLine($"  Project    {project}");
        Console.WriteLine($"  Started    {report.StartTime:HH:mm:ss}");
        Console.WriteLine($"  Wall Clock {FormatDuration(report.TotalDuration)}");
        Console.WriteLine($"  Errors     {report.ErrorCount}");
        Console.WriteLine($"  Warnings   {report.WarningCount} total  ({report.AttributedWarningCount} attributed, {report.UnattributedWarningCount} unattributed)");
        Console.WriteLine($"  Projects   {report.Projects.Count}");
    }

    // ──────────────────────────── Headline ────────────────────────────
    // Short factual synthesis + pointer to top findings. Always rendered.

    private static void RenderHeadline(BuildReport report, BuildAnalysis analysis)
    {
        WriteHeader("Headline");

        // Line 1: wall clock + project count
        Console.WriteLine($"  Build took {FormatDuration(report.TotalDuration)} across {report.Projects.Count} project(s).");

        // Line 2: biggest contributor
        var top = report.Projects.FirstOrDefault();
        if (top is not null && top.SelfPercent >= 10)
        {
            Console.WriteLine($"  Biggest self-time contributor: {top.Name} ({top.SelfPercent:F1}% of self time, {FormatDuration(top.SelfTime)}).");
        }

        // Line 3: highlight the top finding if there is one
        var topCritical = analysis.Findings.FirstOrDefault(f => f.Severity == FindingSeverity.Critical);
        var topWarning = analysis.Findings.FirstOrDefault(f => f.Severity == FindingSeverity.Warning);
        var pointer = topCritical ?? topWarning;
        if (pointer is not null)
        {
            var label = pointer.Severity == FindingSeverity.Critical ? "CRITICAL" : "WARNING";
            Console.WriteLine($"  Top finding [{label}] #{pointer.Number}: {pointer.Title}. See Analysis below.");
        }
        else if (analysis.Findings.Count > 0)
        {
            Console.WriteLine($"  {analysis.Findings.Count} informational finding(s). See Analysis below.");
        }
        else
        {
            Console.WriteLine("  No significant anomalies detected.");
        }

        // Line 4: only add if we have the critical path and it is accepted
        if (report.CriticalPath.Count > 0)
        {
            Console.WriteLine($"  Critical path estimate: {FormatDuration(report.CriticalPathTotal)} across {report.CriticalPath.Count} project(s).");
        }
    }

    // ──────────────────────────── Build Context ────────────────────────────

    private static void RenderBuildContext(BuildReport report)
    {
        var ctx = report.Context;
        var lines = new List<(string Label, string Value)>();
        if (ctx.Configuration is not null) lines.Add(("Configuration", ctx.Configuration));
        if (ctx.BuildMode is not null) lines.Add(("Build Mode", ctx.BuildMode));
        if (ctx.SdkVersion is not null) lines.Add((".NET SDK", ctx.SdkVersion));
        if (ctx.MSBuildVersion is not null) lines.Add(("MSBuild", ctx.MSBuildVersion));
        if (ctx.OperatingSystem is not null) lines.Add(("OS", ctx.OperatingSystem));
        if (ctx.Parallelism is not null) lines.Add(("Parallelism", $"{ctx.Parallelism} nodes"));
        if (ctx.RestoreObserved is true) lines.Add(("Restore", "included in this build"));

        var totalTargets = report.ExecutedTargetCount + report.SkippedTargetCount;
        if (totalTargets > 0)
            lines.Add(("Incremental", $"{report.SkippedTargetCount} of {totalTargets} targets skipped as up-to-date"));

        if (lines.Count == 0) return;

        WriteHeader("Build Context");
        var labelWidth = lines.Max(l => l.Label.Length);
        foreach (var (label, value) in lines)
            Console.WriteLine($"  {label.PadRight(labelWidth)}  {value}");
    }

    // ──────────────────────────── Dependency Graph (consolidated) ────────────────────────────

    private static void RenderDependencyGraphSection(BuildReport report)
    {
        WriteHeader("Dependency Graph");

        var h = report.Graph.Health;
        Console.WriteLine($"  Projects: {h.TotalProjects}    Edges: {h.TotalEdges}    Isolated: {h.IsolatedNodes}    Longest chain: {report.Graph.LongestChainProjectCount}");

        // Cycle status as a one-liner, not a full section
        if (report.Graph.CycleDetectionRan)
        {
            if (report.Graph.Cycles.Count == 0)
                Console.WriteLine("  Cycles: none detected");
            else
                Console.WriteLine($"  Cycles: {report.Graph.Cycles.Count} detected (first: {string.Join(" -> ", report.Graph.Cycles[0])} -> {report.Graph.Cycles[0][0]})");
        }

        // Critical path validation as a one-liner
        var v = report.CriticalPathValidation;
        var statusLabel = v.Accepted ? "accepted" : "rejected";
        Console.WriteLine($"  Critical path validation: {statusLabel} (CPM total {FormatDuration(v.ComputedTotal)} vs wall clock {FormatDuration(v.WallClock)})");
        if (!v.Accepted)
            Console.WriteLine($"    {v.Reason}");

        // Hubs subsection — only if there are hubs worth showing
        if (report.Graph.TopHubs.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Dependency Hubs (by transitive dependents)");
            Console.WriteLine("  High fan-in is a structural signal, not automatic proof of bottleneck status.");
            Console.WriteLine();

            var nameWidth = Math.Max(7, report.Graph.TopHubs.Max(x => x.ProjectName.Length));
            nameWidth = Math.Min(nameWidth, 35);

            Console.WriteLine($"  {"#",-4} {"Project".PadRight(nameWidth)}  {"Ref by",8}  {"Transitive",11}  {"Refs",6}");
            Console.WriteLine($"  {new string('-', 4)} {new string('-', nameWidth)}  {new string('-', 8)}  {new string('-', 11)}  {new string('-', 6)}");

            int rank = 1;
            foreach (var hub in report.Graph.TopHubs.Take(10))
            {
                var name = Truncate(hub.ProjectName, nameWidth);
                Console.WriteLine($"  {rank,-4} {name.PadRight(nameWidth)}  {hub.IncomingCount,8}  {hub.TransitiveDependentsCount,11}  {hub.OutgoingCount,6}");
                rank++;
            }
        }

        // Critical path listing — only if accepted and the path has content
        if (report.CriticalPath.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Critical Path Estimate (model-based, not a scheduler trace)");
            Console.WriteLine();

            var nameWidth = Math.Max(7, report.CriticalPath.Max(p => p.Name.Length));
            nameWidth = Math.Min(nameWidth, 35);

            Console.WriteLine($"  {"Step",-4} {"Project".PadRight(nameWidth)}  {"Self Time",10}  {"% Self",8}  {"Kind",-20}");
            Console.WriteLine($"  {new string('-', 4)} {new string('-', nameWidth)}  {new string('-', 10)}  {new string('-', 8)}  {new string('-', 20)}");

            int step = 1;
            foreach (var p in report.CriticalPath)
            {
                var name = Truncate(p.Name, nameWidth);
                var kind = ProjectKindHeuristic.Label(p.KindHeuristic);
                Console.WriteLine($"  {step,-4} {name.PadRight(nameWidth)}  {FormatDuration(p.SelfTime),10}  {p.SelfPercent,7:F1}%  {kind,-20}");
                step++;
            }
            Console.WriteLine();
            Console.WriteLine($"  Path total: {FormatDuration(report.CriticalPathTotal)} of {FormatDuration(report.TotalDuration)} wall clock.");
        }
    }

    // ──────────────────────────── Category Totals ────────────────────

    private static void RenderCategoryTotals(BuildReport report)
    {
        var totalSelfMs = report.CategoryTotals.Sum(kv => kv.Value.TotalMilliseconds);
        if (totalSelfMs <= 0) return;

        WriteHeader("Self Time by Category");

        var rows = report.CategoryTotals
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (
                Category: CategoryLabel(kv.Key),
                Self: kv.Value,
                Pct: kv.Value.TotalMilliseconds / totalSelfMs * 100))
            .ToList();

        var catWidth = Math.Max(8, rows.Max(r => r.Category.Length));

        Console.WriteLine($"  {"Category".PadRight(catWidth)}  {"Self Time",10}  {"% Self",8}");
        Console.WriteLine($"  {new string('-', catWidth)}  {new string('-', 10)}  {new string('-', 8)}");
        foreach (var r in rows)
            Console.WriteLine($"  {r.Category.PadRight(catWidth)}  {FormatDuration(r.Self),10}  {r.Pct,7:F1}%");
    }

    // ──────────────────────────── Reference Overhead ────────────────

    private static void RenderReferenceOverhead(BuildReport report)
    {
        var o = report.ReferenceOverhead!;
        WriteHeader("Reference Overhead (aggregated across solution)");
        Console.WriteLine($"  Total self time in reference work   {FormatDuration(o.TotalSelfTime)}");
        Console.WriteLine($"  Share of total self time            {o.SelfPercent:F1}%");
        Console.WriteLine($"  Projects paying the cost            {o.PayingProjectsCount} of {o.TotalProjectsCount} ({o.PayingProjectsPercent:F0}%)");
        Console.WriteLine($"  Median per paying project           {FormatDuration(o.MedianPerPayingProject)}");

        if (o.TopProjects.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Top projects by reference overhead:");
            foreach (var p in o.TopProjects.Take(5))
                Console.WriteLine($"    {p.ProjectName,-35}  {FormatDuration(p.SelfTime),10}");
        }
    }

    // ──────────────────────────── Span Outliers ────────────────────

    private static void RenderSpanOutliers(BuildReport report)
    {
        WriteHeader("Span vs Self Outliers");
        Console.WriteLine("  Rule: Span >= 5s, Span/SelfTime >= 5x, Span - SelfTime >= 3s");
        Console.WriteLine("  Pattern has several possible causes (dependency waiting, SDK orchestration, reference");
        Console.WriteLine("  work, static-web-assets, test/benchmark shape, incremental effects). Timing alone does");
        Console.WriteLine("  not identify which — cross-reference with Dependency Graph.");
        Console.WriteLine();

        var rows = report.SpanOutliers.ToList();
        var nameWidth = Math.Max(7, rows.Max(p => p.Name.Length));
        nameWidth = Math.Min(nameWidth, 35);

        Console.WriteLine($"  {"#",-4} {"Project".PadRight(nameWidth)}  {"Span",10}  {"Self Time",10}  {"Ratio",8}  {"Kind",-20}");
        Console.WriteLine($"  {new string('-', 4)} {new string('-', nameWidth)}  {new string('-', 10)}  {new string('-', 10)}  {new string('-', 8)}  {new string('-', 20)}");

        int rank = 1;
        foreach (var p in rows)
        {
            var ratio = p.SelfTime.TotalMilliseconds > 0 ? p.Span.TotalMilliseconds / p.SelfTime.TotalMilliseconds : 0;
            var name = Truncate(p.Name, nameWidth);
            var kind = ProjectKindHeuristic.Label(p.KindHeuristic);
            Console.WriteLine($"  {rank,-4} {name.PadRight(nameWidth)}  {FormatDuration(p.Span),10}  {FormatDuration(p.SelfTime),10}  {ratio,7:F1}x  {kind,-20}");
            rank++;
        }
    }

    // ──────────────────────────── Project Count Tax ────────────────

    private static void RenderProjectCountTax(BuildReport report)
    {
        var tax = report.ProjectCountTax;
        WriteHeader("Project Count Tax Indicators");
        Console.WriteLine("  Candidate signals that the solution pays graph/orchestration cost disproportionate");
        Console.WriteLine("  to local work. Each indicator is a pattern to investigate, not proof of a problem.");
        Console.WriteLine();

        Console.WriteLine($"  References > compile                     {tax.ReferencesExceedCompileCount} of {tax.TotalProjects}");
        Console.WriteLine($"  References are the majority of self time {tax.ReferencesMajorityCount} of {tax.TotalProjects}");
        Console.WriteLine($"  Tiny self / huge span (outlier rule)     {tax.TinySelfHugeSpanCount} of {tax.TotalProjects}");

        if (tax.PerKindStats.Count > 0 && tax.PerKindStats.Any(s => s.Count > 0))
        {
            Console.WriteLine();
            Console.WriteLine("  Per-kind medians (name-based heuristic, not authoritative):");
            Console.WriteLine();
            Console.WriteLine($"  {"Kind",-22}  {"Count",6}  {"Median Self",12}  {"Median Span",12}  {"Median Span/Self",18}");
            Console.WriteLine($"  {new string('-', 22)}  {new string('-', 6)}  {new string('-', 12)}  {new string('-', 12)}  {new string('-', 18)}");
            foreach (var s in tax.PerKindStats)
            {
                var kindLabel = ProjectKindHeuristic.Label(s.Kind);
                var ratioDisplay = s.MedianSpanToSelfRatio > 0 ? $"{s.MedianSpanToSelfRatio:F1}x" : "n/a";
                Console.WriteLine($"  {kindLabel,-22}  {s.Count,6}  {FormatDuration(s.MedianSelfTime),12}  {FormatDuration(s.MedianSpan),12}  {ratioDisplay,18}");
            }
        }
    }

    // ──────────────────────────── Timeline ─────────────────────────────

    private static void RenderTimeline(BuildReport report)
    {
        if (report.Projects.Count == 0) return;

        var hasCritical = report.CriticalPath.Count > 0;
        var header = hasCritical
            ? "Build Timeline (wall-clock Span, * = on critical path estimate)"
            : "Build Timeline (wall-clock Span)";
        WriteHeader(header);

        var totalMs = report.TotalDuration.TotalMilliseconds;
        if (totalMs <= 0) return;

        var criticalSet = new HashSet<string>(
            report.CriticalPath.Select(p => p.FullPath),
            StringComparer.OrdinalIgnoreCase);

        var projects = report.Projects.OrderBy(p => p.StartOffset).ToList();

        var nameWidth = Math.Max(7, projects.Max(p => p.Name.Length));
        nameWidth = Math.Min(nameWidth, 25);
        const int barWidth = 50;
        var pad = new string(' ', nameWidth + 2);

        var tickCount = 5;
        var tickSpacing = barWidth / (tickCount - 1);
        var labels = new string[tickCount];
        for (int i = 0; i < tickCount; i++)
            labels[i] = FormatDuration(TimeSpan.FromMilliseconds(totalMs * i / (tickCount - 1)));

        var axisChars = new char[barWidth];
        Array.Fill(axisChars, ' ');
        for (int i = 0; i < tickCount - 1; i++)
        {
            var pos = i * tickSpacing;
            for (int j = 0; j < labels[i].Length && pos + j < barWidth; j++)
                axisChars[pos + j] = labels[i][j];
        }
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

        foreach (var p in projects)
        {
            var startPos = (int)Math.Round(p.StartOffset.TotalMilliseconds / totalMs * barWidth);
            var endPos = (int)Math.Round(p.EndOffset.TotalMilliseconds / totalMs * barWidth);
            startPos = Math.Clamp(startPos, 0, barWidth - 1);
            endPos = Math.Clamp(endPos, startPos + 1, barWidth);

            var onCritical = hasCritical && criticalSet.Contains(p.FullPath);
            var barChar = onCritical ? '#' : '.';

            var bar = new char[barWidth];
            Array.Fill(bar, ' ');
            for (int i = startPos; i < endPos; i++)
                bar[i] = barChar;

            var name = Truncate(p.Name, nameWidth);
            var marker = hasCritical ? (onCritical ? "*" : " ") : " ";
            Console.WriteLine($"  {name.PadRight(nameWidth)} {marker} {new string(bar)}  {FormatDuration(p.Span)}");
        }
    }

    // ──────────────────────────── Projects ───────────────────────────

    private static void RenderProjectsTable(BuildReport report, int topN)
    {
        var projects = report.Projects.Take(topN).ToList();
        if (projects.Count == 0) return;

        WriteHeader("Top Projects by Self Time");

        var projectNameMax = projects.Max(p => p.Name.Length);
        var targetNameMax = projects.SelectMany(p => p.Targets).Select(t => t.Name.Length).DefaultIfEmpty(0).Max();
        var nameWidth = Math.Max(7, Math.Max(projectNameMax, targetNameMax + 4));
        nameWidth = Math.Min(nameWidth, 38);

        Console.WriteLine($"  {"#",-4} {"Project".PadRight(nameWidth)}  {"Self Time",10}  {"Span",10}  {"% Self",8}  {"Err",4}  {"Warn",4}");
        Console.WriteLine($"  {new string('-', 4)} {new string('-', nameWidth)}  {new string('-', 10)}  {new string('-', 10)}  {new string('-', 8)}  {new string('-', 4)}  {new string('-', 4)}");

        int rank = 1;
        foreach (var p in projects)
        {
            var name = Truncate(p.Name, nameWidth);
            var status = p.Succeeded ? " " : "!";

            Console.WriteLine($"  {rank,-4} {status}{name.PadRight(nameWidth - 1)}  {FormatDuration(p.SelfTime),10}  {FormatDuration(p.Span),10}  {p.SelfPercent,7:F1}%  {p.ErrorCount,4}  {p.WarningCount,4}");
            rank++;

            if (p.CategoryBreakdown.Count > 0)
            {
                var composition = FormatCategoryComposition(p);
                if (composition.Length > 0)
                {
                    var compLeader = "  composition:".PadRight(nameWidth + 1);
                    Console.WriteLine($"       {compLeader} {composition}");
                }
            }

            if (p.Targets.Count > 0)
            {
                foreach (var t in p.Targets)
                {
                    var tname = Truncate(t.Name, nameWidth - 4);
                    var leader = ("  -> " + tname).PadRight(nameWidth);
                    Console.WriteLine($"       {leader} {FormatDuration(t.SelfTime),10}  {"",10}  {t.SelfPercent,7:F1}%  [{CategoryLabel(t.Category)}]");
                }
            }
        }
    }

    private static string FormatCategoryComposition(ProjectTiming p)
    {
        var totalMs = p.CategoryBreakdown.Sum(kv => kv.Value.TotalMilliseconds);
        if (totalMs <= 0) return "";

        var parts = p.CategoryBreakdown
            .Where(kv => kv.Value.TotalMilliseconds > 0)
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => $"{CategoryLabel(kv.Key)} {kv.Value.TotalMilliseconds / totalMs * 100:F0}%");

        return string.Join(", ", parts);
    }

    // ──────────────────────────── Targets ────────────────────────────

    private static void RenderTargetsTable(BuildReport report)
    {
        if (report.TopTargets.Count == 0) return;

        WriteHeader("Top Targets by Self Time");
        Console.WriteLine("  (Categories are deterministic pattern matches against SDK target names — a grouping hint, not authoritative.)");
        Console.WriteLine();

        var nameWidth = Math.Max(6, report.TopTargets.Max(t => t.Name.Length));
        nameWidth = Math.Min(nameWidth, 30);
        var projWidth = Math.Max(7, report.TopTargets.Max(t => t.ProjectName.Length));
        projWidth = Math.Min(projWidth, 25);
        const int catWidth = 17;

        Console.WriteLine($"  {"#",-4} {"Target".PadRight(nameWidth)}  {"Project".PadRight(projWidth)}  {"Category".PadRight(catWidth)}  {"Self Time",10}  {"% Self",8}");
        Console.WriteLine($"  {new string('-', 4)} {new string('-', nameWidth)}  {new string('-', projWidth)}  {new string('-', catWidth)}  {new string('-', 10)}  {new string('-', 8)}");

        int rank = 1;
        foreach (var t in report.TopTargets)
        {
            var name = Truncate(t.Name, nameWidth);
            var proj = Truncate(t.ProjectName, projWidth);
            var cat = Truncate(CategoryLabel(t.Category), catWidth);

            Console.WriteLine($"  {rank,-4} {name.PadRight(nameWidth)}  {proj.PadRight(projWidth)}  {cat.PadRight(catWidth)}  {FormatDuration(t.SelfTime),10}  {t.SelfPercent,7:F1}%");
            rank++;
        }
    }

    // ──────────────────────────── Custom Targets ────────────────────────────

    private static void RenderPotentiallyCustomTargets(BuildReport report)
    {
        var targets = report.PotentiallyCustomTargets
            .Where(t => t.SelfTime.TotalSeconds >= 1)
            .Take(5)
            .ToList();
        if (targets.Count == 0) return;

        WriteHeader("Potentially Custom Targets (self time >= 1s)");

        var nameWidth = Math.Max(6, targets.Max(t => t.Name.Length));
        nameWidth = Math.Min(nameWidth, 35);
        var projWidth = Math.Max(7, targets.Max(t => t.ProjectName.Length));
        projWidth = Math.Min(projWidth, 25);

        Console.WriteLine($"  {"#",-4} {"Target".PadRight(nameWidth)}  {"Project".PadRight(projWidth)}  {"Self Time",10}");
        Console.WriteLine($"  {new string('-', 4)} {new string('-', nameWidth)}  {new string('-', projWidth)}  {new string('-', 10)}");

        int rank = 1;
        foreach (var t in targets)
        {
            var name = Truncate(t.Name, nameWidth);
            var proj = Truncate(t.ProjectName, projWidth);
            Console.WriteLine($"  {rank,-4} {name.PadRight(nameWidth)}  {proj.PadRight(projWidth)}  {FormatDuration(t.SelfTime),10}");
            rank++;
        }
    }

    // ──────────────────────────── Analysis ───────────────────────────

    public static void RenderAnalysis(BuildAnalysis analysis)
    {
        if (analysis.Findings.Count == 0) return;

        WriteHeader("Analysis");

        Console.WriteLine("  Findings are structured as:");
        Console.WriteLine("    Measured               — counted facts only");
        Console.WriteLine("    Likely explanation     — heuristic hypothesis (when present, clearly tagged)");
        Console.WriteLine("    Investigate            — concrete next step");
        Console.WriteLine("  Ordered by severity (Critical → Warning → Info).");
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
            Console.WriteLine($"     Measured:     {f.Measured}");
            if (!string.IsNullOrEmpty(f.LikelyExplanation))
                Console.WriteLine($"     Likely:       {f.LikelyExplanation}");
            Console.WriteLine($"     Investigate:  {f.InvestigationSuggestion}");
            Console.WriteLine($"     Evidence:     {f.Evidence}");
            Console.WriteLine($"     Threshold:    {f.ThresholdName}");
            Console.WriteLine();
        }

        if (analysis.Recommendations.Count > 0)
        {
            Console.WriteLine("  Recommendations (investigations to run, not conclusions)");
            Console.WriteLine();
            foreach (var r in analysis.Recommendations)
                Console.WriteLine($"  {r.Number}. {r.Text}");
        }
    }

    // ──────────────────────────── Helpers ────────────────────────────

    public static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 1) return FormattableString.Invariant($"{ts.TotalMilliseconds:F0}ms");
        if (ts.TotalMinutes < 1) return FormattableString.Invariant($"{ts.TotalSeconds:F2}s");
        return FormattableString.Invariant($"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s");
    }

    public static string CategoryLabel(TargetCategory category) => category switch
    {
        TargetCategory.Compile => "compile",
        TargetCategory.SourceGen => "source-gen",
        TargetCategory.StaticWebAssets => "static-web-assets",
        TargetCategory.Copy => "output copy",
        TargetCategory.Restore => "restore",
        TargetCategory.References => "references",
        TargetCategory.Uncategorized => "uncategorized",
        TargetCategory.Other => "internal",
        _ => "unknown",
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;
}
