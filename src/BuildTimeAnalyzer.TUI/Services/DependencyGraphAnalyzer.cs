using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Builds and analyses the project-reference DAG. Consumes edges extracted from
/// <c>ProjectStartedEventArgs.Items</c> (ProjectReference item type) and produces a
/// graph summary used by multiple report sections.
/// </summary>
public static class DependencyGraphAnalyzer
{
    /// <summary>
    /// A graph is considered "usable" for derived analyses (critical path, etc.) only if it has
    /// at least this many edges when there are multiple projects. A solution of N projects with
    /// zero edges almost certainly means extraction failed.
    /// </summary>
    private const int MinEdgesForUsableGraph = 1;

    public static DependencyGraph Build(
        IReadOnlyList<ProjectTiming> projects,
        IReadOnlyDictionary<string, IReadOnlyList<string>> rawEdges)
    {
        // Index by full path (case-insensitive) so lookups are consistent with LogAnalyzer.
        var projectByPath = new Dictionary<string, ProjectTiming>(projects.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var p in projects) projectByPath[p.FullPath] = p;

        // Build normalized outgoing/incoming adjacency restricted to known projects.
        var outgoing = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var incoming = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in projects)
        {
            outgoing[p.FullPath] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            incoming[p.FullPath] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        int totalEdges = 0;
        foreach (var (from, toList) in rawEdges)
        {
            if (!projectByPath.ContainsKey(from)) continue;
            foreach (var to in toList)
            {
                if (!projectByPath.ContainsKey(to)) continue;
                if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) continue;
                if (outgoing[from].Add(to))
                {
                    incoming[to].Add(from);
                    totalEdges++;
                }
            }
        }

        var nodes = projects
            .Select(p => new ProjectGraphNode
            {
                ProjectName = p.Name,
                FullPath = p.FullPath,
                OutgoingCount = outgoing[p.FullPath].Count,
                IncomingCount = incoming[p.FullPath].Count,
            })
            .ToList();

        var nodesWithOutgoing = nodes.Count(n => n.OutgoingCount > 0);
        var nodesWithIncoming = nodes.Count(n => n.IncomingCount > 0);
        var isolated = nodes.Count(n => n.OutgoingCount == 0 && n.IncomingCount == 0);

        var health = new DependencyGraphHealth
        {
            TotalProjects = nodes.Count,
            TotalEdges = totalEdges,
            IsolatedNodes = isolated,
            NodesWithOutgoing = nodesWithOutgoing,
            NodesWithIncoming = nodesWithIncoming,
        };

        // Hubs = nodes with the highest fan-in (depended on by the most projects)
        var topHubs = nodes
            .Where(n => n.IncomingCount > 0 || n.OutgoingCount > 0)
            .OrderByDescending(n => n.IncomingCount)
            .ThenByDescending(n => n.OutgoingCount)
            .Take(10)
            .ToList();

        // Longest chain by project count — transitive depth via memoised DFS
        var longestChain = ComputeLongestChain(projectByPath, outgoing);

        // Cycle detection
        var cycles = DetectCycles(projectByPath, outgoing);

        // Usability: enough edges for a multi-project solution
        var isUsable = nodes.Count <= 1 || totalEdges >= MinEdgesForUsableGraph;

        return new DependencyGraph
        {
            Health = health,
            Nodes = nodes,
            TopHubs = topHubs,
            LongestChainProjectCount = longestChain,
            Cycles = cycles,
            IsUsable = isUsable,
        };
    }

    /// <summary>
    /// Returns the adjacency map used by CriticalPathAnalyzer. Source of truth for "what depends on what".
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ToDependencyMap(
        IReadOnlyList<ProjectTiming> projects,
        IReadOnlyDictionary<string, IReadOnlyList<string>> rawEdges)
    {
        var projectPaths = new HashSet<string>(projects.Select(p => p.FullPath), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, toList) in rawEdges)
        {
            if (!projectPaths.Contains(from)) continue;
            var filtered = toList.Where(projectPaths.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (filtered.Count > 0) result[from] = filtered;
        }
        return result;
    }

    private static int ComputeLongestChain(
        Dictionary<string, ProjectTiming> projectByPath,
        Dictionary<string, HashSet<string>> outgoing)
    {
        var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int best = 0;
        foreach (var path in projectByPath.Keys)
            best = Math.Max(best, Visit(path));
        return best;

        int Visit(string path)
        {
            if (depth.TryGetValue(path, out var cached)) return cached;
            if (!visiting.Add(path)) return 0; // cycle guard
            int max = 0;
            if (outgoing.TryGetValue(path, out var deps))
            {
                foreach (var dep in deps)
                    max = Math.Max(max, Visit(dep));
            }
            visiting.Remove(path);
            depth[path] = 1 + max;
            return 1 + max;
        }
    }

    private static IReadOnlyList<IReadOnlyList<string>> DetectCycles(
        Dictionary<string, ProjectTiming> projectByPath,
        Dictionary<string, HashSet<string>> outgoing)
    {
        var cycles = new List<IReadOnlyList<string>>();
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 0=white, 1=gray, 2=black
        var stack = new List<string>();

        foreach (var start in projectByPath.Keys)
        {
            if (state.GetValueOrDefault(start) != 0) continue;
            Visit(start);
        }

        return cycles;

        void Visit(string node)
        {
            state[node] = 1;
            stack.Add(node);
            if (outgoing.TryGetValue(node, out var deps))
            {
                foreach (var dep in deps)
                {
                    var s = state.GetValueOrDefault(dep);
                    if (s == 0) Visit(dep);
                    else if (s == 1)
                    {
                        // Found a back-edge — record the cycle from dep to current
                        var cycleStart = stack.IndexOf(dep);
                        if (cycleStart >= 0)
                        {
                            var cycle = stack
                                .Skip(cycleStart)
                                .Select(p => projectByPath.TryGetValue(p, out var pt) ? pt.Name : p)
                                .ToList();
                            cycles.Add(cycle);
                        }
                    }
                }
            }
            state[node] = 2;
            stack.RemoveAt(stack.Count - 1);
        }
    }
}
