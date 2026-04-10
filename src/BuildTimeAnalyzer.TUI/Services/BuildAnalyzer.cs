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
            Measured = $"{top.Name} accounts for {top.SelfPercent:F1}% of total self time ({Fmt(top.SelfTime)}).{targetPhrase}",
            LikelyExplanation = null,
            InvestigationSuggestion = $"Profile {top.Name}'s top targets before deciding whether anything needs to change.",
            Evidence = $"SelfPercent={top.SelfPercent:F1}%, SelfTime={Fmt(top.SelfTime)}",
            ThresholdName = $"{thresholdName}={thresholdValue:F0}%",
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
            Measured = $"{first.Name} has {Fmt(first.SelfTime)} of self time; the next project ({second.Name}) has {Fmt(second.SelfTime)}. Ratio: {ratio:F1}x.",
            LikelyExplanation = null,
            InvestigationSuggestion = $"Review {first.Name}'s target breakdown to understand what is driving the gap.",
            Evidence = $"Ratio={ratio:F2}, First={Fmt(first.SelfTime)}, Second={Fmt(second.SelfTime)}",
            ThresholdName = $"{thresholdName}={thresholdValue:F1}x",
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
            Measured = $"The CPM estimate contains {report.CriticalPath.Count} projects totalling {Fmt(report.CriticalPathTotal)}. Highest-cost nodes: {topThree}.{testBenchmarkNote}",
            LikelyExplanation = "The critical path is derived from the observed project-reference DAG and measured self times. It is a model estimate — not a scheduler trace — and is only as accurate as the extracted dependencies and exclusive timing model.",
            InvestigationSuggestion = "Treat the path as a candidate list for sequential-ordering investigation. Verify that the dependency extraction looks right (graph health section) before using it to prioritise work.",
            Evidence = $"CriticalPathTotal={Fmt(report.CriticalPathTotal)}, CriticalPathPct={cpPct:F1}%, NodeCount={report.CriticalPath.Count}",
            ThresholdName = $"{nameof(CriticalPathMinInterestingPct)}={CriticalPathMinInterestingPct:F0}%",
        });
    }

    // ──────────────────────── Recommendations ──────────────────────

    private static IReadOnlyList<AnalysisRecommendation> GenerateRecommendations(
        List<AnalysisFinding> findings, BuildReport report)
    {
        var recs = new List<AnalysisRecommendation>();

        foreach (var f in findings)
        {
            if (f.Severity == FindingSeverity.Info) continue;

            if (f.Title.StartsWith("Largest self-time share"))
            {
                var top = report.Projects.FirstOrDefault();
                if (top is null) continue;
                var topTargets = top.Targets.Count > 0
                    ? string.Join(", ", top.Targets.Take(3).Select(t => t.Name))
                    : "its top targets";
                recs.Add(new AnalysisRecommendation
                {
                    Number = 0,
                    Text = $"Investigate {top.Name}: start with {topTargets}. Profile the targets holding most of the {Fmt(top.SelfTime)} of self time before deciding whether anything needs to change.",
                });
            }
            else if (f.Title.StartsWith("Largest self-time gap"))
            {
                var top = report.Projects.FirstOrDefault();
                if (top is null) continue;
                recs.Add(new AnalysisRecommendation
                {
                    Number = 0,
                    Text = $"Investigate {top.Name}'s target breakdown to understand the gap. Do not conclude structural changes are needed from timing data alone.",
                });
            }
            else if (f.Title.Contains("ResolvePackageAssets"))
            {
                recs.Add(new AnalysisRecommendation
                {
                    Number = 0,
                    Text = "Investigate NuGet dependency graphs for the affected projects. Run `dotnet nuget why` on heavy packages before changing references.",
                });
            }
            else if (f.Title.StartsWith("Target outlier"))
            {
                recs.Add(new AnalysisRecommendation
                {
                    Number = 0,
                    Text = $"Investigate: {f.Title}. Compare against projects with similar target runtime to find what differs (source generators, analyzers, file volume).",
                });
            }
            else if (f.Title.StartsWith("Reference-related build work"))
            {
                recs.Add(new AnalysisRecommendation
                {
                    Number = 0,
                    Text = "Investigate the dependency hubs, per-project category composition, and the Project Count Tax section together before concluding the solution shape is responsible.",
                });
            }
            else if (f.Title.Contains("span >> self time"))
            {
                recs.Add(new AnalysisRecommendation
                {
                    Number = 0,
                    Text = "Cross-reference the span outliers with the Dependency Hubs and per-project category composition. The pattern has several possible causes and needs the graph context to narrow down.",
                });
            }
        }

        for (int i = 0; i < recs.Count; i++)
            recs[i] = recs[i] with { Number = i + 1 };

        return recs;
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
