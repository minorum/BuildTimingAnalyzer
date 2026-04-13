using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Rendering;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Builds a "Why is this slow?" synthesis for each top project. One factual paragraph
/// pulling together task breakdown, category composition, analyzer/generator cost,
/// graph position, and span/self pattern. No interpretation beyond measured data.
/// </summary>
public static class ProjectDiagnosisBuilder
{
    public static IReadOnlyList<ProjectDiagnosis> Build(
        IReadOnlyList<ProjectTiming> projects,
        IReadOnlyList<AnalyzerReport> analyzerReports,
        IReadOnlyList<ProjectTiming> criticalPath,
        IReadOnlyList<ProjectTiming> spanOutliers,
        IReadOnlyList<TaskTiming> topTasks,
        int topN = 5)
    {
        var criticalSet = new HashSet<string>(
            criticalPath.Select(p => p.FullPath), StringComparer.OrdinalIgnoreCase);
        var outlierSet = new HashSet<string>(
            spanOutliers.Select(p => p.FullPath), StringComparer.OrdinalIgnoreCase);
        var analyzerByProject = new Dictionary<string, AnalyzerReport>(StringComparer.OrdinalIgnoreCase);
        foreach (var ar in analyzerReports)
            analyzerByProject[ar.ProjectName] = ar;
        var tasksByProject = topTasks
            .GroupBy(t => t.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(t => t.SelfTime).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var result = new List<ProjectDiagnosis>();

        foreach (var p in projects.Take(topN))
        {
            var topCategory = p.CategoryBreakdown.Count > 0
                ? p.CategoryBreakdown.OrderByDescending(kv => kv.Value).First()
                : default;
            var topCat = topCategory.Key;
            var topCatPct = p.CategoryBreakdown.Count > 0
                ? topCategory.Value.TotalMilliseconds /
                  Math.Max(1, p.CategoryBreakdown.Sum(kv => kv.Value.TotalMilliseconds)) * 100
                : 0;

            var topTask = tasksByProject.TryGetValue(p.Name, out var tasks) && tasks.Count > 0
                ? tasks[0] : null;

            var ar = analyzerByProject.GetValueOrDefault(p.Name);
            var onCritical = criticalSet.Contains(p.FullPath);
            var isOutlier = outlierSet.Contains(p.FullPath);

            var summary = BuildSummary(p, topCat, topCatPct, topTask, ar, onCritical, isOutlier);

            var packages = ProjectPackageResolver.Resolve(p.FullPath);

            result.Add(new ProjectDiagnosis
            {
                ProjectName = p.Name,
                SelfTime = p.SelfTime,
                SelfPercent = p.SelfPercent,
                TopCategory = ConsoleReportRenderer.CategoryLabel(topCat),
                TopCategoryPercent = topCatPct,
                TopTask = topTask?.TaskName ?? "(none)",
                TopTaskTime = topTask?.SelfTime ?? TimeSpan.Zero,
                AnalyzerTime = ar?.TotalAnalyzerTime,
                GeneratorTime = ar?.TotalGeneratorTime,
                OnCriticalPath = onCritical,
                IsSpanOutlier = isOutlier,
                Summary = summary,
                Packages = packages,
            });
        }

        return result;
    }

    private static string BuildSummary(
        ProjectTiming p,
        TargetCategory topCat,
        double topCatPct,
        TaskTiming? topTask,
        AnalyzerReport? ar,
        bool onCritical,
        bool isOutlier)
    {
        var parts = new List<string>();

        parts.Add($"{p.Name} has {Fmt(p.SelfTime)} of self time ({p.SelfPercent:F1}% of total).");

        if (topCatPct > 0)
            parts.Add($"Dominant category: {ConsoleReportRenderer.CategoryLabel(topCat)} ({topCatPct:F0}% of this project's attributed time).");

        if (topTask is not null)
            parts.Add($"Slowest task: {topTask.TaskName} in {topTask.TargetName} ({Fmt(topTask.SelfTime)}).");

        if (ar is not null)
        {
            if (ar.TotalAnalyzerTime > TimeSpan.FromMilliseconds(100))
                parts.Add($"Analyzer time: {Fmt(ar.TotalAnalyzerTime)} across {ar.Analyzers.Count} analyzer assembly(ies).");
            if (ar.TotalGeneratorTime > TimeSpan.FromMilliseconds(100))
                parts.Add($"Generator time: {Fmt(ar.TotalGeneratorTime)} across {ar.Generators.Count} generator assembly(ies).");
        }

        if (onCritical)
            parts.Add("On the critical path estimate.");

        if (isOutlier)
            parts.Add($"Span outlier: span {Fmt(p.Span)} vs self {Fmt(p.SelfTime)}.");

        return string.Join(" ", parts);
    }

    private static string Fmt(TimeSpan ts) => ConsoleReportRenderer.FormatDuration(ts);
}
