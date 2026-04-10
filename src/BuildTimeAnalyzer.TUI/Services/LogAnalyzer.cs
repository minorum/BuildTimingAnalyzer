using BuildTimeAnalyzer.Models;
using Microsoft.Build.Logging.StructuredLogger;

namespace BuildTimeAnalyzer.Services;

public sealed class LogAnalyzer
{
    // Drill-down: how many projects get target breakdowns populated.
    // Top N by SelfTime, plus anything on the critical path.
    private const int DrillDownTopN = 3;

    private readonly int _topTargets;

    public LogAnalyzer(int topTargets = 20)
    {
        _topTargets = topTargets;
    }

    /// <summary>
    /// Parses the binary log file using a streaming API to avoid loading the entire tree into memory.
    /// Computes exclusive (self) times by subtracting orchestration task (MSBuild/CallTarget) durations.
    /// </summary>
    public System.Threading.Tasks.Task<BuildReport> AnalyzeAsync(string binLogPath, string projectOrSolutionPath, CancellationToken ct = default)
    {
        return System.Threading.Tasks.Task.Run(() => Analyze(binLogPath, projectOrSolutionPath), ct);
    }

    private BuildReport Analyze(string binLogPath, string projectOrSolutionPath)
    {
        var projectTimings = new Dictionary<int, ProjectAccumulator>(256);
        var targetTimings = new List<RawTargetTiming>(4096);

        // Track orchestration task (MSBuild/CallTarget) durations per target.
        // These tasks trigger child project builds; their time inflates the parent target duration.
        var runningOrchTasks = new Dictionary<(int ProjectInstanceId, int TaskId), (int TargetId, DateTime StartTime)>();
        var orchTaskDurations = new Dictionary<(int ProjectInstanceId, int TargetId), TimeSpan>();

        // Project-level dependency edges: child instance id → parent instance id (from ParentProjectBuildEventContext)
        var parentByInstance = new Dictionary<int, int>(256);

        // Executed vs skipped target counts (observed incremental behavior — no mode inference)
        int executedTargets = 0;
        int skippedTargets = 0;
        bool restoreObserved = false;

        // Build context
        string? configuration = null;
        string? sdkVersion = null;
        string? msBuildVersion = null;
        string? operatingSystem = null;
        int? parallelism = null;

        int errorCount = 0;
        int warningCount = 0;
        DateTime buildStart = DateTime.MaxValue;
        DateTime buildEnd = DateTime.MinValue;
        bool succeeded = false;

        var reader = new BinLogReader();

        foreach (var record in reader.ReadRecords(binLogPath))
        {
            if (record.Args is null) continue;

            switch (record.Args)
            {
                case Microsoft.Build.Framework.BuildStartedEventArgs bse:
                    if (bse.Timestamp < buildStart) buildStart = bse.Timestamp;
                    CaptureBuildContext(bse, ref configuration, ref sdkVersion, ref msBuildVersion, ref operatingSystem, ref parallelism);
                    break;

                case Microsoft.Build.Framework.BuildFinishedEventArgs bfe:
                    if (bfe.Timestamp > buildEnd) buildEnd = bfe.Timestamp;
                    succeeded = bfe.Succeeded;
                    break;

                case Microsoft.Build.Framework.ProjectStartedEventArgs pse:
                    {
                        var key = pse.BuildEventContext?.ProjectInstanceId ?? -1;
                        if (key < 0) break;
                        projectTimings.TryAdd(key, new ProjectAccumulator
                        {
                            Name = Path.GetFileNameWithoutExtension(pse.ProjectFile ?? "Unknown"),
                            FullPath = pse.ProjectFile ?? "",
                            StartTime = pse.Timestamp,
                        });

                        var parentId = pse.ParentProjectBuildEventContext?.ProjectInstanceId ?? -1;
                        if (parentId >= 0 && parentId != key)
                            parentByInstance.TryAdd(key, parentId);

                        // Capture configuration from the first project's global properties
                        if (configuration is null && pse.GlobalProperties is not null)
                        {
                            if (pse.GlobalProperties.TryGetValue("Configuration", out var cfg) && !string.IsNullOrEmpty(cfg))
                                configuration = cfg;
                        }
                        // Detect restore by target list
                        if (!restoreObserved && pse.TargetNames is string tn && tn.Contains("Restore", StringComparison.OrdinalIgnoreCase))
                            restoreObserved = true;
                        break;
                    }

                case Microsoft.Build.Framework.ProjectFinishedEventArgs pfe:
                    {
                        var key = pfe.BuildEventContext?.ProjectInstanceId ?? -1;
                        if (key < 0 || !projectTimings.TryGetValue(key, out var acc)) break;
                        acc.EndTime = pfe.Timestamp;
                        acc.Succeeded = pfe.Succeeded;
                        break;
                    }

                case Microsoft.Build.Framework.TargetStartedEventArgs tse:
                    {
                        var ctx = tse.BuildEventContext;
                        var targetId = ctx?.TargetId ?? -1;
                        if (targetId < 0) break;
                        targetTimings.Add(new RawTargetTiming
                        {
                            Id = targetId,
                            ProjectInstanceId = ctx!.ProjectInstanceId,
                            Name = tse.TargetName ?? "Unknown",
                            ProjectName = Path.GetFileNameWithoutExtension(tse.ProjectFile ?? ""),
                            StartTime = tse.Timestamp,
                        });
                        executedTargets++;
                        break;
                    }

                case Microsoft.Build.Framework.TargetSkippedEventArgs:
                    skippedTargets++;
                    break;

                case Microsoft.Build.Framework.TargetFinishedEventArgs tfe:
                    {
                        var ctx = tfe.BuildEventContext;
                        var targetId = ctx?.TargetId ?? -1;
                        var projInstanceId = ctx?.ProjectInstanceId ?? -1;
                        if (targetId < 0) break;
                        for (int i = targetTimings.Count - 1; i >= 0; i--)
                        {
                            if (targetTimings[i].Id == targetId &&
                                targetTimings[i].ProjectInstanceId == projInstanceId &&
                                targetTimings[i].EndTime == default)
                            {
                                targetTimings[i] = targetTimings[i] with { EndTime = tfe.Timestamp };
                                break;
                            }
                        }
                        break;
                    }

                case Microsoft.Build.Framework.TaskStartedEventArgs taskSe:
                    {
                        if (taskSe.TaskName is not ("MSBuild" or "CallTarget")) break;
                        var ctx = taskSe.BuildEventContext;
                        if (ctx is not { ProjectInstanceId: >= 0, TargetId: >= 0, TaskId: >= 0 }) break;
                        runningOrchTasks[(ctx.ProjectInstanceId, ctx.TaskId)] = (ctx.TargetId, taskSe.Timestamp);
                        break;
                    }

                case Microsoft.Build.Framework.TaskFinishedEventArgs taskFe:
                    {
                        if (taskFe.TaskName is not ("MSBuild" or "CallTarget")) break;
                        var ctx = taskFe.BuildEventContext;
                        if (ctx is not { ProjectInstanceId: >= 0, TaskId: >= 0 }) break;
                        if (!runningOrchTasks.Remove((ctx.ProjectInstanceId, ctx.TaskId), out var info)) break;
                        var duration = taskFe.Timestamp - info.StartTime;
                        if (duration > TimeSpan.Zero)
                        {
                            var orchKey = (ctx.ProjectInstanceId, info.TargetId);
                            orchTaskDurations[orchKey] = orchTaskDurations.GetValueOrDefault(orchKey) + duration;
                        }
                        break;
                    }

                case Microsoft.Build.Framework.BuildErrorEventArgs:
                    errorCount++;
                    {
                        var ctx = ((Microsoft.Build.Framework.BuildErrorEventArgs)record.Args).BuildEventContext;
                        var key = ctx?.ProjectInstanceId ?? -1;
                        if (key >= 0 && projectTimings.TryGetValue(key, out var acc))
                            acc.ErrorCount++;
                    }
                    break;

                case Microsoft.Build.Framework.BuildWarningEventArgs:
                    warningCount++;
                    {
                        var ctx = ((Microsoft.Build.Framework.BuildWarningEventArgs)record.Args).BuildEventContext;
                        var key = ctx?.ProjectInstanceId ?? -1;
                        if (key >= 0 && projectTimings.TryGetValue(key, out var acc))
                            acc.WarningCount++;
                    }
                    break;
            }
        }

        // Guard against empty logs
        if (buildStart == DateTime.MaxValue) buildStart = DateTime.UtcNow;
        if (buildEnd == DateTime.MinValue) buildEnd = buildStart;

        var wallClock = buildEnd - buildStart;

        // ── Compute exclusive target times ─────────────────────────────
        var completedTargets = targetTimings.Where(t => t.EndTime > t.StartTime).ToList();

        foreach (var t in completedTargets)
        {
            var orchKey = (t.ProjectInstanceId, t.Id);
            var orchTime = orchTaskDurations.GetValueOrDefault(orchKey);
            var exclusive = t.Duration - orchTime;
            t.ExclusiveDuration = exclusive > TimeSpan.Zero ? exclusive : TimeSpan.Zero;
        }

        // Sum of all self time across all targets — used as the denominator for % Self.
        // This is distinct from wall-clock time (which can be shorter due to parallelism).
        var totalSelfMs = completedTargets.Sum(t => t.ExclusiveDuration.TotalMilliseconds);

        // Per-project exclusive time
        var exclusiveProjectTimes = completedTargets
            .GroupBy(t => t.ProjectInstanceId)
            .ToDictionary(
                g => g.Key,
                g => TimeSpan.FromMilliseconds(g.Sum(t => t.ExclusiveDuration.TotalMilliseconds))
            );

        // Work spans (first target start → last target end) per project path
        var instanceToPath = projectTimings.ToDictionary(kv => kv.Key, kv => kv.Value.FullPath);
        var projectWorkSpans = completedTargets
            .Where(t => t.ExclusiveDuration > TimeSpan.Zero)
            .Where(t => instanceToPath.ContainsKey(t.ProjectInstanceId))
            .GroupBy(t => instanceToPath[t.ProjectInstanceId])
            .ToDictionary(
                g => g.Key,
                g => (
                    First: g.Min(t => t.StartTime) - buildStart,
                    Last: g.Max(t => t.EndTime) - buildStart
                )
            );

        // ── Project list (no target breakdown yet) ─────────────────────
        var projectList = projectTimings
            .Where(kv => kv.Value.EndTime > kv.Value.StartTime)
            .Where(kv => !kv.Value.FullPath.EndsWith(".metaproj", StringComparison.OrdinalIgnoreCase) &&
                         !kv.Value.FullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            .Select(kv => (
                InstanceId: kv.Key,
                Acc: kv.Value,
                ExclusiveTime: exclusiveProjectTimes.GetValueOrDefault(kv.Key)
            ))
            .Where(x => x.ExclusiveTime > TimeSpan.FromMilliseconds(1))
            .GroupBy(x => x.Acc.FullPath)
            .Select(g =>
            {
                var best = g.OrderByDescending(x => x.ExclusiveTime).First();
                var span = projectWorkSpans.GetValueOrDefault(best.Acc.FullPath);
                return new ProjectTiming
                {
                    Name = best.Acc.Name,
                    FullPath = best.Acc.FullPath,
                    SelfTime = best.ExclusiveTime,
                    Succeeded = g.All(x => x.Acc.Succeeded),
                    ErrorCount = g.Sum(x => x.Acc.ErrorCount),
                    WarningCount = g.Sum(x => x.Acc.WarningCount),
                    SelfPercent = totalSelfMs > 0 ? best.ExclusiveTime.TotalMilliseconds / totalSelfMs * 100 : 0,
                    StartOffset = span.First,
                    EndOffset = span.Last,
                };
            })
            .OrderByDescending(p => p.SelfTime)
            .ToList();

        // ── All target timings (deduped by name + project, keeping slowest) ──
        var allTargets = completedTargets
            .Where(t => t.ExclusiveDuration > TimeSpan.FromMilliseconds(1))
            .GroupBy(t => (t.Name, t.ProjectName))
            .Select(g =>
            {
                var best = g.OrderByDescending(t => t.ExclusiveDuration).First();
                return new TargetTiming
                {
                    Name = best.Name,
                    ProjectName = best.ProjectName,
                    SelfTime = best.ExclusiveDuration,
                    SelfPercent = totalSelfMs > 0 ? best.ExclusiveDuration.TotalMilliseconds / totalSelfMs * 100 : 0,
                    Category = TargetCategorizer.Categorize(best.Name),
                };
            })
            .OrderByDescending(t => t.SelfTime)
            .ToList();

        var topTargetList = allTargets.Take(_topTargets).ToList();

        // Category totals (aggregate self time per category)
        var categoryTotals = allTargets
            .GroupBy(t => t.Category)
            .ToDictionary(
                g => g.Key,
                g => TimeSpan.FromMilliseconds(g.Sum(t => t.SelfTime.TotalMilliseconds))
            );

        // Potentially custom targets — Uncategorized only, sorted by self time
        var potentiallyCustom = allTargets
            .Where(t => t.Category == TargetCategory.Uncategorized)
            .OrderByDescending(t => t.SelfTime)
            .ToList();

        // ── Critical path ─────────────────────────────────────────────
        // Build dependency edges by full path (deduping — multiple instances may map to same path pair)
        var depsByPath = BuildDependencyEdgesByPath(parentByInstance, instanceToPath);
        var (criticalPath, criticalTotal) = CriticalPathAnalyzer.Compute(projectList, depsByPath, wallClock);

        // ── Drill-down: populate Targets for top-N and critical-path projects ──
        var drillDownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in projectList.Take(DrillDownTopN)) drillDownPaths.Add(p.FullPath);
        foreach (var p in criticalPath) drillDownPaths.Add(p.FullPath);

        if (drillDownPaths.Count > 0)
        {
            var targetsByProjectName = allTargets
                .GroupBy(t => t.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.SelfTime).Take(5).ToList(),
                              StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < projectList.Count; i++)
            {
                if (!drillDownPaths.Contains(projectList[i].FullPath)) continue;
                if (!targetsByProjectName.TryGetValue(projectList[i].Name, out var targets)) continue;
                projectList[i] = projectList[i] with { Targets = targets };
            }
            // Also update criticalPath entries to reflect populated Targets
            criticalPath = criticalPath
                .Select(cp => projectList.FirstOrDefault(p =>
                    string.Equals(p.FullPath, cp.FullPath, StringComparison.OrdinalIgnoreCase)) ?? cp)
                .ToList();
        }

        return new BuildReport
        {
            ProjectOrSolutionPath = projectOrSolutionPath,
            StartTime = buildStart,
            EndTime = buildEnd,
            Succeeded = succeeded,
            ErrorCount = errorCount,
            WarningCount = warningCount,
            Projects = projectList,
            TopTargets = topTargetList,
            Context = new BuildContext
            {
                Configuration = configuration,
                SdkVersion = sdkVersion,
                MSBuildVersion = msBuildVersion,
                OperatingSystem = operatingSystem,
                Parallelism = parallelism,
                RestoreObserved = restoreObserved ? true : (bool?)null,
            },
            CategoryTotals = categoryTotals,
            ExecutedTargetCount = executedTargets,
            SkippedTargetCount = skippedTargets,
            PotentiallyCustomTargets = potentiallyCustom,
            CriticalPath = criticalPath,
            CriticalPathTotal = criticalTotal,
        };
    }

    // ──────────────────────────── helpers ────────────────────────────

    private static void CaptureBuildContext(
        Microsoft.Build.Framework.BuildStartedEventArgs bse,
        ref string? configuration,
        ref string? sdkVersion,
        ref string? msBuildVersion,
        ref string? operatingSystem,
        ref int? parallelism)
    {
        var env = bse.BuildEnvironment;
        if (env is null) return;

        if (env.TryGetValue("MSBuildToolsVersion", out var mv) && !string.IsNullOrEmpty(mv))
            msBuildVersion = mv;
        if (env.TryGetValue("MSBuildVersion", out var mv2) && !string.IsNullOrEmpty(mv2))
            msBuildVersion ??= mv2;
        if (env.TryGetValue("NETCoreSdkVersion", out var sv) && !string.IsNullOrEmpty(sv))
            sdkVersion = sv;
        if (env.TryGetValue("OS", out var os) && !string.IsNullOrEmpty(os))
            operatingSystem = os;
        if (env.TryGetValue("Configuration", out var cfg) && !string.IsNullOrEmpty(cfg))
            configuration = cfg;
        if (env.TryGetValue("MSBuildNodeCount", out var nc) && int.TryParse(nc, out var p) && p > 0)
            parallelism = p;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildDependencyEdgesByPath(
        Dictionary<int, int> parentByInstance,
        Dictionary<int, string> instanceToPath)
    {
        // Parent (p) depends on Child (c): p → c means "p depends on c"
        // We collect edges as parentPath → List<childPath>, deduping identical pairs.
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in parentByInstance)
        {
            var childId = kv.Key;
            var parentId = kv.Value;
            if (!instanceToPath.TryGetValue(childId, out var childPath)) continue;
            if (!instanceToPath.TryGetValue(parentId, out var parentPath)) continue;
            if (string.Equals(childPath, parentPath, StringComparison.OrdinalIgnoreCase)) continue;

            if (!edges.TryGetValue(parentPath, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                edges[parentPath] = set;
            }
            set.Add(childPath);
        }

        return edges.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProjectAccumulator
    {
        public required string Name { get; init; }
        public required string FullPath { get; init; }
        public DateTime StartTime { get; init; }
        public DateTime EndTime { get; set; }
        public bool Succeeded { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public TimeSpan Duration => EndTime > StartTime ? EndTime - StartTime : TimeSpan.Zero;
    }

    private sealed record RawTargetTiming
    {
        public int Id { get; init; }
        public int ProjectInstanceId { get; init; }
        public required string Name { get; init; }
        public required string ProjectName { get; init; }
        public DateTime StartTime { get; init; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration => EndTime > StartTime ? EndTime - StartTime : TimeSpan.Zero;
        public TimeSpan ExclusiveDuration { get; set; }
    }
}
