using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Rendering;

public static class ConsoleReportRenderer
{
    public static void Render(BuildReport report, int topN)
    {
        RenderSummary(report);
        Console.WriteLine();
        RenderBuildContext(report);

        if (report.Graph.Nodes.Count > 0)
        {
            Console.WriteLine();
            RenderGraphHealth(report);

            if (report.Graph.TopHubs.Count > 0)
            {
                Console.WriteLine();
                RenderDependencyHubs(report);
            }

            Console.WriteLine();
            RenderCycleStatus(report);
        }

        // Critical path validation status is always shown when the graph has content,
        // so the reader understands why the section is or is not present
        if (report.Graph.Nodes.Count > 0)
        {
            Console.WriteLine();
            RenderCriticalPathValidation(report);
        }

        if (report.CriticalPath.Count > 0)
        {
            Console.WriteLine();
            RenderCriticalPath(report);
        }

        if (report.CategoryTotals.Count > 0)
        {
            Console.WriteLine();
            RenderCategoryTotals(report);
        }

        if (report.ReferenceOverhead is not null)
        {
            Console.WriteLine();
            RenderReferenceOverhead(report);
        }

        if (report.SpanOutliers.Count > 0)
        {
            Console.WriteLine();
            RenderSpanOutliers(report);
        }

        if (report.Projects.Count > 0)
        {
            Console.WriteLine();
            RenderProjectCountTax(report);
        }

        Console.WriteLine();
        RenderTimeline(report);
        Console.WriteLine();
        RenderProjectsTable(report, topN);
        Console.WriteLine();
        RenderTargetsTable(report);

        var showCustom = report.PotentiallyCustomTargets.Any(t => t.SelfTime.TotalSeconds >= 1);
        if (showCustom)
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

    // ──────────────────────────── Build Context ────────────────────────────

    private static void RenderBuildContext(BuildReport report)
    {
        var ctx = report.Context;
        var lines = new List<(string Label, string Value)>();
        if (ctx.Configuration is not null) lines.Add(("Configuration", ctx.Configuration));
        if (ctx.SdkVersion is not null) lines.Add((".NET SDK", ctx.SdkVersion));
        if (ctx.MSBuildVersion is not null) lines.Add(("MSBuild", ctx.MSBuildVersion));
        if (ctx.OperatingSystem is not null) lines.Add(("OS", ctx.OperatingSystem));
        if (ctx.Parallelism is not null) lines.Add(("Parallelism", $"{ctx.Parallelism} nodes"));
        if (ctx.RestoreObserved is true) lines.Add(("Restore", "included in this build"));

        var totalTargets = report.ExecutedTargetCount + report.SkippedTargetCount;
        if (totalTargets > 0)
            lines.Add(("Incremental", $"{report.SkippedTargetCount} of {totalTargets} targets skipped as up-to-date"));

        if (lines.Count == 0) return;

        Console.WriteLine("  Build Context");
        Console.WriteLine();
        var labelWidth = lines.Max(l => l.Label.Length);
        foreach (var (label, value) in lines)
            Console.WriteLine($"  {label.PadRight(labelWidth)}  {value}");
    }

    // ──────────────────────────── Graph Health ─────────────────────────

    private static void RenderGraphHealth(BuildReport report)
    {
        var h = report.Graph.Health;
        Console.WriteLine("  Dependency Graph Health");
        Console.WriteLine();
        Console.WriteLine($"  Total projects          {h.TotalProjects}");
        Console.WriteLine($"  Total edges             {h.TotalEdges}");
        Console.WriteLine($"  Nodes with outgoing     {h.NodesWithOutgoing}");
        Console.WriteLine($"  Nodes with incoming     {h.NodesWithIncoming}");
        Console.WriteLine($"  Isolated nodes          {h.IsolatedNodes}");
        Console.WriteLine($"  Longest chain           {report.Graph.LongestChainProjectCount} projects");

        if (!report.Graph.IsUsable)
        {
            Console.WriteLine();
            Console.WriteLine("  Note: graph has too few edges for derived analyses (critical path, etc.).");
            Console.WriteLine("  Check whether ProjectReference extraction captured your project references correctly.");
        }
    }

    // ──────────────────────────── Dependency Hubs ─────────────────────

    private static void RenderDependencyHubs(BuildReport report)
    {
        Console.WriteLine("  Dependency Hubs");
        Console.WriteLine("  (Sorted by transitive dependents — how much of the graph is downstream.");
        Console.WriteLine("   High fan-in is a structural signal, not automatic proof of bottleneck status.)");
        Console.WriteLine();

        var nameWidth = Math.Max(7, report.Graph.TopHubs.Max(h => h.ProjectName.Length));
        nameWidth = Math.Min(nameWidth, 35);

        Console.WriteLine($"  {"#",-4} {"Project".PadRight(nameWidth)}  {"Direct Ref by",14}  {"Transitive Deps",16}  {"Direct Refs",12}");
        Console.WriteLine($"  {new string('-', 4)} {new string('-', nameWidth)}  {new string('-', 14)}  {new string('-', 16)}  {new string('-', 12)}");

        int rank = 1;
        foreach (var h in report.Graph.TopHubs.Take(10))
        {
            var name = Truncate(h.ProjectName, nameWidth);
            Console.WriteLine($"  {rank,-4} {name.PadRight(nameWidth)}  {h.IncomingCount,14}  {h.TransitiveDependentsCount,16}  {h.OutgoingCount,12}");
            rank++;
        }
    }

    // ──────────────────────────── Cycle Status ────────────────────────────

    private static void RenderCycleStatus(BuildReport report)
    {
        Console.WriteLine("  Dependency Cycle Check");
        Console.WriteLine();
        if (!report.Graph.CycleDetectionRan)
        {
            Console.WriteLine("  Cycle detection did not run.");
            return;
        }
        if (report.Graph.Cycles.Count == 0)
        {
            Console.WriteLine("  No project-reference cycles detected.");
            return;
        }

        Console.WriteLine($"  {report.Graph.Cycles.Count} cycle(s) detected:");
        foreach (var cycle in report.Graph.Cycles.Take(5))
            Console.WriteLine($"    {string.Join(" -> ", cycle)} -> {cycle[0]}");
    }

    // ──────────────────────────── Critical Path Validation ────────────────

    private static void RenderCriticalPathValidation(BuildReport report)
    {
        var v = report.CriticalPathValidation;
        Console.WriteLine("  Critical Path Validation");
        Console.WriteLine();
        Console.WriteLine($"  Graph usable      {(v.GraphWasUsable ? "yes" : "no")}");
        Console.WriteLine($"  CPM total         {FormatDuration(v.ComputedTotal)}");
        Console.WriteLine($"  Wall clock        {FormatDuration(v.WallClock)}");
        Console.WriteLine($"  Accepted          {(v.Accepted ? "yes" : "no")}");
        Console.WriteLine($"  Reason            {v.Reason}");
    }

    // ──────────────────────────── Critical Path ────────────────────────────

    private static void RenderCriticalPath(BuildReport report)
    {
        Console.WriteLine("  Critical Path Estimate (model-based, not a scheduler trace)");
        Console.WriteLine("  Derived from observed ProjectReference DAG + measured self times. Accuracy depends on");
        Console.WriteLine("  whether dependency extraction and exclusive timing are capturing your build correctly.");
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
        Console.WriteLine($"  Path total: {FormatDuration(report.CriticalPathTotal)} of {FormatDuration(report.TotalDuration)} wall clock");
    }

    // ──────────────────────────── Category Totals ────────────────────

    private static void RenderCategoryTotals(BuildReport report)
    {
        var totalSelfMs = report.CategoryTotals.Sum(kv => kv.Value.TotalMilliseconds);
        if (totalSelfMs <= 0) return;

        Console.WriteLine("  Self Time by Category");
        Console.WriteLine();

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
        Console.WriteLine("  Reference Overhead (aggregated across solution)");
        Console.WriteLine();
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
        Console.WriteLine("  Span vs Self Outliers");
        Console.WriteLine("  Rule: Span >= 5s, Span/SelfTime >= 5x, Span - SelfTime >= 3s");
        Console.WriteLine("  The pattern has several possible causes (dependency waiting, SDK orchestration, reference");
        Console.WriteLine("  work, static-web-assets, test/benchmark shape, incremental effects). Timing alone does not");
        Console.WriteLine("  identify which — cross-reference with Dependency Hubs and category composition.");
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
        Console.WriteLine("  Project Count Tax Indicators");
        Console.WriteLine("  Candidate signals that the solution pays graph/orchestration cost disproportionate to");
        Console.WriteLine("  local work. Each indicator is a pattern to investigate, not proof of a problem.");
        Console.WriteLine();

        Console.WriteLine($"  References > compile                     {tax.ReferencesExceedCompileCount} of {tax.TotalProjects}");
        Console.WriteLine($"  References are the majority of self time {tax.ReferencesMajorityCount} of {tax.TotalProjects}");
        Console.WriteLine($"  Tiny self / huge span (outlier rule)     {tax.TinySelfHugeSpanCount} of {tax.TotalProjects}");
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

    // ──────────────────────────── Timeline ─────────────────────────────

    private static void RenderTimeline(BuildReport report)
    {
        if (report.Projects.Count == 0) return;

        var hasCritical = report.CriticalPath.Count > 0;
        var header = hasCritical
            ? "  Build Timeline (wall-clock Span per project, * = on critical path estimate)"
            : "  Build Timeline (wall-clock Span per project)";

        Console.WriteLine(header);
        Console.WriteLine();

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

        Console.WriteLine("  Top Projects by Self Time");
        Console.WriteLine();

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
        // Normalise against the sum of the breakdown, not p.SelfTime, so percentages always add to 100%.
        // SelfTime and CategoryBreakdown use slightly different dedup strategies, and mixing them
        // produces misleading sums.
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

        Console.WriteLine("  Top Targets by Self Time");
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

        Console.WriteLine("  Potentially Custom Targets (self time >= 1s)");
        Console.WriteLine();

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

        Console.WriteLine();
        WriteHeader("Analysis");

        Console.WriteLine("  Findings are structured as:");
        Console.WriteLine("    Measured               — counted facts only");
        Console.WriteLine("    Likely explanation     — heuristic hypothesis (when present, clearly tagged)");
        Console.WriteLine("    Investigate            — concrete next step");
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
