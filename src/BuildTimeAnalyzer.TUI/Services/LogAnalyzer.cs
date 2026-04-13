using System.Collections;
using BuildTimeAnalyzer.Models;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;

namespace BuildTimeAnalyzer.Services;

public sealed class LogAnalyzer
{
    private const int DrillDownTopN = 5;

    // Span-vs-self outlier rule
    private const double SpanOutlierMinSpanSeconds = 5.0;
    private const double SpanOutlierMinRatio = 5.0;
    private const double SpanOutlierMinGapSeconds = 3.0;

    private readonly int _topTargets;

    public LogAnalyzer(int topTargets = 20)
    {
        _topTargets = topTargets;
    }

    public Task<BuildReport> AnalyzeAsync(
        string binLogPath,
        string projectOrSolutionPath,
        CancellationToken ct = default
    )
    {
        return System.Threading.Tasks.Task.Run(
            () => Analyze(binLogPath, projectOrSolutionPath),
            ct
        );
    }

    private BuildReport Analyze(string binLogPath, string projectOrSolutionPath)
    {
        var projectTimings = new Dictionary<int, ProjectAccumulator>(256);
        var targetTimings = new List<RawTargetTiming>(4096);

        var runningOrchTasks =
            new Dictionary<
                (int ProjectInstanceId, int TaskId),
                (int TargetId, DateTime StartTime)
            >();
        var orchTaskDurations = new Dictionary<(int ProjectInstanceId, int TargetId), TimeSpan>();

        var rawEdges = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // All task timings (not just orchestration)
        var allRawTasks = new List<RawTaskTiming>(8192);
        // Active Csc/Vbc tasks for collecting ReportAnalyzer messages
        var activeCscTasks = new Dictionary<(int ProjectInstanceId, int TaskId), CscTaskAccumulator>();
        var completedCscTasks = new List<CscTaskAccumulator>();
        // Target skip reasons
        var skipInfos = new List<TargetSkipInfo>();

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
        var warningCodeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        DateTime buildStart = DateTime.MaxValue;
        DateTime buildEnd = DateTime.MinValue;
        bool succeeded = false;

        var reader = new BinLogReader();

        foreach (var record in reader.ReadRecords(binLogPath))
        {
            if (record.Args is null)
                continue;

            switch (record.Args)
            {
                case BuildStartedEventArgs bse:
                    if (bse.Timestamp < buildStart)
                        buildStart = bse.Timestamp;
                    CaptureBuildContext(
                        bse,
                        ref configuration,
                        ref sdkVersion,
                        ref msBuildVersion,
                        ref operatingSystem,
                        ref parallelism
                    );
                    break;

                case BuildFinishedEventArgs bfe:
                    if (bfe.Timestamp > buildEnd)
                        buildEnd = bfe.Timestamp;
                    succeeded = bfe.Succeeded;
                    break;

                case ProjectStartedEventArgs pse:
                {
                    var key = pse.BuildEventContext?.ProjectInstanceId ?? -1;
                    if (key < 0)
                        break;
                    var projectFile = pse.ProjectFile ?? "";
                    projectTimings.TryAdd(
                        key,
                        new ProjectAccumulator
                        {
                            Name = Path.GetFileNameWithoutExtension(
                                projectFile == "" ? "Unknown" : projectFile
                            ),
                            FullPath = projectFile,
                            StartTime = pse.Timestamp,
                        }
                    );

                    if (pse.Items is not null)
                        ExtractProjectReferences(projectFile, pse.Items, rawEdges);

                    if (configuration is null && pse.GlobalProperties is not null)
                    {
                        if (
                            pse.GlobalProperties.TryGetValue("Configuration", out var cfg)
                            && !string.IsNullOrEmpty(cfg)
                        )
                            configuration = cfg;
                    }

                    if (
                        !restoreObserved
                        && pse.TargetNames is string tn
                        && tn.Contains("Restore", StringComparison.OrdinalIgnoreCase)
                    )
                        restoreObserved = true;
                    break;
                }

                case ProjectEvaluationFinishedEventArgs pef:
                    if (pef.Items is not null && !string.IsNullOrEmpty(pef.ProjectFile))
                        ExtractProjectReferences(pef.ProjectFile, pef.Items, rawEdges);
                    break;

                case ProjectFinishedEventArgs pfe:
                {
                    var key = pfe.BuildEventContext?.ProjectInstanceId ?? -1;
                    if (key < 0 || !projectTimings.TryGetValue(key, out var acc))
                        break;
                    acc.EndTime = pfe.Timestamp;
                    acc.Succeeded = pfe.Succeeded;
                    break;
                }

                case TargetStartedEventArgs tse:
                {
                    var ctx = tse.BuildEventContext;
                    var targetId = ctx?.TargetId ?? -1;
                    if (targetId < 0)
                        break;
                    targetTimings.Add(
                        new RawTargetTiming
                        {
                            Id = targetId,
                            ProjectInstanceId = ctx!.ProjectInstanceId,
                            Name = tse.TargetName ?? "Unknown",
                            ProjectName = Path.GetFileNameWithoutExtension(tse.ProjectFile ?? ""),
                            StartTime = tse.Timestamp,
                        }
                    );
                    executedTargets++;
                    break;
                }

                case TargetSkippedEventArgs tsk:
                    skippedTargets++;
                    skipInfos.Add(new TargetSkipInfo
                    {
                        TargetName = tsk.TargetName ?? "Unknown",
                        ProjectName = Path.GetFileNameWithoutExtension(tsk.ProjectFile ?? ""),
                        SkipReason = tsk.SkipReason.ToString(),
                        Condition = tsk.Condition,
                        EvaluatedCondition = tsk.EvaluatedCondition,
                    });
                    break;

                case TargetFinishedEventArgs tfe:
                {
                    var ctx = tfe.BuildEventContext;
                    var targetId = ctx?.TargetId ?? -1;
                    var projInstanceId = ctx?.ProjectInstanceId ?? -1;
                    if (targetId < 0)
                        break;
                    for (int i = targetTimings.Count - 1; i >= 0; i--)
                    {
                        if (
                            targetTimings[i].Id == targetId
                            && targetTimings[i].ProjectInstanceId == projInstanceId
                            && targetTimings[i].EndTime == default
                        )
                        {
                            targetTimings[i] = targetTimings[i] with { EndTime = tfe.Timestamp };
                            break;
                        }
                    }
                    break;
                }

                case TaskStartedEventArgs taskSe:
                {
                    var ctx = taskSe.BuildEventContext;
                    if (ctx is not { ProjectInstanceId: >= 0, TargetId: >= 0, TaskId: >= 0 })
                        break;

                    // Track ALL tasks for task-level timing
                    allRawTasks.Add(new RawTaskTiming
                    {
                        TaskId = ctx.TaskId,
                        ProjectInstanceId = ctx.ProjectInstanceId,
                        TargetId = ctx.TargetId,
                        Name = taskSe.TaskName ?? "Unknown",
                        ProjectName = Path.GetFileNameWithoutExtension(taskSe.ProjectFile ?? ""),
                        StartTime = taskSe.Timestamp,
                    });

                    // Orchestration task tracking (for exclusive target time)
                    if (taskSe.TaskName is "MSBuild" or "CallTarget")
                    {
                        runningOrchTasks[(ctx.ProjectInstanceId, ctx.TaskId)] = (
                            ctx.TargetId,
                            taskSe.Timestamp
                        );
                    }

                    // Csc/Vbc task tracking (for ReportAnalyzer output)
                    if (taskSe.TaskName is "Csc" or "Vbc")
                    {
                        activeCscTasks[(ctx.ProjectInstanceId, ctx.TaskId)] = new CscTaskAccumulator
                        {
                            ProjectName = Path.GetFileNameWithoutExtension(taskSe.ProjectFile ?? ""),
                            StartTime = taskSe.Timestamp,
                        };
                    }
                    break;
                }

                case TaskFinishedEventArgs taskFe:
                {
                    var ctx = taskFe.BuildEventContext;
                    if (ctx is not { ProjectInstanceId: >= 0, TaskId: >= 0 })
                        break;

                    // Close the task timing
                    var taskKey = (ctx.ProjectInstanceId, ctx.TaskId);
                    for (int i = allRawTasks.Count - 1; i >= 0; i--)
                    {
                        if (allRawTasks[i].TaskId == ctx.TaskId &&
                            allRawTasks[i].ProjectInstanceId == ctx.ProjectInstanceId &&
                            allRawTasks[i].EndTime == default)
                        {
                            allRawTasks[i] = allRawTasks[i] with { EndTime = taskFe.Timestamp };
                            break;
                        }
                    }

                    // Orchestration task duration (existing logic)
                    if (taskFe.TaskName is "MSBuild" or "CallTarget")
                    {
                        if (runningOrchTasks.Remove(taskKey, out var info))
                        {
                            var duration = taskFe.Timestamp - info.StartTime;
                            if (duration > TimeSpan.Zero)
                            {
                                var orchKey = (ctx.ProjectInstanceId, info.TargetId);
                                orchTaskDurations[orchKey] =
                                    orchTaskDurations.GetValueOrDefault(orchKey) + duration;
                            }
                        }
                    }

                    // Close Csc/Vbc task accumulator and move to completed list
                    if (taskFe.TaskName is "Csc" or "Vbc" &&
                        activeCscTasks.Remove(taskKey, out var cscAcc))
                    {
                        cscAcc.EndTime = taskFe.Timestamp;
                        completedCscTasks.Add(cscAcc);
                    }
                    break;
                }

                case BuildMessageEventArgs msg:
                {
                    // Collect messages from Csc/Vbc tasks for ReportAnalyzer parsing
                    var ctx = msg.BuildEventContext;
                    if (ctx is { ProjectInstanceId: >= 0, TaskId: >= 0 } &&
                        msg.Message is not null &&
                        activeCscTasks.TryGetValue((ctx.ProjectInstanceId, ctx.TaskId), out var csc))
                    {
                        csc.Messages.Add(msg.Message);
                    }
                    break;
                }

                case BuildErrorEventArgs:
                    errorCount++;

                    {
                        var ctx = ((BuildErrorEventArgs)record.Args).BuildEventContext;
                        var key = ctx?.ProjectInstanceId ?? -1;
                        if (key >= 0 && projectTimings.TryGetValue(key, out var acc))
                            acc.ErrorCount++;
                    }
                    break;

                case BuildWarningEventArgs warnEvent:
                    warningCount++;

                    {
                        var ctx = warnEvent.BuildEventContext;
                        var key = ctx?.ProjectInstanceId ?? -1;
                        if (key >= 0 && projectTimings.TryGetValue(key, out var acc))
                        {
                            acc.WarningCount++;
                            attributedWarningCount++;
                        }
                        if (!string.IsNullOrEmpty(warnEvent.Code))
                            warningCodeCounts[warnEvent.Code] = warningCodeCounts.GetValueOrDefault(warnEvent.Code) + 1;
                    }
                    break;
            }
        }

        if (buildStart == DateTime.MaxValue)
            buildStart = DateTime.UtcNow;
        if (buildEnd == DateTime.MinValue)
            buildEnd = buildStart;

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
                g =>
                    (
                        First: g.Min(t => t.StartTime) - buildStart,
                        Last: g.Max(t => t.EndTime) - buildStart
                    )
            );

        // ── Project list ────────────────────────────────────────────────
        var projectList = projectTimings
            .Where(kv => kv.Value.EndTime > kv.Value.StartTime)
            .Where(kv =>
                !kv.Value.FullPath.EndsWith(".metaproj", StringComparison.OrdinalIgnoreCase)
                && !kv.Value.FullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            )
            .Select(kv =>
                (
                    InstanceId: kv.Key,
                    Acc: kv.Value,
                    ExclusiveTime: exclusiveProjectTimes.GetValueOrDefault(kv.Key)
                )
            )
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
                    SelfPercent =
                        totalSelfMs > 0
                            ? best.ExclusiveTime.TotalMilliseconds / totalSelfMs * 100
                            : 0,
                    StartOffset = span.First,
                    EndOffset = span.Last,
                    KindHeuristic = ProjectKindHeuristic.Classify(best.Acc.Name),
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
                    SelfPercent =
                        totalSelfMs > 0
                            ? best.ExclusiveDuration.TotalMilliseconds / totalSelfMs * 100
                            : 0,
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

        // Per-project category breakdowns (for drill-down and project-count-tax analysis)
        var categoryByProject = allTargets
            .GroupBy(t => t.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g =>
                    (IReadOnlyDictionary<TargetCategory, TimeSpan>)
                        g.GroupBy(t => t.Category)
                            .ToDictionary(
                                x => x.Key,
                                x =>
                                    TimeSpan.FromMilliseconds(
                                        x.Sum(t => t.SelfTime.TotalMilliseconds)
                                    )
                            ),
                StringComparer.OrdinalIgnoreCase
            );

        // ── Reference overhead stats ───────────────────────────────────
        var referenceOverhead = ComputeReferenceOverhead(allTargets, projectList);

        // ── Span-vs-self outliers ──────────────────────────────────────
        var spanOutliers = projectList
            .Where(p =>
                p.Span.TotalSeconds >= SpanOutlierMinSpanSeconds
                && p.SelfTime.TotalMilliseconds > 0
                && p.Span.TotalMilliseconds / p.SelfTime.TotalMilliseconds >= SpanOutlierMinRatio
                && (p.Span - p.SelfTime).TotalSeconds >= SpanOutlierMinGapSeconds
            )
            .OrderByDescending(p =>
                p.Span.TotalMilliseconds / Math.Max(1, p.SelfTime.TotalMilliseconds)
            )
            .ToList();

        // ── Project count tax ──────────────────────────────────────────
        var projectCountTax = ComputeProjectCountTax(projectList, categoryByProject, spanOutliers);

        // ── Dependency graph ───────────────────────────────────────────
        var rawEdgesReadOnly = rawEdges.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.ToList(),
            StringComparer.OrdinalIgnoreCase
        );
        var graph = DependencyGraphAnalyzer.Build(projectList, rawEdgesReadOnly);
        var depsMap = DependencyGraphAnalyzer.ToDependencyMap(projectList, rawEdgesReadOnly);

        // ── Critical path (always returns validation) ──────────────────
        var (criticalPath, criticalPathTotal, cpValidation) = CriticalPathAnalyzer.Compute(
            projectList,
            depsMap,
            wallClock,
            graph.IsUsable
        );

        // ── Drill-down: populate Targets + CategoryBreakdown for top N and critical path ──
        var drillDownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in projectList.Take(DrillDownTopN))
            drillDownPaths.Add(p.FullPath);
        foreach (var p in criticalPath)
            drillDownPaths.Add(p.FullPath);

        if (drillDownPaths.Count > 0)
        {
            var targetsByProjectName = allTargets
                .GroupBy(t => t.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(t => t.SelfTime).Take(5).ToList(),
                    StringComparer.OrdinalIgnoreCase
                );

            for (int i = 0; i < projectList.Count; i++)
            {
                if (!drillDownPaths.Contains(projectList[i].FullPath))
                    continue;
                var targets =
                    targetsByProjectName.GetValueOrDefault(projectList[i].Name)
                    ?? new List<TargetTiming>();
                var breakdown =
                    categoryByProject.GetValueOrDefault(projectList[i].Name)
                    ?? new Dictionary<TargetCategory, TimeSpan>();
                projectList[i] = projectList[i] with
                {
                    Targets = targets,
                    CategoryBreakdown = breakdown,
                };
            }
            criticalPath = criticalPath
                .Select(cp =>
                    projectList.FirstOrDefault(p =>
                        string.Equals(p.FullPath, cp.FullPath, StringComparison.OrdinalIgnoreCase)
                    ) ?? cp
                )
                .ToList();
        }

        // ── Task-level timing ──────────────────────────────────────────
        // Resolve target names for each task and compute task self time
        var targetNameLookup = new Dictionary<(int ProjectInstanceId, int TargetId), string>();
        foreach (var t in targetTimings)
            targetNameLookup.TryAdd((t.ProjectInstanceId, t.Id), t.Name);

        var completedTasks = allRawTasks.Where(t => t.EndTime > t.StartTime).ToList();
        var totalTaskMs = completedTasks.Sum(t => t.Duration.TotalMilliseconds);

        var taskTimingList = completedTasks
            .Select(t => new TaskTiming
            {
                TaskName = t.Name,
                TargetName = targetNameLookup.GetValueOrDefault((t.ProjectInstanceId, t.TargetId), "Unknown"),
                ProjectName = t.ProjectName,
                SelfTime = t.Duration,
                SelfPercent = totalTaskMs > 0 ? t.Duration.TotalMilliseconds / totalTaskMs * 100 : 0,
            })
            .OrderByDescending(t => t.SelfTime)
            .ToList();
        var topTaskList = taskTimingList.Take(30).ToList();

        // ── Analyzer reports (from ReportAnalyzer output in Csc messages) ──
        // Also drain any still-active tasks (task finish event may have been missing)
        completedCscTasks.AddRange(activeCscTasks.Values);
        var analyzerReports = new List<AnalyzerReport>();
        foreach (var acc in completedCscTasks)
        {
            if (acc.Messages.Count == 0) continue;
            var cscWallTime = acc.EndTime > acc.StartTime ? acc.EndTime - acc.StartTime : TimeSpan.Zero;
            var report = AnalyzerReportParser.Parse(acc.ProjectName, cscWallTime, acc.Messages);
            if (report is not null) analyzerReports.Add(report);
        }

        // ── Project diagnoses ("Why is this slow?") ──
        var projectDiagnoses = ProjectDiagnosisBuilder.Build(
            projectList, analyzerReports, criticalPath, spanOutliers, taskTimingList);

        var warningsByCode = warningCodeCounts
            .Select(kv => new WarningCodeTally
            {
                Code = kv.Key,
                Prefix = ExtractPrefix(kv.Key),
                Count = kv.Value,
            })
            .OrderByDescending(t => t.Count)
            .ToList();

        return new BuildReport
        {
            ProjectOrSolutionPath = projectOrSolutionPath,
            StartTime = buildStart,
            EndTime = buildEnd,
            Succeeded = succeeded,
            ErrorCount = errorCount,
            WarningCount = warningCount,
            AttributedWarningCount = attributedWarningCount,
            WarningsByCode = warningsByCode,
            Projects = projectList,
            TopTargets = topTargetList,
            TopTasks = topTaskList,
            SkipReasons = skipInfos,
            AnalyzerReports = analyzerReports,
            ProjectDiagnoses = projectDiagnoses,
            Context = new BuildContext
            {
                Configuration = configuration,
                SdkVersion = sdkVersion,
                MSBuildVersion = msBuildVersion,
                OperatingSystem = operatingSystem,
                Parallelism = parallelism,
                RestoreObserved = restoreObserved ? true : null,
            },
            CategoryTotals = categoryTotals,
            ExecutedTargetCount = executedTargets,
            SkippedTargetCount = skippedTargets,
            PotentiallyCustomTargets = potentiallyCustom,
            ReferenceOverhead = referenceOverhead,
            SpanOutliers = spanOutliers,
            ProjectCountTax = projectCountTax,
            Graph = graph,
            CriticalPath = criticalPath,
            CriticalPathTotal = criticalPathTotal,
            CriticalPathValidation = cpValidation,
        };
    }

    // ──────────────────────────── helpers ────────────────────────────

    private static void ExtractProjectReferences(
        string projectFile,
        IEnumerable items,
        Dictionary<string, HashSet<string>> rawEdges
    )
    {
        if (string.IsNullOrEmpty(projectFile))
            return;

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
        if (projectDir is null)
            return;

        foreach (var raw in items)
        {
            string? itemType = null;
            ITaskItem? item = null;

            if (raw is DictionaryEntry entry)
            {
                itemType = entry.Key as string;
                item = entry.Value as ITaskItem;
            }

            if (itemType is null || item is null)
                continue;
            if (!itemType.Equals("ProjectReference", StringComparison.Ordinal))
                continue;

            var spec = item.ItemSpec;
            if (string.IsNullOrEmpty(spec))
                continue;

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
        List<ProjectTiming> projects
    )
    {
        if (projects.Count == 0)
            return null;

        var refByProject = allTargets
            .Where(t => t.Category == TargetCategory.References)
            .GroupBy(t => t.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => TimeSpan.FromMilliseconds(g.Sum(t => t.SelfTime.TotalMilliseconds)),
                StringComparer.OrdinalIgnoreCase
            );

        if (refByProject.Count == 0)
            return null;

        var total = TimeSpan.FromMilliseconds(refByProject.Values.Sum(v => v.TotalMilliseconds));
        if (total <= TimeSpan.Zero)
            return null;

        var totalSelfMs = projects.Sum(p => p.SelfTime.TotalMilliseconds);
        var pct = totalSelfMs > 0 ? total.TotalMilliseconds / totalSelfMs * 100 : 0;

        var paying = refByProject.Values.Where(v => v > TimeSpan.Zero).ToList();
        var median =
            paying.Count == 0 ? TimeSpan.Zero : paying.OrderBy(v => v).ElementAt(paying.Count / 2);

        var top = refByProject
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .Select(kv => new ReferenceOverheadProject
            {
                ProjectName = kv.Key,
                SelfTime = kv.Value,
            })
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

    private static ProjectCountTaxStats ComputeProjectCountTax(
        List<ProjectTiming> projects,
        Dictionary<string, IReadOnlyDictionary<TargetCategory, TimeSpan>> categoryByProject,
        List<ProjectTiming> spanOutliers
    )
    {
        int refsExceedCompile = 0;
        int refsMajority = 0;

        foreach (var p in projects)
        {
            if (!categoryByProject.TryGetValue(p.Name, out var cats))
                continue;
            var refs = cats.GetValueOrDefault(TargetCategory.References).TotalMilliseconds;
            var compile = cats.GetValueOrDefault(TargetCategory.Compile).TotalMilliseconds;
            var selfMs = p.SelfTime.TotalMilliseconds;

            if (refs > compile && refs > 0)
                refsExceedCompile++;
            if (selfMs > 0 && refs / selfMs > 0.5)
                refsMajority++;
        }

        var perKind = projects
            .GroupBy(p => p.KindHeuristic)
            .Select(g =>
            {
                var sorted = g.OrderBy(p => p.SelfTime.TotalMilliseconds).ToList();
                var medianSelf = sorted[sorted.Count / 2].SelfTime;
                var sortedSpan = g.OrderBy(p => p.Span.TotalMilliseconds).ToList();
                var medianSpan = sortedSpan[sortedSpan.Count / 2].Span;
                var ratios = g.Where(p => p.SelfTime.TotalMilliseconds > 0)
                    .Select(p => p.Span.TotalMilliseconds / p.SelfTime.TotalMilliseconds)
                    .OrderBy(r => r)
                    .ToList();
                var medianRatio = ratios.Count == 0 ? 0 : ratios[ratios.Count / 2];

                return new ProjectKindStats
                {
                    Kind = g.Key,
                    Count = g.Count(),
                    MedianSelfTime = medianSelf,
                    MedianSpan = medianSpan,
                    MedianSpanToSelfRatio = medianRatio,
                };
            })
            .OrderBy(s => s.Kind)
            .ToList();

        return new ProjectCountTaxStats
        {
            ReferencesExceedCompileCount = refsExceedCompile,
            ReferencesMajorityCount = refsMajority,
            TinySelfHugeSpanCount = spanOutliers.Count,
            TotalProjects = projects.Count,
            PerKindStats = perKind,
        };
    }

    private static void CaptureBuildContext(
        BuildStartedEventArgs bse,
        ref string? configuration,
        ref string? sdkVersion,
        ref string? msBuildVersion,
        ref string? operatingSystem,
        ref int? parallelism
    )
    {
        var env = bse.BuildEnvironment;
        if (env is null)
            return;

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

    private static string ExtractPrefix(string code)
    {
        // Leading ASCII letters form the prefix (CS, CA, IDE, NETSDK, NU, MSB, ...).
        int i = 0;
        while (i < code.Length && char.IsAsciiLetter(code[i])) i++;
        return i == 0 ? "OTHER" : code[..i];
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

    private sealed record RawTaskTiming
    {
        public int TaskId { get; init; }
        public int ProjectInstanceId { get; init; }
        public int TargetId { get; init; }
        public required string Name { get; init; }
        public required string ProjectName { get; init; }
        public DateTime StartTime { get; init; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration => EndTime > StartTime ? EndTime - StartTime : TimeSpan.Zero;
    }

    private sealed class CscTaskAccumulator
    {
        public required string ProjectName { get; init; }
        public DateTime StartTime { get; init; }
        public DateTime EndTime { get; set; }
        public List<string> Messages { get; } = new();
    }
}
