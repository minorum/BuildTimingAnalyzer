using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Builds and analyses the project-reference DAG. Consumes edges extracted from
/// <c>ProjectStartedEventArgs.Items</c> / <c>ProjectEvaluationFinishedEventArgs.Items</c>
/// (ProjectReference item type) and produces a graph summary used by multiple report sections.
/// </summary>
public static class DependencyGraphAnalyzer
{
    private const int MinEdgesForUsableGraph = 1;

    public static DependencyGraph Build(
        IReadOnlyList<ProjectTiming> projects,
        IReadOnlyDictionary<string, IReadOnlyList<string>> rawEdges)
    {
        var projectByPath = new Dictionary<string, ProjectTiming>(projects.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var p in projects) projectByPath[p.FullPath] = p;

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

        // Transitive closures: count of reachable nodes via outgoing (dependencies) and incoming (dependents)
        var transitiveDependencies = ComputeTransitiveClosureCounts(projectByPath.Keys, outgoing);
        var transitiveDependents = ComputeTransitiveClosureCounts(projectByPath.Keys, incoming);

        var nodes = projects
            .Select(p => new ProjectGraphNode
            {
                ProjectName = p.Name,
                FullPath = p.FullPath,
                OutgoingCount = outgoing[p.FullPath].Count,
                IncomingCount = incoming[p.FullPath].Count,
                TransitiveDependenciesCount = transitiveDependencies.GetValueOrDefault(p.FullPath),
                TransitiveDependentsCount = transitiveDependents.GetValueOrDefault(p.FullPath),
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

        // Hubs — rank by transitive dependents (downstream blast radius), then immediate fan-in
        var topHubs = nodes
            .Where(n => n.IncomingCount > 0 || n.OutgoingCount > 0)
            .OrderByDescending(n => n.TransitiveDependentsCount)
            .ThenByDescending(n => n.IncomingCount)
            .Take(10)
            .ToList();

        var longestChain = ComputeLongestChain(projectByPath, outgoing);

        // Cycle detection always runs so the report can display a definitive result
        var cycles = DetectCycles(projectByPath, outgoing);

        var isUsable = nodes.Count <= 1 || totalEdges >= MinEdgesForUsableGraph;

        return new DependencyGraph
        {
            Health = health,
            Nodes = nodes,
            TopHubs = topHubs,
            LongestChainProjectCount = longestChain,
            Cycles = cycles,
            CycleDetectionRan = true,
            IsUsable = isUsable,
        };
    }

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

    private static Dictionary<string, int> ComputeTransitiveClosureCounts(
        IEnumerable<string> nodes,
        Dictionary<string, HashSet<string>> adjacency)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in nodes)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(start);
            visited.Add(start);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (!adjacency.TryGetValue(cur, out var neighbours)) continue;
                foreach (var n in neighbours)
                {
                    if (visited.Add(n)) queue.Enqueue(n);
                }
            }
            // Exclude the starting node from the count
            result[start] = visited.Count - 1;
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
            if (!visiting.Add(path)) return 0;
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
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
