using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Rendering;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Evidence-driven analysis. Every finding cites a concrete metric and a named threshold
/// constant. Recommendations are investigation targets only — never structural conclusions
/// from timing data alone.
/// </summary>
public static class BuildAnalyzer
{
    // ── Named thresholds ─────────────────────────────────────────────

    private const double BottleneckCriticalPct = 25.0;
    private const double BottleneckWarningPct = 15.0;
    private const double DisproportionCriticalRatio = 2.5;
    private const double DisproportionWarningRatio = 1.8;

    private const double OutlierTargetMedianMultiplier = 4.0;
    private const double OutlierTargetMinSeconds = 2.0;

    private const double CostlyResolvePackageAssetsSeconds = 3.0;

    private const int WarningsMinForFinding = 20;
    private const double WarningConcentrationRatio = 0.30;

    private const double CriticalPathMinInterestingPct = 30.0;

    // Reference overhead — strict "systemic, not isolated" thresholds
    private const double ReferenceSelfPctMin = 10.0;
    private const double PayingProjectsPctMin = 50.0;
    private const double MedianReferencePerPayingProjectMinMs = 250.0;

    // Span-vs-self waiting pattern — fire when multiple projects match the outlier rule
    private const int SpanOutliersMinCountForFinding = 3;

    public static BuildAnalysis Analyze(BuildReport report)
    {
        if (report.TotalDuration < TimeSpan.FromSeconds(1))
            return new BuildAnalysis { Findings = [], Recommendations = [] };

        var findings = new List<AnalysisFinding>();

        var totalSelfMs = report.Projects.Sum(p => p.SelfTime.TotalMilliseconds);

        DetectBottleneckProject(report, findings);
        DetectDisproportionateProject(report, findings);
        DetectOutlierTargets(report, findings);
        DetectCostlyResolvePackageAssets(report, findings);
        DetectWarningConcentration(report, findings);
        DetectSystemicReferenceOverhead(report, findings);
        DetectSpanWaitingPattern(report, findings);
        DetectCriticalPathConcentration(report, totalSelfMs, findings);

        for (int i = 0; i < findings.Count; i++)
            findings[i] = findings[i] with { Number = i + 1 };

        var recommendations = GenerateRecommendations(findings, report);

        return new BuildAnalysis
        {
            Findings = findings,
            Recommendations = recommendations,
        };
    }

    // ──────────────────────────── Findings ────────────────────────────

    private static void DetectBottleneckProject(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.Projects.Count < 2) return;

        var top = report.Projects[0];
        if (top.SelfPercent <= BottleneckWarningPct) return;

        var isCritical = top.SelfPercent > BottleneckCriticalPct;
        var severity = isCritical ? FindingSeverity.Critical : FindingSeverity.Warning;
        var thresholdName = isCritical ? nameof(BottleneckCriticalPct) : nameof(BottleneckWarningPct);
        var thresholdValue = isCritical ? BottleneckCriticalPct : BottleneckWarningPct;

        var topTargetsForProject = top.Targets.Count > 0
            ? top.Targets.Take(2).ToList()
            : report.TopTargets.Where(t => t.ProjectName == top.Name).Take(2).ToList();

        var targetDetails = topTargetsForProject.Count > 0
            ? " Top targets: " + string.Join(", ", topTargetsForProject.Select(t => $"{t.Name} ({Fmt(t.SelfTime)}, {CategoryLabel(t.Category)})")) + "."
            : "";

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"{top.Name} holds the largest share of self time",
            Detail = $"It accounts for {top.SelfPercent:F1}% of total self time ({Fmt(top.SelfTime)}).{targetDetails} Investigate its top targets to understand where the time is going.",
            Severity = severity,
            Evidence = $"SelfPercent={top.SelfPercent:F1}%, SelfTime={Fmt(top.SelfTime)}",
            ThresholdName = $"{thresholdName}={thresholdValue:F0}%",
        });
    }

    private static void DetectDisproportionateProject(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.Projects.Count < 2) return;

        var first = report.Projects[0];
        var second = report.Projects[1];

        if (second.SelfTime.TotalMilliseconds <= 0) return;
        var ratio = first.SelfTime.TotalMilliseconds / second.SelfTime.TotalMilliseconds;

        if (ratio < DisproportionWarningRatio) return;

        var isCritical = ratio >= DisproportionCriticalRatio;
        var severity = isCritical ? FindingSeverity.Critical : FindingSeverity.Warning;
        var thresholdName = isCritical ? nameof(DisproportionCriticalRatio) : nameof(DisproportionWarningRatio);
        var thresholdValue = isCritical ? DisproportionCriticalRatio : DisproportionWarningRatio;

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"{first.Name} self time is {ratio:F1}x the next project",
            Detail = $"{first.Name} has {Fmt(first.SelfTime)} of self time versus {second.Name} at {Fmt(second.SelfTime)}. Investigate what targets are driving this — the gap is wide enough to be worth explaining.",
            Severity = severity,
            Evidence = $"Ratio={ratio:F2}, First.SelfTime={Fmt(first.SelfTime)}, Second.SelfTime={Fmt(second.SelfTime)}",
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
                    Title = $"{outlier.Name} in {outlier.ProjectName} is an outlier",
                    Detail = $"The median {outlier.Name} self time across projects is {Fmt(median)}, but {outlier.ProjectName} runs at {Fmt(outlier.SelfTime)} ({multiplier:F1}x median). Investigate why this project's {outlier.Name} behaves differently.",
                    Severity = FindingSeverity.Warning,
                    Evidence = $"SelfTime={Fmt(outlier.SelfTime)}, Median={Fmt(median)}, Multiplier={multiplier:F2}x",
                    ThresholdName = $"{nameof(OutlierTargetMedianMultiplier)}={OutlierTargetMedianMultiplier:F0}x",
                });
            }
        }
    }

    private static void DetectCostlyResolvePackageAssets(BuildReport report, List<AnalysisFinding> findings)
    {
        var costly = report.TopTargets
            .Where(t => t.Name == "ResolvePackageAssets" &&
                        t.SelfTime.TotalSeconds > CostlyResolvePackageAssetsSeconds)
            .OrderByDescending(t => t.SelfTime)
            .ToList();

        if (costly.Count == 0) return;

        var max = costly[0];

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"ResolvePackageAssets is expensive in {costly.Count} project(s)",
            Detail = $"Slowest: {max.ProjectName} at {Fmt(max.SelfTime)}. This target resolves NuGet package assets and is typically proportional to the transitive dependency graph size. Investigate by running `dotnet nuget why` on heavy packages.",
            Severity = FindingSeverity.Warning,
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
        var isConcentrated = concentrationRatio <= WarningConcentrationRatio;

        if (!isConcentrated) return;

        var attributedTotal = projectsWithWarnings.Sum(p => p.WarningCount);
        var topN = Math.Min(5, projectsWithWarnings.Count);
        var topWarnings = projectsWithWarnings.Take(topN).Sum(p => p.WarningCount);
        var remainingAttributed = attributedTotal - topWarnings;

        var topList = string.Join(", ",
            projectsWithWarnings.Take(topN).Select(p => $"{p.Name} ({p.WarningCount})"));

        // Finding explicitly labels its scope as "attributed" — does not imply the full warning total
        var detail = $"Top {topN} attributed sources: {topList}. Other attributed projects: {remainingAttributed}. " +
                     $"Total attributed: {attributedTotal} of {report.WarningCount} warnings " +
                     $"({report.UnattributedWarningCount} could not be attributed to a specific project). " +
                     $"Concentrated attributed warnings are easier to fix than scattered ones — investigate the top sources.";

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Attributed warnings are concentrated in {projectsWithWarnings.Count} of {report.Projects.Count} projects",
            Detail = detail,
            Severity = FindingSeverity.Info,
            Evidence = $"ProjectsWithWarnings={projectsWithWarnings.Count}, TotalProjects={report.Projects.Count}, ConcentrationRatio={concentrationRatio:F2}",
            ThresholdName = $"{nameof(WarningConcentrationRatio)}={WarningConcentrationRatio:F2}",
        });
    }

    private static void DetectSystemicReferenceOverhead(BuildReport report, List<AnalysisFinding> findings)
    {
        var overhead = report.ReferenceOverhead;
        if (overhead is null) return;

        // Strict "systemic, not isolated" — all three thresholds must hold
        if (overhead.SelfPercent < ReferenceSelfPctMin) return;
        if (overhead.PayingProjectsPercent < PayingProjectsPctMin) return;
        if (overhead.MedianPerPayingProject.TotalMilliseconds < MedianReferencePerPayingProjectMinMs) return;

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = "Reference-related build work appears to be systemic, not isolated",
            Detail = $"Reference-category targets (ResolveAssemblyReferences, ProcessFrameworkReferences, _HandlePackageFileConflicts, etc.) account for "
                   + $"{overhead.SelfPercent:F1}% of total self time ({Fmt(overhead.TotalSelfTime)}) across "
                   + $"{overhead.PayingProjectsCount} of {overhead.TotalProjectsCount} projects "
                   + $"(median {Fmt(overhead.MedianPerPayingProject)} per paying project). "
                   + "Investigate whether the solution's project count and dependency graph are causing repeated reference-resolution overhead across many small projects.",
            Severity = FindingSeverity.Warning,
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
            Title = $"{report.SpanOutliers.Count} project(s) have long span relative to low self time",
            Detail = $"These projects spent most of their wall-clock span waiting rather than doing local work: {examples}. "
                   + "That pattern suggests waiting on dependencies, graph scheduling, or repeated framework/reference work rather than heavy project-local execution. "
                   + "Investigate solution topology and dependency fan-out.",
            Severity = FindingSeverity.Warning,
            Evidence = $"OutlierCount={report.SpanOutliers.Count}, Rules: Span>=5s, Span/SelfTime>=5x, Span-SelfTime>=3s",
            ThresholdName = $"{nameof(SpanOutliersMinCountForFinding)}={SpanOutliersMinCountForFinding}",
        });
    }

    private static void DetectCriticalPathConcentration(BuildReport report, double totalSelfMs, List<AnalysisFinding> findings)
    {
        // Hard rule: no critical path findings if validation failed (empty path).
        if (report.CriticalPath.Count == 0 || totalSelfMs <= 0) return;

        var cpSelfMs = report.CriticalPath.Sum(p => p.SelfTime.TotalMilliseconds);
        var cpPct = cpSelfMs / totalSelfMs * 100;

        if (cpPct < CriticalPathMinInterestingPct) return;

        var topThree = string.Join(" → ",
            report.CriticalPath.OrderByDescending(p => p.SelfTime).Take(3).Select(p => $"{p.Name} ({Fmt(p.SelfTime)})"));

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Critical path concentrates {cpPct:F1}% of total self time",
            Detail = $"{report.CriticalPath.Count} projects on the critical path ({Fmt(report.CriticalPathTotal)} total). Highest-cost nodes: {topThree}. Investigate these first — they determine the lower bound on build time assuming the observed dependency graph.",
            Severity = FindingSeverity.Info,
            Evidence = $"CriticalPathTotal={Fmt(report.CriticalPathTotal)}, CriticalPathPct={cpPct:F1}%, NodeCount={report.CriticalPath.Count}",
            ThresholdName = $"{nameof(CriticalPathMinInterestingPct)}={CriticalPathMinInterestingPct:F0}%",
        });
    }

    // ──────────────────────── Recommendations ──────────────────────
    // Hard rule: investigation-first language. No architectural conclusions from timing data alone.

    private static IReadOnlyList<AnalysisRecommendation> GenerateRecommendations(
        List<AnalysisFinding> findings, BuildReport report)
    {
        var recs = new List<AnalysisRecommendation>();

        foreach (var f in findings)
        {
            if (f.Severity == FindingSeverity.Info) continue;

            if (f.Title.Contains("holds the largest share"))
            {
                var top = report.Projects.FirstOrDefault();
                if (top is null) continue;
                var topTargets = top.Targets.Count > 0
                    ? string.Join(", ", top.Targets.Take(3).Select(t => t.Name))
                    : "its top targets";
                recs.Add(new AnalysisRecommendation
                {
                    Number = 0,
                    Text = $"Investigate {top.Name}: start with {topTargets}. Profile the targets holding most of the {Fmt(top.SelfTime)} of self time before deciding whether to change anything.",
                });
            }
            else if (f.Title.Contains("self time is") && f.Title.Contains("the next project"))
            {
                var top = report.Projects.FirstOrDefault();
                if (top is null) continue;
                recs.Add(new AnalysisRecommendation
                {
                    Number = 0,
                    Text = $"Investigate why {top.Name}'s self time is disproportionate to the rest of the solution. Focus on its target breakdown before concluding the project needs structural changes.",
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
            else if (f.Title.Contains("is an outlier"))
            {
                recs.Add(new AnalysisRecommendation
                {
                    Number = 0,
                    Text = $"Investigate: {f.Title}. Compare against projects with similar target to find what differs (source generators, custom analyzers, file volume).",
                });
            }
            else if (f.Title.Contains("Reference-related build work"))
            {
                recs.Add(new AnalysisRecommendation
                {
                    Number = 0,
                    Text = "Investigate solution topology: check whether fan-out and project count are causing repeated reference-resolution across many small projects. Review the dependency graph section before restructuring.",
                });
            }
            else if (f.Title.Contains("long span relative to low self time"))
            {
                recs.Add(new AnalysisRecommendation
                {
                    Number = 0,
                    Text = "Investigate the dependency graph for the span-outlier projects. Look at their position in the DAG — they are likely waiting on upstream dependencies rather than doing heavy local work.",
                });
            }
        }

        for (int i = 0; i < recs.Count; i++)
            recs[i] = recs[i] with { Number = i + 1 };

        return recs;
    }

    // ──────────────────────────── Helpers ───────────────────────────

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
