using BuildTimeAnalyzer.Models;
using Microsoft.Build.Logging.StructuredLogger;

namespace BuildTimeAnalyzer.Services;

public sealed class LogAnalyzer
{
    private readonly int _topTargets;

    public LogAnalyzer(int topTargets = 20)
    {
        _topTargets = topTargets;
    }

    /// <summary>
    /// Parses the binary log file using a streaming API to avoid loading the entire tree into memory.
    /// Computes exclusive times by subtracting orchestration task (MSBuild/CallTarget) durations.
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
                        break;
                    }

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

        var totalMs = (buildEnd - buildStart).TotalMilliseconds;

        // ── Compute exclusive target times ─────────────────────────────
        // Exclusive = raw duration minus time spent in MSBuild/CallTarget tasks (child builds).
        var completedTargets = targetTimings.Where(t => t.EndTime > t.StartTime).ToList();

        foreach (var t in completedTargets)
        {
            var orchKey = (t.ProjectInstanceId, t.Id);
            var orchTime = orchTaskDurations.GetValueOrDefault(orchKey);
            var exclusive = t.Duration - orchTime;
            t.ExclusiveDuration = exclusive > TimeSpan.Zero ? exclusive : TimeSpan.Zero;
        }

        // ── Compute exclusive project times (sum of exclusive target times) ──
        var exclusiveProjectTimes = completedTargets
            .GroupBy(t => t.ProjectInstanceId)
            .ToDictionary(
                g => g.Key,
                g => TimeSpan.FromMilliseconds(g.Sum(t => t.ExclusiveDuration.TotalMilliseconds))
            );

        // ── Build per-project list ─────────────────────────────────────
        // Filter out solution metaprojects (they're orchestration, not real projects).
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
                return new ProjectTiming
                {
                    Name = best.Acc.Name,
                    FullPath = best.Acc.FullPath,
                    Duration = best.ExclusiveTime,
                    Succeeded = g.All(x => x.Acc.Succeeded),
                    ErrorCount = g.Sum(x => x.Acc.ErrorCount),
                    WarningCount = g.Sum(x => x.Acc.WarningCount),
                    Percentage = totalMs > 0 ? best.ExclusiveTime.TotalMilliseconds / totalMs * 100 : 0,
                };
            })
            .OrderByDescending(p => p.Duration)
            .ToList();

        // ── Build target list ──────────────────────────────────────────
        // Use exclusive duration; filter out near-zero orchestration targets.
        var topTargetList = completedTargets
            .Where(t => t.ExclusiveDuration > TimeSpan.FromMilliseconds(1))
            .GroupBy(t => (t.Name, t.ProjectName))
            .Select(g =>
            {
                var best = g.OrderByDescending(t => t.ExclusiveDuration).First();
                return new TargetTiming
                {
                    Name = best.Name,
                    ProjectName = best.ProjectName,
                    Duration = best.ExclusiveDuration,
                    Percentage = totalMs > 0 ? best.ExclusiveDuration.TotalMilliseconds / totalMs * 100 : 0,
                };
            })
            .OrderByDescending(t => t.Duration)
            .Take(_topTargets)
            .ToList();

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
        };
    }

    // ──────────────────────────── helpers ────────────────────────────

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
