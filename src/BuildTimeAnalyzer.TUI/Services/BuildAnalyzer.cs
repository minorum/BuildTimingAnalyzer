using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Rendering;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Evidence-driven analysis. Every finding is split into strict layers:
///   • Title / Measured       — purely factual, no interpretation
///   • LikelyExplanation      — heuristic hypothesis (optional, clearly tagged)
///   • InvestigationSuggestion — concrete next step
///
/// Recommendations are investigation targets only — never structural conclusions from timing data.
/// </summary>
public static class BuildAnalyzer
{
    // ── Named thresholds ─────────────────────────────────────────────

    private const double LargestShareCriticalPct = 25.0;
    private const double LargestShareWarningPct = 15.0;
    private const double LargestGapCriticalRatio = 2.5;
    private const double LargestGapWarningRatio = 1.8;

    private const double OutlierTargetMedianMultiplier = 4.0;
    private const double OutlierTargetMinSeconds = 2.0;

    private const double CostlyResolvePackageAssetsSeconds = 3.0;

    private const int WarningsMinForFinding = 20;
    private const double WarningConcentrationRatio = 0.30;

    private const double CriticalPathMinInterestingPct = 30.0;

    private const double ReferenceSelfPctMin = 10.0;
    private const double PayingProjectsPctMin = 50.0;
    private const double MedianReferencePerPayingProjectMinMs = 250.0;

    private const int SpanOutliersMinCountForFinding = 3;

    // TFM negotiation overhead — cost aggregates across many ProjectReference edges
    private const double TfmNegotiationAggregateSecondsThreshold = 120;
    private const string TfmNegotiationTarget = "_GetProjectReferenceTargetFrameworkProperties";

    // Generator anomalies
    private const double GenLoggingOutlierMinSeconds = 5;
    private const double GenLoggingOutlierProjectShareThreshold = 0.5;
    private const double ComInterfaceGeneratorMinSecondsForFinding = 10;
    private const double CSharpAnalyzersInNonRoslynMinSeconds = 5;

    // Warning concentration on the critical path
    private const int WarningsOnCriticalPathMinPerProject = 50;

    private static readonly HashSet<string> KnownGenLoggingAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Gen.Logging",
    };
    private static readonly HashSet<string> KnownComInterfaceGeneratorAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Interop.ComInterfaceGenerator",
    };
    private const string CSharpAnalyzersAssembly = "Microsoft.CodeAnalysis.CSharp.Analyzers";

    public static BuildAnalysis Analyze(BuildReport report)
    {
        if (report.TotalDuration < TimeSpan.FromSeconds(1))
            return new BuildAnalysis { Findings = [], Recommendations = [] };

        var findings = new List<AnalysisFinding>();
        var totalSelfMs = report.Projects.Sum(p => p.SelfTime.TotalMilliseconds);

        // Kept findings name a specific project + measured number + concrete next step.
        // Removed: LargestGap, SpanWaitingPattern, CriticalPathConcentration, WarningConcentration,
        // BroadReferenceOverhead, OutlierTargets, ComInterfaceGenerator no-op — either too generic,
        // duplicates the blocking chain, or "no action needed" (trivia, not findings).
        DetectLargestShare(report, findings);
        DetectCostlyResolvePackageAssets(report, findings);
        DetectTfmNegotiationOverhead(report, findings);
        DetectBenchmarksOnCriticalPath(report, findings);
        DetectWarningHeavyCriticalPath(report, findings);
        DetectGenLoggingOutlier(report, findings);
        DetectComInterfaceGeneratorWithUsages(report, findings);
        DetectCSharpAnalyzersInNonRoslynProject(report, findings);
        DetectHeavyTransitivesInTestOrBenchmark(report, findings);

        // Severity order: Critical first, then Warning, then Info.
        // Keep detection order within a severity so consecutive runs produce stable numbering.
        var ordered = findings
            .Select((f, idx) => (f, idx))
            .OrderBy(x => x.f.Severity switch
            {
                FindingSeverity.Critical => 0,
                FindingSeverity.Warning => 1,
                _ => 2,
            })
            .ThenBy(x => x.idx)
            .Select(x => x.f)
            .ToList();
        findings = ordered;

        for (int i = 0; i < findings.Count; i++)
            findings[i] = findings[i] with { Number = i + 1 };

        // Recommendations removed: the per-finding InvestigationSuggestion is the action item,
        // duplicating it in a separate section adds length without adding information.
        return new BuildAnalysis { Findings = findings, Recommendations = [] };
    }

    // ──────────────────────────── Findings ────────────────────────────

    private static void DetectLargestShare(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.Projects.Count < 2) return;

        var top = report.Projects[0];
        if (top.SelfPercent <= LargestShareWarningPct) return;

        var inspectTarget = top.Targets.Count > 0
            ? $"{top.Name} — start with target {top.Targets[0].Name} ({Fmt(top.Targets[0].SelfTime)}, {CategoryLabel(top.Targets[0].Category)})"
            : $"{top.Name}";

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"{top.Name} dominates build time",
            Severity = top.SelfPercent > LargestShareCriticalPct ? FindingSeverity.Critical : FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            Measured = $"{top.Name}: {Fmt(top.SelfTime)} ({top.SelfPercent:F1}% of total).",
            LikelyExplanation = null,
            InvestigationSuggestion = $"Inspect {inspectTarget}.",
            Evidence = $"SelfPercent={top.SelfPercent:F1}%, SelfTime={Fmt(top.SelfTime)}",
            ThresholdName = $"top-project-share > {LargestShareWarningPct:F0}%",
            UpperBoundImpactPercent = top.SelfPercent,
        });
    }

    private static void DetectLargestGap(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.Projects.Count < 2) return;

        var first = report.Projects[0];
        var second = report.Projects[1];

        if (second.SelfTime.TotalMilliseconds <= 0) return;
        var ratio = first.SelfTime.TotalMilliseconds / second.SelfTime.TotalMilliseconds;

        if (ratio < LargestGapWarningRatio) return;

        var isCritical = ratio >= LargestGapCriticalRatio;
        var severity = isCritical ? FindingSeverity.Critical : FindingSeverity.Warning;
        var thresholdName = isCritical ? nameof(LargestGapCriticalRatio) : nameof(LargestGapWarningRatio);
        var thresholdValue = isCritical ? LargestGapCriticalRatio : LargestGapWarningRatio;

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Largest self-time gap to next project: {first.Name}",
            Severity = severity,
            Confidence = FindingConfidence.High,
            Measured = $"{first.Name} has {Fmt(first.SelfTime)} of self time; the next project ({second.Name}) has {Fmt(second.SelfTime)}. Ratio: {ratio:F1}x.",
            LikelyExplanation = null,
            InvestigationSuggestion = $"Review {first.Name}'s target breakdown to understand what is driving the gap.",
            Evidence = $"Ratio={ratio:F2}, First={Fmt(first.SelfTime)}, Second={Fmt(second.SelfTime)}",
            ThresholdName = $"{thresholdName}={thresholdValue:F1}x",
            UpperBoundImpactPercent = first.SelfPercent,
        });
    }

    private static void DetectOutlierTargets(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.TopTargets.Count < 3) return;

        var groups = report.TopTargets
            .GroupBy(t => t.Name)
            .Where(g => g.Count() >= 3)
            .ToList();

        foreach (var group in groups)
        {
            var sorted = group.OrderBy(t => t.SelfTime).ToList();
            var median = sorted[sorted.Count / 2].SelfTime;
            if (median.TotalMilliseconds <= 0) continue;

            foreach (var outlier in sorted.Where(t =>
                t.SelfTime.TotalMilliseconds > median.TotalMilliseconds * OutlierTargetMedianMultiplier &&
                t.SelfTime.TotalSeconds > OutlierTargetMinSeconds))
            {
                var multiplier = outlier.SelfTime.TotalMilliseconds / median.TotalMilliseconds;

                findings.Add(new AnalysisFinding
                {
                    Number = 0,
                    Title = $"Target outlier: {outlier.Name} in {outlier.ProjectName}",
                    Severity = FindingSeverity.Warning,
                    Confidence = FindingConfidence.High,
                    UpperBoundImpactPercent = outlier.SelfPercent,
                    Measured = $"Median {outlier.Name} self time is {Fmt(median)}; {outlier.ProjectName} runs at {Fmt(outlier.SelfTime)} ({multiplier:F1}x median).",
                    LikelyExplanation = "A target running much slower than its median across projects often reflects different inputs — source generators, analyzers, or file volume specific to that project. The multiplier alone does not identify which.",
                    InvestigationSuggestion = $"Compare {outlier.ProjectName} against projects with similar {outlier.Name} runtime to find what differs.",
                    Evidence = $"SelfTime={Fmt(outlier.SelfTime)}, Median={Fmt(median)}, Multiplier={multiplier:F2}x",
                    ThresholdName = $"{nameof(OutlierTargetMedianMultiplier)}={OutlierTargetMedianMultiplier:F0}x",
                });
            }
        }
    }

    private static void DetectCostlyResolvePackageAssets(BuildReport report, List<AnalysisFinding> findings)
    {
        var costly = report.TopTargets
            .Where(t => t.Name == "ResolvePackageAssets" && t.SelfTime.TotalSeconds > CostlyResolvePackageAssetsSeconds)
            .OrderByDescending(t => t.SelfTime)
            .ToList();

        if (costly.Count == 0) return;
        var max = costly[0];

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"ResolvePackageAssets expensive in {costly.Count} project(s), slowest {max.ProjectName}: {Fmt(max.SelfTime)}",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            UpperBoundImpactPercent = costly.Sum(c => c.SelfPercent),
            Measured = $"Slowest: {max.ProjectName} at {Fmt(max.SelfTime)}. {costly.Count} project(s) cross the {CostlyResolvePackageAssetsSeconds:F0}s threshold.",
            LikelyExplanation = null,
            InvestigationSuggestion = $"Run `dotnet nuget why {max.ProjectName} <heavy-package>` on its largest direct packages to locate transitive chains.",
            Evidence = $"Max.SelfTime={Fmt(max.SelfTime)}, Count={costly.Count}",
            ThresholdName = $"> {CostlyResolvePackageAssetsSeconds:F0}s",
        });
    }

    private static void DetectWarningConcentration(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.WarningCount < WarningsMinForFinding) return;

        var projectsWithWarnings = report.Projects
            .Where(p => p.WarningCount > 0)
            .OrderByDescending(p => p.WarningCount)
            .ToList();

        if (projectsWithWarnings.Count == 0) return;

        var concentrationRatio = (double)projectsWithWarnings.Count / Math.Max(1, report.Projects.Count);
        if (concentrationRatio > WarningConcentrationRatio) return;

        var attributedTotal = projectsWithWarnings.Sum(p => p.WarningCount);
        var topN = Math.Min(5, projectsWithWarnings.Count);
        var topWarnings = projectsWithWarnings.Take(topN).Sum(p => p.WarningCount);
        var remainingAttributed = attributedTotal - topWarnings;

        var topList = string.Join(", ",
            projectsWithWarnings.Take(topN).Select(p => $"{p.Name} ({p.WarningCount})"));

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Attributed warnings are concentrated in {projectsWithWarnings.Count} of {report.Projects.Count} projects",
            Severity = FindingSeverity.Info,
            Confidence = FindingConfidence.High,
            Measured = $"Of {report.WarningCount} total warnings, {report.AttributedWarningCount} are attributed to a specific project. " +
                       $"Top {topN} attributed sources: {topList}. Other attributed projects: {remainingAttributed}. " +
                       $"Unattributed: {report.UnattributedWarningCount}.",
            LikelyExplanation = null,
            InvestigationSuggestion = "Fix the top sources first — concentrated attributed warnings are easier to clean up than scattered ones.",
            Evidence = $"ProjectsWithWarnings={projectsWithWarnings.Count}, TotalProjects={report.Projects.Count}, ConcentrationRatio={concentrationRatio:F2}",
            ThresholdName = $"{nameof(WarningConcentrationRatio)}={WarningConcentrationRatio:F2}",
        });
    }

    private static void DetectBroadReferenceOverhead(BuildReport report, List<AnalysisFinding> findings)
    {
        var overhead = report.ReferenceOverhead;
        if (overhead is null) return;

        if (overhead.SelfPercent < ReferenceSelfPctMin) return;
        if (overhead.PayingProjectsPercent < PayingProjectsPctMin) return;
        if (overhead.MedianPerPayingProject.TotalMilliseconds < MedianReferencePerPayingProjectMinMs) return;

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = "Reference-related build work is broadly distributed",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.Medium,
            UpperBoundImpactPercent = overhead.SelfPercent,
            Measured = $"Reference-category targets (ResolveAssemblyReferences, ProcessFrameworkReferences, _HandlePackageFileConflicts, etc.) " +
                       $"account for {overhead.SelfPercent:F1}% of total self time ({Fmt(overhead.TotalSelfTime)}), " +
                       $"paid by {overhead.PayingProjectsCount} of {overhead.TotalProjectsCount} projects " +
                       $"(median {Fmt(overhead.MedianPerPayingProject)} per paying project).",
            LikelyExplanation = "Reference work spread across most projects can be a sign of fragmented dependency graphs — many small projects each paying a base reference-resolution cost. " +
                                "It could also reflect unusual target customisation or a dependency-heavy codebase. The distribution alone does not identify which.",
            InvestigationSuggestion = "Cross-reference this with the Dependency Hubs, per-project category composition, and Project Count Tax sections before concluding the solution shape is the cause.",
            Evidence = $"ReferenceSelfPct={overhead.SelfPercent:F1}%, PayingProjectsPct={overhead.PayingProjectsPercent:F0}%, MedianPerPaying={Fmt(overhead.MedianPerPayingProject)}",
            ThresholdName = $"{nameof(ReferenceSelfPctMin)}={ReferenceSelfPctMin:F0}%, {nameof(PayingProjectsPctMin)}={PayingProjectsPctMin:F0}%, {nameof(MedianReferencePerPayingProjectMinMs)}={MedianReferencePerPayingProjectMinMs:F0}ms",
        });
    }

    private static void DetectSpanWaitingPattern(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.SpanOutliers.Count < SpanOutliersMinCountForFinding) return;

        var examples = string.Join(", ",
            report.SpanOutliers.Take(4).Select(p => $"{p.Name} (span {Fmt(p.Span)}, self {Fmt(p.SelfTime)})"));

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"{report.SpanOutliers.Count} project(s) have span >> self time",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.Low,
            Measured = $"{report.SpanOutliers.Count} project(s) match the outlier rule (Span ≥ 5s, Span/SelfTime ≥ 5x, Span − SelfTime ≥ 3s). Examples: {examples}.",
            LikelyExplanation = "This pattern has several possible causes and the report cannot distinguish between them from timing alone: " +
                                "dependency waiting, SDK target orchestration, framework/reference work, static-web-assets pipelines, test/benchmark-specific build shape, or incremental-build effects.",
            InvestigationSuggestion = "Cross-reference the listed projects with the Dependency Hubs section and the per-project category composition to narrow down the cause.",
            Evidence = $"OutlierCount={report.SpanOutliers.Count}",
            ThresholdName = $"{nameof(SpanOutliersMinCountForFinding)}={SpanOutliersMinCountForFinding}",
        });
    }

    private static void DetectCriticalPathConcentration(BuildReport report, double totalSelfMs, List<AnalysisFinding> findings)
    {
        if (report.CriticalPath.Count == 0 || totalSelfMs <= 0) return;

        var cpSelfMs = report.CriticalPath.Sum(p => p.SelfTime.TotalMilliseconds);
        var cpPct = cpSelfMs / totalSelfMs * 100;
        if (cpPct < CriticalPathMinInterestingPct) return;

        var topThree = string.Join(" → ",
            report.CriticalPath.OrderByDescending(p => p.SelfTime).Take(3).Select(p => $"{p.Name} ({Fmt(p.SelfTime)})"));

        var testBenchmarkCount = report.CriticalPath.Count(p =>
            p.KindHeuristic == ProjectKind.Test || p.KindHeuristic == ProjectKind.Benchmark);
        var testBenchmarkNote = testBenchmarkCount > 0
            ? $" Note: {testBenchmarkCount} project(s) on the path are classified as test/benchmark by name-based heuristic — weight accordingly if they are not part of your primary optimization target."
            : "";

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Critical path estimate carries {cpPct:F1}% of total self time",
            Severity = FindingSeverity.Info,
            Confidence = FindingConfidence.Medium,
            Measured = $"The CPM estimate contains {report.CriticalPath.Count} projects totalling {Fmt(report.CriticalPathTotal)}. Highest-cost nodes: {topThree}.{testBenchmarkNote}",
            LikelyExplanation = "The critical path is derived from the observed project-reference DAG and measured self times. It is a model estimate — not a scheduler trace — and is only as accurate as the extracted dependencies and exclusive timing model.",
            InvestigationSuggestion = "Treat the path as a candidate list for sequential-ordering investigation. Verify that the dependency extraction looks right (graph health section) before using it to prioritise work.",
            Evidence = $"CriticalPathTotal={Fmt(report.CriticalPathTotal)}, CriticalPathPct={cpPct:F1}%, NodeCount={report.CriticalPath.Count}",
            ThresholdName = $"{nameof(CriticalPathMinInterestingPct)}={CriticalPathMinInterestingPct:F0}%",
        });
    }

    private static void DetectTfmNegotiationOverhead(BuildReport report, List<AnalysisFinding> findings)
    {
        var tfmTasks = report.TopTasks
            .Where(t => string.Equals(t.TargetName, TfmNegotiationTarget, StringComparison.Ordinal))
            .ToList();
        if (tfmTasks.Count == 0) return;

        var totalMs = tfmTasks.Sum(t => t.SelfTime.TotalMilliseconds);
        if (totalMs < TfmNegotiationAggregateSecondsThreshold * 1000) return;

        var edges = report.Graph.Health.TotalEdges;

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Reference framework lookup runs {Fmt(TimeSpan.FromMilliseconds(totalMs))} across {edges} project edges",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            Measured = $"_GetProjectReferenceTargetFrameworkProperties total: {Fmt(TimeSpan.FromMilliseconds(totalMs))} across {edges} ProjectReference edges.",
            LikelyExplanation = null,
            InvestigationSuggestion = "Add `SkipGetTargetFrameworkProperties=\"true\"` to same-TFM ProjectReferences via Directory.Build.targets, or try `dotnet build -graph`.",
            Evidence = $"TfmNegotiationTotal={Fmt(TimeSpan.FromMilliseconds(totalMs))}, Edges={edges}",
            ThresholdName = $"total > {TfmNegotiationAggregateSecondsThreshold:F0}s",
        });
    }

    private static void DetectBenchmarksOnCriticalPath(BuildReport report, List<AnalysisFinding> findings)
    {
        // Name-based classification is evidence, not proof — only emit when there's a specific
        // project to point at with measurable time on the blocking chain.
        var testBench = report.CriticalPath
            .Where(p => p.KindHeuristic == ProjectKind.Test || p.KindHeuristic == ProjectKind.Benchmark)
            .Where(p => p.SelfTime.TotalSeconds >= 10)
            .OrderByDescending(p => p.SelfTime)
            .ToList();
        if (testBench.Count == 0) return;

        var totalSelf = testBench.Sum(p => p.SelfTime.TotalMilliseconds);
        var totalSelfMs = report.Projects.Sum(p => p.SelfTime.TotalMilliseconds);
        var pct = totalSelfMs > 0 ? totalSelf / totalSelfMs * 100 : 0;

        var names = string.Join(", ", testBench.Select(p => $"{p.Name} ({Fmt(p.SelfTime)})"));
        var top = testBench[0];

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Test/benchmark projects on the blocking chain: {testBench.Count} · {Fmt(TimeSpan.FromMilliseconds(totalSelf))}",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            UpperBoundImpactPercent = pct,
            Measured = $"On the blocking chain: {names}. Named classification — verify before excluding.",
            LikelyExplanation = null,
            InvestigationSuggestion = $"Inspect {top.Name} .sln entry; if only needed for test/benchmark runs, remove its .Build.0 line under Debug|Any CPU.",
            Evidence = $"TestBenchmarkOnPath={testBench.Count}, CombinedSelfTime={Fmt(TimeSpan.FromMilliseconds(totalSelf))}",
            ThresholdName = "on blocking chain + self >= 10s",
        });
    }

    private static void DetectWarningHeavyCriticalPath(BuildReport report, List<AnalysisFinding> findings)
    {
        var heavy = report.CriticalPath
            .Where(p => p.WarningCount > WarningsOnCriticalPathMinPerProject)
            .OrderByDescending(p => p.WarningCount)
            .ToList();
        if (heavy.Count == 0) return;

        var top = heavy[0];
        var lines = string.Join(", ", heavy.Take(3).Select(p => $"{p.Name} ({p.WarningCount} warnings)"));
        var topPrefix = report.WarningsByPrefix.OrderByDescending(kv => kv.Value).FirstOrDefault();

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Warnings concentrated on the blocking chain: {lines}",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            Measured = $"Blocking-chain projects with >{WarningsOnCriticalPathMinPerProject} warnings each: {lines}. Most common warning prefix solution-wide: {topPrefix.Key} ({topPrefix.Value}).",
            LikelyExplanation = null,
            InvestigationSuggestion = $"Inspect warnings of type {topPrefix.Key} in {top.Name}. `dotnet build /warnaserror:{topPrefix.Key}` on a branch forces the fix.",
            Evidence = $"TopProject={top.Name}({top.WarningCount}), TopPrefix={topPrefix.Key}={topPrefix.Value}",
            ThresholdName = $"per-project warnings > {WarningsOnCriticalPathMinPerProject}",
        });
    }

    private static void DetectGenLoggingOutlier(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.AnalyzerReports.Count == 0) return;

        var solutionAvgMs = ComputeAverageGeneratorTime(report, KnownGenLoggingAssemblies);

        // Find per-project Gen.Logging time
        var outliers = new List<(AnalyzerReport Report, ProjectTiming? Project, TimeSpan GenTime, double Share)>();
        foreach (var ar in report.AnalyzerReports)
        {
            var genTime = ar.Generators
                .Where(g => KnownGenLoggingAssemblies.Contains(g.AssemblyName))
                .Sum(g => g.Time.TotalMilliseconds);
            if (genTime < GenLoggingOutlierMinSeconds * 1000) continue;

            var project = report.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, ar.ProjectName, StringComparison.OrdinalIgnoreCase));
            var projectSelfMs = project?.SelfTime.TotalMilliseconds ?? 0;
            if (projectSelfMs <= 0) continue;

            var share = genTime / projectSelfMs;
            if (share < GenLoggingOutlierProjectShareThreshold) continue;

            outliers.Add((ar, project, TimeSpan.FromMilliseconds(genTime), share));
        }
        if (outliers.Count == 0) return;

        var top = outliers.OrderByDescending(x => x.GenTime).First();
        var transitiveDependents = report.Graph.Nodes
            .FirstOrDefault(n => string.Equals(n.ProjectName, top.Project?.Name, StringComparison.OrdinalIgnoreCase))
            ?.TransitiveDependentsCount ?? 0;

        var projectName = top.Project?.Name ?? top.Report.ProjectName;
        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Gen.Logging dominates {projectName}: {Fmt(top.GenTime)} ({top.Share * 100:F0}% of its time)",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            UpperBoundImpactPercent = top.Project?.SelfPercent,
            Measured = $"{projectName}: Gen.Logging {Fmt(top.GenTime)} vs project {Fmt(top.Project?.SelfTime ?? TimeSpan.Zero)}. Solution average for Gen.Logging: ~{Fmt(TimeSpan.FromMilliseconds(solutionAvgMs))}.",
            LikelyExplanation = null,
            InvestigationSuggestion = $"Inspect [LoggerMessage] usage in {projectName}. If absent, find and drop the Microsoft.Extensions.Telemetry reference pulling it in.",
            Evidence = $"GenLoggingTime={Fmt(top.GenTime)}, ProjectSelf={Fmt(top.Project?.SelfTime ?? TimeSpan.Zero)}, Share={top.Share * 100:F0}%",
            ThresholdName = $">{GenLoggingOutlierMinSeconds:F0}s and >{GenLoggingOutlierProjectShareThreshold * 100:F0}% of project",
        });
    }

    private static void DetectComInterfaceGeneratorWithUsages(BuildReport report, List<AnalysisFinding> findings)
    {
        // Only fire when [GeneratedComInterface] is actually used. The no-op case (ran but
        // zero usages) is unavoidable SDK cost — not a finding, just trivia.
        if (report.GeneratedComInterfaceUsages.Count == 0) return;

        var totalMs = report.AnalyzerReports
            .SelectMany(r => r.Generators)
            .Where(g => KnownComInterfaceGeneratorAssemblies.Contains(g.AssemblyName))
            .Sum(g => g.Time.TotalMilliseconds);
        if (totalMs < ComInterfaceGeneratorMinSecondsForFinding * 1000) return;

        var usages = string.Join(", ", report.GeneratedComInterfaceUsages.Take(5))
                     + (report.GeneratedComInterfaceUsages.Count > 5 ? ", …" : "");
        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"ComInterfaceGenerator runs {Fmt(TimeSpan.FromMilliseconds(totalMs))} total across {report.GeneratedComInterfaceUsages.Count} project(s) with [GeneratedComInterface]",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            Measured = $"Projects using [GeneratedComInterface]: {usages}. Total summed generator time: {Fmt(TimeSpan.FromMilliseconds(totalMs))}.",
            LikelyExplanation = null,
            InvestigationSuggestion = $"Verify usages are intentional in: {usages}. Emitted code can be inspected with -p:EmitCompilerGeneratedFiles=true.",
            Evidence = $"ComInterfaceGenTotal={Fmt(TimeSpan.FromMilliseconds(totalMs))}, AttributeUsages={report.GeneratedComInterfaceUsages.Count}",
            ThresholdName = $"{nameof(ComInterfaceGeneratorMinSecondsForFinding)}={ComInterfaceGeneratorMinSecondsForFinding:F0}s",
        });
    }

    private static void DetectCSharpAnalyzersInNonRoslynProject(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.AnalyzerReports.Count == 0) return;

        var totalAnalyzerMs = report.AnalyzerReports.Sum(r => r.TotalAnalyzerTime.TotalMilliseconds);

        foreach (var ar in report.AnalyzerReports)
        {
            var csTime = ar.Analyzers
                .Where(a => string.Equals(a.AssemblyName, CSharpAnalyzersAssembly, StringComparison.OrdinalIgnoreCase))
                .Sum(a => a.Time.TotalMilliseconds);
            if (csTime < CSharpAnalyzersInNonRoslynMinSeconds * 1000) continue;

            var pct = totalAnalyzerMs > 0 ? csTime / totalAnalyzerMs * 100 : 0;
            findings.Add(new AnalysisFinding
            {
                Number = 0,
                Title = $"Roslyn compiler analyzer running in {ar.ProjectName}: {Fmt(TimeSpan.FromMilliseconds(csTime))}",
                Severity = FindingSeverity.Warning,
                Confidence = FindingConfidence.High,
                Measured = $"{ar.ProjectName}: Microsoft.CodeAnalysis.CSharp.Analyzers runs for {Fmt(TimeSpan.FromMilliseconds(csTime))} ({pct:F1}% of solution analyzer time). This analyzer targets Roslyn-extension projects, not application code.",
                LikelyExplanation = null,
                InvestigationSuggestion = $"Run `dotnet nuget why {ar.ProjectName} Microsoft.CodeAnalysis.CSharp.Analyzers`. On the introducing PackageReference, set <IncludeAssets>compile; runtime</IncludeAssets>.",
                Evidence = $"CSharpAnalyzersTime={Fmt(TimeSpan.FromMilliseconds(csTime))}, ShareOfAnalyzerTime={pct:F1}%",
                ThresholdName = $"> {CSharpAnalyzersInNonRoslynMinSeconds:F0}s",
            });
        }
    }

    private static void DetectHeavyTransitivesInTestOrBenchmark(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.ProjectDiagnoses.Count == 0) return;

        foreach (var d in report.ProjectDiagnoses)
        {
            var project = report.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, d.ProjectName, StringComparison.OrdinalIgnoreCase));
            if (project is null) continue;
            if (project.KindHeuristic is not (ProjectKind.Test or ProjectKind.Benchmark)) continue;
            if (d.Packages is null) continue;

            var heavyDirect = d.Packages.DirectPackages.Where(p => p.IsKnownHeavy).ToList();
            var heavyTransitive = d.Packages.TransitivePackages.Where(p => p.IsKnownHeavy).ToList();
            var heavyAll = heavyDirect.Concat(heavyTransitive).Distinct().ToList();
            if (heavyAll.Count == 0) continue;

            var heavyList = string.Join(", ", heavyAll.Select(p => p.Version is null ? p.Id : $"{p.Id} {p.Version}"));
            var heavyTransitiveByParent = heavyTransitive
                .Where(p => p.ParentPackage is not null)
                .GroupBy(p => p.ParentPackage!)
                .Select(g => $"{g.Key} → {string.Join(", ", g.Select(p => p.Id))}");
            var introducedBy = heavyTransitiveByParent.Any()
                ? $" Introduced via: {string.Join("; ", heavyTransitiveByParent)}."
                : "";

            findings.Add(new AnalysisFinding
            {
                Number = 0,
                Title = $"{d.ProjectName} pulls in heavy production packages: {heavyAll.Count}",
                Severity = FindingSeverity.Warning,
                Confidence = FindingConfidence.High,
                Measured = $"{d.ProjectName} ({(project.KindHeuristic == ProjectKind.Test ? "test" : "benchmark")}): heavy package(s) {heavyList}.{introducedBy}",
                LikelyExplanation = null,
                InvestigationSuggestion = $"Inspect direct ProjectReferences of {d.ProjectName}. Replace heavy refs with contracts/abstractions, or use <IncludeAssets>compile</IncludeAssets>.",
                Evidence = $"HeavyPackages={heavyAll.Count}, TransitiveTotal={d.Packages.TransitivePackages.Count}",
                ThresholdName = "test/benchmark + heavy package present",
            });
        }
    }

    private static double ComputeAverageGeneratorTime(BuildReport report, HashSet<string> assemblies)
    {
        var times = report.AnalyzerReports
            .SelectMany(r => r.Generators)
            .Where(g => assemblies.Contains(g.AssemblyName))
            .Select(g => g.Time.TotalMilliseconds)
            .ToList();
        return times.Count == 0 ? 0 : times.Average();
    }

    // ──────────────────────── Recommendations ──────────────────────

    private static IReadOnlyList<AnalysisRecommendation> GenerateRecommendations(
        List<AnalysisFinding> findings, BuildReport report)
    {
        var recs = new List<AnalysisRecommendation>();

        // Each finding contributes its own actionable recommendation. The dispatch is keyed
        // on finding Title so adding a new finding + its recommendation stays co-located.
        foreach (var f in findings)
        {
            if (f.Severity == FindingSeverity.Info) continue;
            var text = RecommendationFor(f, report);
            if (text is not null)
                recs.Add(new AnalysisRecommendation { Number = 0, Text = text });
        }

        // Cross-finding rules (don't map 1:1 to a single finding).
        foreach (var cross in CrossFindingRecommendations(findings, report))
            recs.Add(new AnalysisRecommendation { Number = 0, Text = cross });

        for (int i = 0; i < recs.Count; i++)
            recs[i] = recs[i] with { Number = i + 1 };

        return recs;
    }

    private static string? RecommendationFor(AnalysisFinding f, BuildReport report)
    {
        if (f.Title.StartsWith("_GetProjectReferenceTargetFrameworkProperties"))
        {
            return "Add `SkipGetTargetFrameworkProperties=\"true\"` to ProjectReferences that do not need TFM negotiation (automate via Directory.Build.targets for same-TFM projects). Also try `dotnet build -graph` to switch to static graph evaluation.";
        }
        if (f.Title.Contains("test/benchmark project") && f.Title.Contains("critical path"))
        {
            var names = string.Join(", ", report.CriticalPath
                .Where(p => p.KindHeuristic == ProjectKind.Test || p.KindHeuristic == ProjectKind.Benchmark)
                .Select(p => p.Name));
            return $"Exclude {names} from default solution builds (Solution Configuration, .sln .Build.0 edit, or `<ExcludeFromBuild>` conditional in the csproj). Measure the wall-clock delta before committing — the DAG topology determines whether the leaf was actually gating anything.";
        }
        if (f.Title.StartsWith("Largest self-time share"))
        {
            var top = report.Projects.FirstOrDefault();
            if (top is null) return null;
            var topTargets = top.Targets.Count > 0
                ? string.Join(", ", top.Targets.Take(3).Select(t => t.Name))
                : "its top targets";
            return $"Investigate {top.Name}: start with {topTargets}. Profile the targets holding most of the {Fmt(top.SelfTime)} of self time before deciding whether anything needs to change.";
        }
        if (f.Title.StartsWith("Largest self-time gap"))
        {
            var top = report.Projects.FirstOrDefault();
            return top is null ? null :
                $"Investigate {top.Name}'s target breakdown to understand the gap. Do not conclude structural changes are needed from timing data alone.";
        }
        if (f.Title.Contains("ResolvePackageAssets"))
        {
            return "Investigate NuGet dependency graphs for the affected projects. Run `dotnet nuget why <PackageId>` on heavy packages before changing references.";
        }
        if (f.Title.StartsWith("Target outlier"))
        {
            return $"Investigate: {f.Title}. Compare against projects with similar target runtime to find what differs (source generators, analyzers, file volume).";
        }
        if (f.Title.StartsWith("Reference-related build work"))
        {
            return "Investigate the dependency hubs, per-project category composition, and the Project Count Tax section together before concluding the solution shape is responsible.";
        }
        if (f.Title.Contains("span >> self time"))
        {
            return "Cross-reference the span outliers with the Dependency Hubs and per-project category composition. The pattern has several possible causes and needs the graph context to narrow down.";
        }
        return null;
    }

    private static IEnumerable<string> CrossFindingRecommendations(List<AnalysisFinding> findings, BuildReport report)
    {
        // Critical-path serial bottleneck — parallelism can't help unless the chain breaks.
        if (report.CriticalPath.Count > 0 && report.TotalDuration.TotalMilliseconds > 0)
        {
            var cpRatio = report.CriticalPathTotal.TotalMilliseconds / report.TotalDuration.TotalMilliseconds;
            if (cpRatio >= 0.70)
                yield return $"Critical path is {cpRatio * 100:F0}% of wall clock — the build is essentially serial. Adding parallelism (-m:N, MSBuildNodeCount) cannot shorten wall time until a dependency in the chain is broken. Focus on shortening the path, not widening it.";
        }

        // Heavy generator packages (>10s solution-wide) → auditing hint.
        if (report.AnalyzerReports.Count > 0)
        {
            var generatorsBySolution = report.AnalyzerReports
                .SelectMany(r => r.Generators)
                .GroupBy(e => e.AssemblyName)
                .Select(g => new { Name = g.Key, Total = TimeSpan.FromMilliseconds(g.Sum(e => e.Time.TotalMilliseconds)) })
                .OrderByDescending(x => x.Total)
                .FirstOrDefault();
            if (generatorsBySolution is { } top && top.Total.TotalSeconds >= 10)
                yield return $"Top generator '{top.Name}' consumes {Fmt(top.Total)} solution-wide. Audit whether every referencing project actually uses it — source generators run in every project that references their package, even if no attributes are present in that project's source.";
        }

        // High warning volume — actionable breakdown path.
        if (report.WarningCount >= 500 && report.WarningsByCode.Count > 0)
        {
            var top = report.WarningsByCode.First();
            yield return $"High warning volume ({report.WarningCount} total). Top source: {top.Code} ({top.Count}). Fix the top code first — concentrated warnings are faster to clean up than scattered ones. Use `dotnet build /warnaserror:{top.Code}` on a branch to turn the top code into errors and force the fix.";
        }
    }

    private static string Fmt(TimeSpan ts) => ConsoleReportRenderer.FormatDuration(ts);

    private static string CategoryLabel(TargetCategory category) => category switch
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
}
