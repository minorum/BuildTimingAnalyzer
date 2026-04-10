using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Computes a project-level critical path using standard CPM:
///   • Node cost  = project SelfTime (verified exclusive)
///   • Edges      = project dependency DAG (parent → child = "depends on")
///   • Algorithm  = earliest_finish(p) = max(earliest_finish(d) for d in deps) + SelfTime(p)
///
/// The result is an <i>estimate</i> of the longest sequential chain implied by the observed
/// project DAG and measured self times — not a definitive statement about the minimum build time.
///
/// A validation gate rejects the computed path if its total exceeds the wall-clock build time,
/// which would indicate an inconsistent or incomplete dependency model. In that case an empty
/// critical path is returned and callers should omit the section from the report.
/// </summary>
public static class CriticalPathAnalyzer
{
    public static (IReadOnlyList<ProjectTiming> Path, TimeSpan Total) Compute(
        IReadOnlyList<ProjectTiming> projects,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies,
        TimeSpan wallClockBuildTime)
    {
        if (projects.Count == 0)
            return ([], TimeSpan.Zero);

        // Lookup: project full path → ProjectTiming
        var byPath = new Dictionary<string, ProjectTiming>(projects.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var p in projects) byPath[p.FullPath] = p;

        // Earliest finish and predecessor pointers via memoised DFS.
        var earliestFinish = new Dictionary<string, TimeSpan>(projects.Count, StringComparer.OrdinalIgnoreCase);
        var predecessor = new Dictionary<string, string?>(projects.Count, StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
            Visit(project.FullPath);

        // Pick the node with the largest earliest_finish — that's the end of the critical path.
        var endPath = earliestFinish
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .FirstOrDefault();

        if (endPath is null)
            return ([], TimeSpan.Zero);

        var computedTotal = earliestFinish[endPath];

        // ── Validation gate ─────────────────────────────────────────────
        // If the computed critical path is longer than wall-clock build time, our dependency
        // model is incomplete or cyclic. Reject the result rather than ship misleading data.
        if (computedTotal > wallClockBuildTime + TimeSpan.FromMilliseconds(50))
            return ([], TimeSpan.Zero);

        // Walk predecessors back from the endpoint.
        var path = new List<ProjectTiming>();
        var cursor = endPath;
        while (cursor is not null && byPath.TryGetValue(cursor, out var node))
        {
            path.Add(node);
            cursor = predecessor.GetValueOrDefault(cursor);
        }
        path.Reverse();

        return (path, computedTotal);

        TimeSpan Visit(string path)
        {
            if (earliestFinish.TryGetValue(path, out var cached)) return cached;
            if (!byPath.TryGetValue(path, out var project)) return TimeSpan.Zero;
            if (!visiting.Add(path))
            {
                // Cycle protection — should not happen for a real project DAG but guard anyway.
                return TimeSpan.Zero;
            }

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
}
