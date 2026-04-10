using System.Collections;
using BuildTimeAnalyzer.Models;
using Microsoft.Build.Logging.StructuredLogger;

namespace BuildTimeAnalyzer.Services;

public sealed class LogAnalyzer
{
    private const int DrillDownTopN = 3;

    // Span-vs-self outlier rule (matches the spec exactly)
    private const double SpanOutlierMinSpanSeconds = 5.0;
    private const double SpanOutlierMinRatio = 5.0;
    private const double SpanOutlierMinGapSeconds = 3.0;

    private readonly int _topTargets;

    public LogAnalyzer(int topTargets = 20)
    {
        _topTargets = topTargets;
    }

    public System.Threading.Tasks.Task<BuildReport> AnalyzeAsync(string binLogPath, string projectOrSolutionPath, CancellationToken ct = default)
    {
        return System.Threading.Tasks.Task.Run(() => Analyze(binLogPath, projectOrSolutionPath), ct);
    }

    private BuildReport Analyze(string binLogPath, string projectOrSolutionPath)
    {
        var projectTimings = new Dictionary<int, ProjectAccumulator>(256);
        var targetTimings = new List<RawTargetTiming>(4096);

        // Orchestration task (MSBuild/CallTarget) handling
        var runningOrchTasks = new Dictionary<(int ProjectInstanceId, int TaskId), (int TargetId, DateTime StartTime)>();
        var orchTaskDurations = new Dictionary<(int ProjectInstanceId, int TargetId), TimeSpan>();

        // Raw edges: parent project full path → set of dependency full paths (resolved + normalized)
        var rawEdges = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        int executedTargets = 0;
        int skippedTargets = 0;
        bool restoreObserved = false;

        string? configuration = null;
        string? sdkVersion = null;
        string? msBuildVersion = null;
        string? operatingSystem = null;
        int? parallelism = null;

        int errorCount = 0;
        int warningCount = 0;
        int attributedWarningCount = 0;
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
                        var projectFile = pse.ProjectFile ?? "";
                        projectTimings.TryAdd(key, new ProjectAccumulator
                        {
                            Name = Path.GetFileNameWithoutExtension(projectFile == "" ? "Unknown" : projectFile),
                            FullPath = projectFile,
                            StartTime = pse.Timestamp,
                        });

                        // Fallback item extraction — ProjectStartedEventArgs.Items is often null in binlogs,
                        // the authoritative source is ProjectEvaluationFinishedEventArgs handled below.
                        if (pse.Items is not null)
                            ExtractProjectReferences(projectFile, pse.Items, rawEdges);

                        if (configuration is null && pse.GlobalProperties is not null)
                        {
                            if (pse.GlobalProperties.TryGetValue("Configuration", out var cfg) && !string.IsNullOrEmpty(cfg))
                                configuration = cfg;
                        }

                        if (!restoreObserved && pse.TargetNames is string tn && tn.Contains("Restore", StringComparison.OrdinalIgnoreCase))
                            restoreObserved = true;
                        break;
                    }

                case Microsoft.Build.Framework.ProjectEvaluationFinishedEventArgs pef:
                    {
                        // Primary source of ProjectReference items — always populated in modern binlogs
                        if (pef.Items is not null && !string.IsNullOrEmpty(pef.ProjectFile))
                            ExtractProjectReferences(pef.ProjectFile, pef.Items, rawEdges);
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
                        {
                            acc.WarningCount++;
                            attributedWarningCount++;
                        }
                    }
                    break;
            }
        }

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

        var totalSelfMs = completedTargets.Sum(t => t.ExclusiveDuration.TotalMilliseconds);

        var exclusiveProjectTimes = completedTargets
            .GroupBy(t => t.ProjectInstanceId)
            .ToDictionary(
                g => g.Key,
                g => TimeSpan.FromMilliseconds(g.Sum(t => t.ExclusiveDuration.TotalMilliseconds))
            );

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

        // ── Project list ────────────────────────────────────────────────
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

        // ── Target list + categories ───────────────────────────────────
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

        var categoryTotals = allTargets
            .GroupBy(t => t.Category)
            .ToDictionary(
                g => g.Key,
                g => TimeSpan.FromMilliseconds(g.Sum(t => t.SelfTime.TotalMilliseconds))
            );

        var potentiallyCustom = allTargets
            .Where(t => t.Category == TargetCategory.Uncategorized)
            .OrderByDescending(t => t.SelfTime)
            .ToList();

        // ── Reference overhead stats ───────────────────────────────────
        var referenceOverhead = ComputeReferenceOverhead(allTargets, projectList);

        // ── Span-vs-self outliers ──────────────────────────────────────
        var spanOutliers = projectList
            .Where(p =>
                p.Span.TotalSeconds >= SpanOutlierMinSpanSeconds &&
                p.SelfTime.TotalMilliseconds > 0 &&
                p.Span.TotalMilliseconds / p.SelfTime.TotalMilliseconds >= SpanOutlierMinRatio &&
                (p.Span - p.SelfTime).TotalSeconds >= SpanOutlierMinGapSeconds)
            .OrderByDescending(p => p.Span.TotalMilliseconds / Math.Max(1, p.SelfTime.TotalMilliseconds))
            .ToList();

        // ── Dependency graph ───────────────────────────────────────────
        var rawEdgesReadOnly = rawEdges.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
        var graph = DependencyGraphAnalyzer.Build(projectList, rawEdgesReadOnly);
        var depsMap = DependencyGraphAnalyzer.ToDependencyMap(projectList, rawEdgesReadOnly);

        // ── Critical path — only if the graph is usable ────────────────
        IReadOnlyList<ProjectTiming> criticalPath = [];
        TimeSpan criticalPathTotal = TimeSpan.Zero;
        if (graph.IsUsable)
        {
            (criticalPath, criticalPathTotal) = CriticalPathAnalyzer.Compute(projectList, depsMap, wallClock);
        }

        // ── Drill-down ─────────────────────────────────────────────────
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
            AttributedWarningCount = attributedWarningCount,
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
            ReferenceOverhead = referenceOverhead,
            SpanOutliers = spanOutliers,
            Graph = graph,
            CriticalPath = criticalPath,
            CriticalPathTotal = criticalPathTotal,
        };
    }

    // ──────────────────────────── helpers ────────────────────────────

    private static void ExtractProjectReferences(
        string projectFile,
        IEnumerable items,
        Dictionary<string, HashSet<string>> rawEdges)
    {
        if (string.IsNullOrEmpty(projectFile)) return;

        string? projectDir;
        string fromKey;
        try
        {
            projectDir = Path.GetDirectoryName(projectFile);
            fromKey = Path.GetFullPath(projectFile);
        }
        catch
        {
            return;
        }
        if (projectDir is null) return;

        foreach (var raw in items)
        {
            // Items come through as DictionaryEntry(string itemType, ITaskItem item).
            // Be defensive about alternative shapes some MSBuild versions use.
            string? itemType = null;
            Microsoft.Build.Framework.ITaskItem? item = null;

            if (raw is DictionaryEntry entry)
            {
                itemType = entry.Key as string;
                item = entry.Value as Microsoft.Build.Framework.ITaskItem;
            }

            if (itemType is null || item is null) continue;
            if (!itemType.Equals("ProjectReference", StringComparison.Ordinal)) continue;

            var spec = item.ItemSpec;
            if (string.IsNullOrEmpty(spec)) continue;

            string resolved;
            try
            {
                resolved = Path.IsPathRooted(spec) ? spec : Path.Combine(projectDir, spec);
                resolved = Path.GetFullPath(resolved);
            }
            catch
            {
                continue;
            }

            if (!rawEdges.TryGetValue(fromKey, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                rawEdges[fromKey] = set;
            }
            set.Add(resolved);
        }
    }

    private static ReferenceOverheadStats? ComputeReferenceOverhead(
        List<TargetTiming> allTargets,
        List<ProjectTiming> projects)
    {
        if (projects.Count == 0) return null;

        // Per-project reference self time (grouped by ProjectName)
        var refByProject = allTargets
            .Where(t => t.Category == TargetCategory.References)
            .GroupBy(t => t.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => TimeSpan.FromMilliseconds(g.Sum(t => t.SelfTime.TotalMilliseconds)),
                StringComparer.OrdinalIgnoreCase);

        if (refByProject.Count == 0) return null;

        var total = TimeSpan.FromMilliseconds(refByProject.Values.Sum(v => v.TotalMilliseconds));
        if (total <= TimeSpan.Zero) return null;

        var totalSelfMs = projects.Sum(p => p.SelfTime.TotalMilliseconds);
        var pct = totalSelfMs > 0 ? total.TotalMilliseconds / totalSelfMs * 100 : 0;

        var paying = refByProject.Values.Where(v => v > TimeSpan.Zero).ToList();
        var median = paying.Count == 0
            ? TimeSpan.Zero
            : paying.OrderBy(v => v).ElementAt(paying.Count / 2);

        var top = refByProject
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .Select(kv => new ReferenceOverheadProject { ProjectName = kv.Key, SelfTime = kv.Value })
            .ToList();

        return new ReferenceOverheadStats
        {
            TotalSelfTime = total,
            SelfPercent = pct,
            PayingProjectsCount = paying.Count,
            TotalProjectsCount = projects.Count,
            MedianPerPayingProject = median,
            TopProjects = top,
        };
    }

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
