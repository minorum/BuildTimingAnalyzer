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

        DetectLargestShare(report, findings);
        DetectLargestGap(report, findings);
        DetectOutlierTargets(report, findings);
        DetectCostlyResolvePackageAssets(report, findings);
        DetectWarningConcentration(report, findings);
        DetectBroadReferenceOverhead(report, findings);
        DetectSpanWaitingPattern(report, findings);
        DetectCriticalPathConcentration(report, totalSelfMs, findings);
        DetectTfmNegotiationOverhead(report, findings);
        DetectBenchmarksOnCriticalPath(report, findings);
        DetectWarningHeavyCriticalPath(report, findings);
        DetectGenLoggingOutlier(report, findings);
        DetectComInterfaceGeneratorNoOp(report, findings);
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

        var recommendations = GenerateRecommendations(findings, report);

        return new BuildAnalysis { Findings = findings, Recommendations = recommendations };
    }

    // ──────────────────────────── Findings ────────────────────────────

    private static void DetectLargestShare(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.Projects.Count < 2) return;

        var top = report.Projects[0];
        if (top.SelfPercent <= LargestShareWarningPct) return;

        var isCritical = top.SelfPercent > LargestShareCriticalPct;
        var severity = isCritical ? FindingSeverity.Critical : FindingSeverity.Warning;
        var thresholdName = isCritical ? nameof(LargestShareCriticalPct) : nameof(LargestShareWarningPct);
        var thresholdValue = isCritical ? LargestShareCriticalPct : LargestShareWarningPct;

        var topTargets = top.Targets.Take(2).Select(t => $"{t.Name} ({Fmt(t.SelfTime)}, {CategoryLabel(t.Category)})").ToList();
        var targetPhrase = topTargets.Count > 0 ? $" Top targets: {string.Join(", ", topTargets)}." : "";

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Largest self-time share: {top.Name}",
            Severity = severity,
            Confidence = FindingConfidence.High,
            Measured = $"{top.Name} accounts for {top.SelfPercent:F1}% of total self time ({Fmt(top.SelfTime)}).{targetPhrase}",
            LikelyExplanation = null,
            InvestigationSuggestion = $"Profile {top.Name}'s top targets before deciding whether anything needs to change.",
            Evidence = $"SelfPercent={top.SelfPercent:F1}%, SelfTime={Fmt(top.SelfTime)}",
            ThresholdName = $"{thresholdName}={thresholdValue:F0}%",
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
            Title = $"Expensive ResolvePackageAssets: {costly.Count} project(s)",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            UpperBoundImpactPercent = costly.Sum(c => c.SelfPercent),
            Measured = $"{costly.Count} project(s) cross the {CostlyResolvePackageAssetsSeconds:F0}s threshold. Slowest: {max.ProjectName} at {Fmt(max.SelfTime)}.",
            LikelyExplanation = "ResolvePackageAssets cost typically scales with the transitive NuGet graph size, but this is a correlation — not proof. High cost does not directly identify which packages are responsible.",
            InvestigationSuggestion = "Run `dotnet nuget why` on heavy packages in the affected projects to locate transitive chains.",
            Evidence = $"Max.SelfTime={Fmt(max.SelfTime)}, Count={costly.Count}",
            ThresholdName = $"{nameof(CostlyResolvePackageAssetsSeconds)}={CostlyResolvePackageAssetsSeconds:F0}s",
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
        var longestChain = report.Graph.LongestChainProjectCount;
        var projectCount = report.Projects.Count;

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = "_GetProjectReferenceTargetFrameworkProperties overhead from deep dependency graph",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.Medium,
            Measured = $"Appears {tfmTasks.Count}x in top tasks, total ~{Fmt(TimeSpan.FromMilliseconds(totalMs))}. With {edges} edge(s) across {projectCount} project(s), this runs at least {edges} times.",
            LikelyExplanation = $"This is the MSBuild two-phase TargetFramework negotiation protocol — for each ProjectReference, MSBuild asks the referenced project which TargetFramework to build as before resolving outputs. Cost scales with edge count in the dependency graph. A {longestChain}-project longest chain means up to {longestChain} sequential evaluations on the critical path.",
            InvestigationSuggestion = "1) For ProjectReferences where cross-targeting is not needed, add `SkipGetTargetFrameworkProperties=\"true\"` metadata (automate via Directory.Build.targets for same-TFM projects). 2) Try static graph mode: `dotnet build -graph <solution>` — pre-computes the DAG and can skip runtime negotiation. 3) Excluding test/benchmark projects from the default build removes their reference edges and shrinks this overhead.",
            Evidence = $"TfmNegotiationCount={tfmTasks.Count}, TotalTfmTime={Fmt(TimeSpan.FromMilliseconds(totalMs))}, Edges={edges}",
            ThresholdName = $"{nameof(TfmNegotiationAggregateSecondsThreshold)}={TfmNegotiationAggregateSecondsThreshold:F0}s",
        });
    }

    private static void DetectBenchmarksOnCriticalPath(BuildReport report, List<AnalysisFinding> findings)
    {
        var testBench = report.CriticalPath
            .Where(p => p.KindHeuristic == ProjectKind.Test || p.KindHeuristic == ProjectKind.Benchmark)
            .ToList();
        if (testBench.Count == 0) return;

        var totalSelf = testBench.Sum(p => p.SelfTime.TotalMilliseconds);
        var totalSelfMs = report.Projects.Sum(p => p.SelfTime.TotalMilliseconds);
        var pct = totalSelfMs > 0 ? totalSelf / totalSelfMs * 100 : 0;

        var names = string.Join(", ", testBench.Select(p => $"{p.Name} ({Fmt(p.SelfTime)})"));

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"{testBench.Count} test/benchmark project(s) on the critical path",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.Medium,
            UpperBoundImpactPercent = pct,
            Measured = $"Projects on the critical path classified by name-based heuristic as test or benchmark: {names}. Combined self time: {Fmt(TimeSpan.FromMilliseconds(totalSelf))} ({pct:F1}% of total self time).",
            LikelyExplanation = "Test and benchmark projects extend the critical path for every full-solution build, even though they are only needed when tests/benchmarks actually run. Excluding them from the default dev/CI build shortens wall-clock time without affecting production output.",
            InvestigationSuggestion = "Option A — Solution Configuration: create a BuildOnly configuration that unchecks Tests/Benchmarks. Option B — edit the .sln directly, remove the .Build.0 entry for those project GUIDs under Debug|Any CPU. Option C — conditional `<ExcludeFromBuild>` property in the project, toggled by a `$(ExcludeBenchmarks)` flag. Measure before committing: the DAG topology matters, removing a leaf does nothing if it wasn't actually gating a dependency.",
            Evidence = $"TestBenchmarkOnPath={testBench.Count}, CombinedSelfTime={Fmt(TimeSpan.FromMilliseconds(totalSelf))}",
            ThresholdName = "on critical path",
        });
    }

    private static void DetectWarningHeavyCriticalPath(BuildReport report, List<AnalysisFinding> findings)
    {
        var heavy = report.CriticalPath
            .Where(p => p.WarningCount > WarningsOnCriticalPathMinPerProject)
            .OrderByDescending(p => p.WarningCount)
            .ToList();
        if (heavy.Count == 0) return;

        var lines = string.Join(", ", heavy.Take(5).Select(p =>
            $"{p.Name} ({p.WarningCount} warnings, {Fmt(p.SelfTime)} self)"));
        var topPrefixes = report.WarningsByPrefix
            .OrderByDescending(kv => kv.Value)
            .Take(3)
            .Select(kv => $"{kv.Key} {kv.Value}");
        var prefixSummary = string.Join(" · ", topPrefixes);

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"{heavy.Count} warning-heavy project(s) on the critical path",
            Severity = FindingSeverity.Critical,
            Confidence = FindingConfidence.High,
            Measured = $"Critical path includes projects with high warning counts: {lines}. Solution-wide warning prefix breakdown: {prefixSummary}.",
            LikelyExplanation = "CS nullable warnings (CS8600–CS8629) extend Roslyn's type-inference work on every compile because the analyzer must walk the type graph to compute flow state. CA warnings have lower per-build cost but indicate analyzers running in projects on the slow path.",
            InvestigationSuggestion = "Fix the top-prefix warnings on critical-path projects first — they reduce both build time and warning noise. Use `dotnet build /warnaserror:CS8600` on a branch to force the fix on the most common nullable warnings.",
            Evidence = $"WarningHeavyCount={heavy.Count}, TopByCount={heavy[0].Name}({heavy[0].WarningCount})",
            ThresholdName = $"{nameof(WarningsOnCriticalPathMinPerProject)}={WarningsOnCriticalPathMinPerProject}",
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

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Gen.Logging cost outlier in {top.Project?.Name ?? top.Report.ProjectName}",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            UpperBoundImpactPercent = top.Project?.SelfPercent,
            Measured = $"{top.Project?.Name ?? top.Report.ProjectName}: Gen.Logging {Fmt(top.GenTime)} vs project self {Fmt(top.Project?.SelfTime ?? TimeSpan.Zero)} ({top.Share * 100:F0}%). Solution average for Gen.Logging: ~{Fmt(TimeSpan.FromMilliseconds(solutionAvgMs))}. Transitive dependents: {transitiveDependents}.",
            LikelyExplanation = "Microsoft.Gen.Logging runs only when [LoggerMessage] attributes are present in the compilation unit. A project where its cost dominates self time either has heavy LoggerMessage usage or is unnecessarily pulling in Microsoft.Extensions.Telemetry.",
            InvestigationSuggestion = $"Audit [LoggerMessage] usage in {top.Project?.Name ?? top.Report.ProjectName}. If genuinely needed, the cost is real. If not, find the package introducing Microsoft.Extensions.Telemetry (likely a shared abstractions library) and either drop the package or set <IncludeAssets>compile; runtime</IncludeAssets> on it.",
            Evidence = $"GenLoggingTime={Fmt(top.GenTime)}, ProjectSelf={Fmt(top.Project?.SelfTime ?? TimeSpan.Zero)}, Share={top.Share * 100:F0}%",
            ThresholdName = $"{nameof(GenLoggingOutlierMinSeconds)}={GenLoggingOutlierMinSeconds:F0}s & share>={GenLoggingOutlierProjectShareThreshold:F0}",
        });
    }

    private static void DetectComInterfaceGeneratorNoOp(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.AnalyzerReports.Count == 0) return;

        var totalMs = report.AnalyzerReports
            .SelectMany(r => r.Generators)
            .Where(g => KnownComInterfaceGeneratorAssemblies.Contains(g.AssemblyName))
            .Sum(g => g.Time.TotalMilliseconds);
        if (totalMs < ComInterfaceGeneratorMinSecondsForFinding * 1000) return;

        var projectsRunningIt = report.AnalyzerReports
            .Count(r => r.Generators.Any(g => KnownComInterfaceGeneratorAssemblies.Contains(g.AssemblyName)));
        var usagesFound = report.GeneratedComInterfaceUsages.Count;
        var isConfirmedNoOp = usagesFound == 0;

        var conclusion = isConfirmedNoOp
            ? "0 usages of [GeneratedComInterface] found in source files — confirmed no-op. Cost is unavoidable without modifying SDK targets."
            : $"Usages of [GeneratedComInterface] found in: {string.Join(", ", report.GeneratedComInterfaceUsages.Take(5))}{(usagesFound > 5 ? ", …" : "")} — generator is doing real work in those projects.";

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"ComInterfaceGenerator: {Fmt(TimeSpan.FromMilliseconds(totalMs))} CPU across {projectsRunningIt} project(s) — {(isConfirmedNoOp ? "confirmed no-op" : $"{usagesFound} project(s) actually using it")}",
            Severity = isConfirmedNoOp ? FindingSeverity.Info : FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            Measured = $"Microsoft.Interop.ComInterfaceGenerator ran in {projectsRunningIt} project(s) for {Fmt(TimeSpan.FromMilliseconds(totalMs))} CPU-summed. {conclusion}",
            LikelyExplanation = isConfirmedNoOp
                ? "ComInterfaceGenerator ships with the .NET SDK and runs in every project regardless of whether [GeneratedComInterface] is used. The work is genuinely no-op when no attributes are present, but the generator is still loaded and given a chance to scan."
                : "Cost is justified — generator is producing P/Invoke marshalling code in the listed projects.",
            InvestigationSuggestion = isConfirmedNoOp
                ? "No action needed. The generator cannot be disabled without modifying SDK targets. Consider this a baseline cost of using .NET 8+ SDK."
                : "Verify usages are intentional. ComInterfaceGenerator-emitted code can be inspected via -p:EmitCompilerGeneratedFiles=true.",
            Evidence = $"ComInterfaceGenCpu={Fmt(TimeSpan.FromMilliseconds(totalMs))}, Projects={projectsRunningIt}, AttributeUsages={usagesFound}",
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
                Title = $"Roslyn compiler analyzer running in {ar.ProjectName}",
                Severity = FindingSeverity.Warning,
                Confidence = FindingConfidence.Medium,
                Measured = $"{ar.ProjectName} runs Microsoft.CodeAnalysis.CSharp.Analyzers for {Fmt(TimeSpan.FromMilliseconds(csTime))} ({pct:F1}% of solution analyzer time).",
                LikelyExplanation = "Microsoft.CodeAnalysis.CSharp.Analyzers targets Roslyn-extension projects (analyzer/source-generator authors). In a regular application or library, it is almost certainly being pulled in transitively by a package that doesn't actually need it.",
                InvestigationSuggestion = "Find the introducing package with `dotnet nuget why <project> Microsoft.CodeAnalysis.CSharp.Analyzers`. Then on the introducing PackageReference, set `<IncludeAssets>compile; runtime</IncludeAssets>` to keep the package's library code without pulling in its analyzers.",
                Evidence = $"CSharpAnalyzersTime={Fmt(TimeSpan.FromMilliseconds(csTime))}, ShareOfAnalyzerTime={pct:F1}%",
                ThresholdName = $"{nameof(CSharpAnalyzersInNonRoslynMinSeconds)}={CSharpAnalyzersInNonRoslynMinSeconds:F0}s",
            });
            // One finding per offending project — don't double up across the analyzer table.
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
                Title = $"Heavy production packages in non-production project: {d.ProjectName}",
                Severity = FindingSeverity.Warning,
                Confidence = FindingConfidence.Medium,
                Measured = $"{d.ProjectName} ({(project.KindHeuristic == ProjectKind.Test ? "test" : "benchmark")}) pulls in heavy package(s): {heavyList}. Total transitive package count: {d.Packages.TransitivePackages.Count}.{introducedBy}",
                LikelyExplanation = "Test and benchmark projects that depend on production packages inherit their full source-generator and analyzer cost. Replacing the production reference with a contracts/abstractions project (or a production project that doesn't have the heavy generator) keeps compile-time-only data without the runtime/generator weight.",
                InvestigationSuggestion = "Inspect the direct ProjectReferences of this project. Replace any reference to a heavy production project with a reference to its contracts/abstractions project, or use <IncludeAssets>compile</IncludeAssets> on the introducing PackageReference.",
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
