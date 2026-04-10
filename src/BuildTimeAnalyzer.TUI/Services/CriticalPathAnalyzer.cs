using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Computes a project-level critical path estimate using CPM:
///   • Node cost  = project SelfTime (verified exclusive)
///   • Edges      = project dependency DAG (from ProjectReference items)
///   • Algorithm  = earliest_finish(p) = max(earliest_finish(d) for d in deps) + SelfTime(p)
///
/// The result is an <i>estimate</i> implied by the observed project DAG and measured self times.
/// A validation record is always returned so the report can display accept/reject status with
/// the underlying numbers that justified the decision.
/// </summary>
public static class CriticalPathAnalyzer
{
    /// <summary>Tolerance for accepting a computed path whose total is slightly above wall clock (clock-skew slack).</summary>
    private const int ValidationToleranceMs = 50;

    public static (IReadOnlyList<ProjectTiming> Path, TimeSpan Total, CriticalPathValidation Validation) Compute(
        IReadOnlyList<ProjectTiming> projects,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies,
        TimeSpan wallClockBuildTime,
        bool graphIsUsable)
    {
        if (!graphIsUsable)
        {
            return ([], TimeSpan.Zero, new CriticalPathValidation
            {
                ComputedTotal = TimeSpan.Zero,
                WallClock = wallClockBuildTime,
                Accepted = false,
                Reason = "Graph is not usable (too few edges for a multi-project solution). Critical path was not computed.",
                GraphWasUsable = false,
            });
        }

        if (projects.Count == 0)
        {
            return ([], TimeSpan.Zero, new CriticalPathValidation
            {
                ComputedTotal = TimeSpan.Zero,
                WallClock = wallClockBuildTime,
                Accepted = false,
                Reason = "No projects to analyse.",
                GraphWasUsable = true,
            });
        }

        var byPath = new Dictionary<string, ProjectTiming>(projects.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var p in projects) byPath[p.FullPath] = p;

        var earliestFinish = new Dictionary<string, TimeSpan>(projects.Count, StringComparer.OrdinalIgnoreCase);
        var predecessor = new Dictionary<string, string?>(projects.Count, StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
            Visit(project.FullPath);

        var endPath = earliestFinish
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .FirstOrDefault();

        var computedTotal = endPath is not null ? earliestFinish[endPath] : TimeSpan.Zero;

        // Validation gate
        var tolerance = TimeSpan.FromMilliseconds(ValidationToleranceMs);
        var accepted = endPath is not null && computedTotal <= wallClockBuildTime + tolerance;
        string reason;
        if (endPath is null)
            reason = "No end node found.";
        else if (!accepted)
            reason = $"Computed path total ({FormatDuration(computedTotal)}) exceeds wall clock ({FormatDuration(wallClockBuildTime)}) beyond tolerance ({ValidationToleranceMs}ms). " +
                     "The dependency model is likely incomplete or inconsistent. Path suppressed.";
        else
            reason = $"Computed path total {FormatDuration(computedTotal)} <= wall clock {FormatDuration(wallClockBuildTime)} (within {ValidationToleranceMs}ms tolerance).";

        var validation = new CriticalPathValidation
        {
            ComputedTotal = computedTotal,
            WallClock = wallClockBuildTime,
            Accepted = accepted,
            Reason = reason,
            GraphWasUsable = true,
        };

        if (!accepted || endPath is null)
            return ([], TimeSpan.Zero, validation);

        var path = new List<ProjectTiming>();
        var cursor = endPath;
        while (cursor is not null && byPath.TryGetValue(cursor, out var node))
        {
            path.Add(node);
            cursor = predecessor.GetValueOrDefault(cursor);
        }
        path.Reverse();

        return (path, computedTotal, validation);

        TimeSpan Visit(string path)
        {
            if (earliestFinish.TryGetValue(path, out var cached)) return cached;
            if (!byPath.TryGetValue(path, out var project)) return TimeSpan.Zero;
            if (!visiting.Add(path)) return TimeSpan.Zero;

            TimeSpan bestDepFinish = TimeSpan.Zero;
            string? bestDep = null;
            if (dependencies.TryGetValue(path, out var deps))
            {
                foreach (var dep in deps)
                {
                    var finish = Visit(dep);
                    if (finish > bestDepFinish)
                    {
                        bestDepFinish = finish;
                        bestDep = dep;
                    }
                }
            }

            var finishTime = bestDepFinish + project.SelfTime;
            earliestFinish[path] = finishTime;
            predecessor[path] = bestDep;
            visiting.Remove(path);
            return finishTime;
        }
    }

    private static string FormatDuration(TimeSpan ts) => Rendering.ConsoleReportRenderer.FormatDuration(ts);
}
