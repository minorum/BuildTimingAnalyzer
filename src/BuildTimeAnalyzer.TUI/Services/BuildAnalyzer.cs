using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Rendering;

namespace BuildTimeAnalyzer.Services;

public static class BuildAnalyzer
{
    // ── Thresholds (easy to tune) ───────────────────────────────────
    private const double BottleneckCriticalPct = 25.0;
    private const double BottleneckWarningPct = 15.0;
    private const double DisproportionateRatioCritical = 2.5;
    private const double DisproportionateRatioWarning = 1.8;
    private const double DominantTargetTypePct = 0.40;
    private const double UnusualTargetMedianMultiplier = 4.0;
    private const double UnusualTargetMinSeconds = 2.0;
    private const double CostlyResolvePackageAssetsSeconds = 3.0;
    private const double WarningConcentrationRatio = 0.30;
    private const int MinProjectsForClusters = 4;
    private const double ClusterProximityPct = 0.15;
    private const double ClusterMinAvgSeconds = 3.0;

    public static BuildAnalysis Analyze(BuildReport report)
    {
        if (report.TotalDuration < TimeSpan.FromSeconds(1))
            return new BuildAnalysis { Findings = [], Recommendations = [] };

        var findings = new List<AnalysisFinding>();

        DetectBottleneckProject(report, findings);
        DetectDisproportionatelySlowProject(report, findings);
        DetectDominantTargetType(report, findings);
        DetectUnusuallySlowTargets(report, findings);
        DetectCostlyResolvePackageAssets(report, findings);
        DetectWarningConcentration(report, findings);

        // Number findings sequentially
        for (int i = 0; i < findings.Count; i++)
            findings[i] = findings[i] with { Number = i + 1 };

        var recommendations = GenerateRecommendations(findings, report);

        return new BuildAnalysis
        {
            Findings = findings,
            Recommendations = recommendations,
        };
    }

    // ──────────────────────────── Heuristics ────────────────────────

    private static void DetectBottleneckProject(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.Projects.Count < 2) return;

        var top = report.Projects[0];
        if (top.Percentage <= BottleneckWarningPct) return;

        var severity = top.Percentage > BottleneckCriticalPct
            ? FindingSeverity.Critical
            : FindingSeverity.Warning;

        // Find the top targets belonging to this project
        var projectTargets = report.TopTargets
            .Where(t => t.ProjectName == top.Name)
            .Take(2)
            .ToList();

        var detail = $"This project consumes {top.Percentage:F1}% of the total build time.";
        if (projectTargets.Count > 0)
        {
            var targetDetails = string.Join(", ",
                projectTargets.Select(t => $"{t.Name} ({Fmt(t.Duration)})"));
            detail += $" Top targets: {targetDetails}.";
        }

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"{top.Name} is the #1 bottleneck ({Fmt(top.Duration)} = {top.Percentage:F1}% of build)",
            Detail = detail,
            Severity = severity,
        });
    }

    private static void DetectDisproportionatelySlowProject(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.Projects.Count < 2) return;

        var first = report.Projects[0];
        var second = report.Projects[1];

        if (second.Duration.TotalMilliseconds <= 0) return;
        var ratio = first.Duration.TotalMilliseconds / second.Duration.TotalMilliseconds;

        if (ratio >= DisproportionateRatioWarning)
        {
            var severity = ratio >= DisproportionateRatioCritical
                ? FindingSeverity.Critical
                : FindingSeverity.Warning;

            findings.Add(new AnalysisFinding
            {
                Number = 0,
                Title = $"{first.Name} takes {ratio:F1}x longer than the next project",
                Detail = $"{first.Name} ({Fmt(first.Duration)}) vs {second.Name} ({Fmt(second.Duration)}). Consider splitting it if it contains unrelated responsibilities.",
                Severity = severity,
            });
        }

        // Detect clusters of similarly-timed projects
        DetectProjectClusters(report, findings);
    }

    private static void DetectProjectClusters(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.Projects.Count < MinProjectsForClusters) return;

        var clusters = new List<List<ProjectTiming>>();
        var current = new List<ProjectTiming> { report.Projects[0] };

        for (int i = 1; i < report.Projects.Count; i++)
        {
            var prev = report.Projects[i - 1];
            var curr = report.Projects[i];

            if (prev.Duration.TotalMilliseconds > 0 &&
                Math.Abs(prev.Duration.TotalMilliseconds - curr.Duration.TotalMilliseconds) / prev.Duration.TotalMilliseconds <= ClusterProximityPct)
            {
                current.Add(curr);
            }
            else
            {
                if (current.Count >= 2) clusters.Add(current);
                current = [curr];
            }
        }
        if (current.Count >= 2) clusters.Add(current);

        // Report the largest notable cluster
        var bestCluster = clusters
            .Where(c => c.Average(p => p.Duration.TotalSeconds) >= ClusterMinAvgSeconds)
            .OrderByDescending(c => c.Count)
            .FirstOrDefault();

        if (bestCluster is null) return;

        var avg = TimeSpan.FromMilliseconds(bestCluster.Average(p => p.Duration.TotalMilliseconds));
        var names = bestCluster.Count <= 4
            ? string.Join(", ", bestCluster.Select(p => p.Name))
            : string.Join(", ", bestCluster.Take(3).Select(p => p.Name)) + $" and {bestCluster.Count - 3} more";

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"{bestCluster.Count} projects form a cluster at ~{Fmt(avg)} each",
            Detail = $"Projects with similar build times: {names}. This suggests a common dependency layer or similar code size.",
            Severity = FindingSeverity.Info,
        });
    }

    private static void DetectDominantTargetType(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.TopTargets.Count < 3) return;

        var groups = report.TopTargets
            .GroupBy(t => t.Name)
            .Select(g => (Name: g.Key, Count: g.Count(), TotalMs: g.Sum(t => t.Duration.TotalMilliseconds)))
            .OrderByDescending(g => g.Count)
            .ToList();

        var dominant = groups[0];
        var ratio = (double)dominant.Count / report.TopTargets.Count;

        if (ratio < DominantTargetTypePct) return;

        var totalTime = TimeSpan.FromMilliseconds(dominant.TotalMs);

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"{dominant.Name} dominates \u2014 {dominant.Count} of the top {report.TopTargets.Count} slowest targets",
            Detail = $"{dominant.Name} accounts for {Fmt(totalTime)} total across {dominant.Count} projects. This is the primary work the compiler performs.",
            Severity = FindingSeverity.Info,
        });
    }

    private static void DetectUnusuallySlowTargets(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.TopTargets.Count < 3) return;

        var groups = report.TopTargets
            .GroupBy(t => t.Name)
            .Where(g => g.Count() >= 3)
            .ToList();

        foreach (var group in groups)
        {
            var sorted = group.OrderBy(t => t.Duration).ToList();
            var median = sorted[sorted.Count / 2].Duration;

            if (median.TotalMilliseconds <= 0) continue;

            foreach (var outlier in sorted.Where(t =>
                t.Duration.TotalMilliseconds > median.TotalMilliseconds * UnusualTargetMedianMultiplier &&
                t.Duration.TotalSeconds > UnusualTargetMinSeconds))
            {
                var multiplier = outlier.Duration.TotalMilliseconds / median.TotalMilliseconds;

                findings.Add(new AnalysisFinding
                {
                    Number = 0,
                    Title = $"{outlier.Name} is unusually slow in {outlier.ProjectName} ({Fmt(outlier.Duration)})",
                    Detail = $"The median for {outlier.Name} is {Fmt(median)}, but {outlier.ProjectName} takes {multiplier:F1}x that. May indicate a source generator issue or unusually large input.",
                    Severity = FindingSeverity.Warning,
                });
            }
        }
    }

    private static void DetectCostlyResolvePackageAssets(BuildReport report, List<AnalysisFinding> findings)
    {
        var costly = report.TopTargets
            .Where(t => t.Name == "ResolvePackageAssets" &&
                        t.Duration.TotalSeconds > CostlyResolvePackageAssetsSeconds)
            .ToList();

        if (costly.Count == 0) return;

        var min = costly.Min(t => t.Duration);
        var max = costly.Max(t => t.Duration);
        var projects = string.Join(", ", costly.Select(t => t.ProjectName));
        var range = min == max ? Fmt(max) : $"{Fmt(min)}\u2013{Fmt(max)}";

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"ResolvePackageAssets is costly ({range}) in {costly.Count} project(s)",
            Detail = $"Affected: {projects}. This indicates bloated NuGet dependency trees. Run `dotnet nuget why` to find heavy transitive dependencies.",
            Severity = FindingSeverity.Warning,
        });
    }

    private static void DetectWarningConcentration(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.WarningCount == 0) return;

        var projectsWithWarnings = report.Projects
            .Where(p => p.WarningCount > 0)
            .OrderByDescending(p => p.WarningCount)
            .ToList();

        if (projectsWithWarnings.Count == 0) return;

        var concentrationRatio = (double)projectsWithWarnings.Count / report.Projects.Count;
        var isConcentrated = concentrationRatio <= WarningConcentrationRatio;

        if (!isConcentrated && report.WarningCount <= 20) return;

        var severity = report.WarningCount > 30
            ? FindingSeverity.Warning
            : FindingSeverity.Info;

        var topProjects = projectsWithWarnings.Take(5)
            .Select(p => $"{p.Name} ({p.WarningCount})")
            .ToList();
        var projectList = string.Join(", ", topProjects);
        if (projectsWithWarnings.Count > 5)
            projectList += $" and {projectsWithWarnings.Count - 5} more";

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"{report.WarningCount} warnings concentrated in {projectsWithWarnings.Count} of {report.Projects.Count} projects",
            Detail = $"Top sources: {projectList}. Concentrated warnings make targeted cleanup feasible.",
            Severity = severity,
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

            if (f.Title.Contains("is the #1 bottleneck"))
            {
                var name = report.Projects.Count > 0 ? report.Projects[0].Name : "the top project";
                recs.Add(new AnalysisRecommendation
                {
                    Number = 0,
                    Text = $"Investigate {name} \u2014 at {Fmt(report.Projects[0].Duration)} it's the single biggest bottleneck. Profile its compilation and dependency graph.",
                });
            }
            else if (f.Title.Contains("takes") && f.Title.Contains("longer than the next"))
            {
                var name = report.Projects.Count > 0 ? report.Projects[0].Name : "the top project";
                recs.Add(new AnalysisRecommendation
                {
                    Number = 0,
                    Text = $"Consider splitting {name} if it contains unrelated responsibilities \u2014 its build time dwarfs everything else.",
                });
            }
            else if (f.Title.Contains("ResolvePackageAssets"))
            {
                recs.Add(new AnalysisRecommendation
                {
                    Number = 0,
                    Text = "Audit NuGet dependencies \u2014 run `dotnet nuget why` on heavy packages to find transitive chains that can be trimmed.",
                });
            }
            else if (f.Title.Contains("unusually slow"))
            {
                recs.Add(new AnalysisRecommendation
                {
                    Number = 0,
                    Text = $"Investigate: {f.Title}. Check for source generators or excessive global usings.",
                });
            }
            else if (f.Title.Contains("warnings"))
            {
                recs.Add(new AnalysisRecommendation
                {
                    Number = 0,
                    Text = $"Fix the {report.WarningCount} compiler warnings \u2014 they are concentrated in a few projects, making targeted cleanup practical.",
                });
            }
        }

        // Number sequentially
        for (int i = 0; i < recs.Count; i++)
            recs[i] = recs[i] with { Number = i + 1 };

        return recs;
    }

    // ──────────────────────────── Helpers ───────────────────────────

    private static string Fmt(TimeSpan ts) => ConsoleReportRenderer.FormatDuration(ts);
}
