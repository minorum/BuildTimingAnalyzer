using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Rendering;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Evidence-driven analysis. Every finding is split into strict layers:
///   • Title / Measured       — purely factual, no interpretation
///   • LikelyExplanation      — heuristic hypothesis (optional, clearly tagged)
///   • InvestigationSuggestion — concrete next step
///
/// Recommendations are investigation targets only — never structural conclusions from timing data.
/// </summary>
public static class BuildAnalyzer
{
    // ── Named thresholds ─────────────────────────────────────────────
    // The five "policy" thresholds below live on AnalyzerThresholds so btanalyzer.json can tune
    // them per project. The generator-specific constants that follow stay internal.

    // Generator anomalies
    private const double GenLoggingOutlierMinSeconds = 5;
    private const double GenLoggingOutlierProjectShareThreshold = 0.5;
    private const double ComInterfaceGeneratorMinSecondsForFinding = 10;
    private const double CSharpAnalyzersInNonRoslynMinSeconds = 5;

    private static readonly HashSet<string> KnownGenLoggingAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Gen.Logging",
    };
    private static readonly HashSet<string> KnownComInterfaceGeneratorAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Interop.ComInterfaceGenerator",
    };
    private const string CSharpAnalyzersAssembly = "Microsoft.CodeAnalysis.CSharp.Analyzers";

    public static BuildAnalysis Analyze(BuildReport report, AnalyzerThresholds? thresholds = null)
    {
        var t = thresholds ?? AnalyzerThresholds.Default;

        if (report.TotalDuration < TimeSpan.FromSeconds(1))
            return new BuildAnalysis { Findings = [], Recommendations = [] };

        var findings = new List<AnalysisFinding>();

        // Kept findings name a specific project + measured number + concrete next step.
        // Removed: LargestGap, SpanWaitingPattern, CriticalPathConcentration, WarningConcentration,
        // BroadReferenceOverhead, OutlierTargets, ComInterfaceGenerator no-op — either too generic,
        // duplicates the blocking chain, or "no action needed" (trivia, not findings).
        DetectLargestShare(report, findings, t);
        DetectSerializedBuild(report, findings, t);
        DetectDependencyCycles(report, findings);
        DetectCostlyResolvePackageAssets(report, findings, t);
        DetectTfmNegotiationOverhead(report, findings, t);
        DetectBenchmarksOnCriticalPath(report, findings);
        DetectWarningHeavyCriticalPath(report, findings, t);
        DetectGenLoggingOutlier(report, findings);
        DetectComInterfaceGeneratorWithUsages(report, findings);
        DetectCSharpAnalyzersInNonRoslynProject(report, findings);
        DetectHeavyTransitivesInTestOrBenchmark(report, findings);
        DetectProjectCountTax(report, findings, t);

        // Ranking is the report's spine: Critical before Warning before Info, and within a severity
        // the finding covering the most time first (UpperBoundImpactPercent). Detection order breaks
        // ties so consecutive runs number identically. This ordering is authoritative — every consumer
        // (Markdown, HTML, the "inspect next" list) reads findings in this order rather than re-ranking.
        var ordered = findings
            .Select((f, idx) => (f, idx))
            .OrderBy(x => x.f.Severity switch
            {
                FindingSeverity.Critical => 0,
                FindingSeverity.Warning => 1,
                _ => 2,
            })
            .ThenByDescending(x => x.f.UpperBoundImpactPercent ?? 0)
            .ThenBy(x => x.idx)
            .Select(x => x.f)
            .ToList();
        findings = ordered;

        for (int i = 0; i < findings.Count; i++)
            findings[i] = findings[i] with { Number = i + 1 };

        // Recommendations removed: the per-finding InvestigationSuggestion is the action item,
        // duplicating it in a separate section adds length without adding information.
        return new BuildAnalysis { Findings = findings, Recommendations = [] };
    }

    // ──────────────────────────── Findings ────────────────────────────

    private static void DetectLargestShare(BuildReport report, List<AnalysisFinding> findings, AnalyzerThresholds t)
    {
        if (report.Projects.Count < 2) return;

        var top = report.Projects[0];
        if (top.SelfPercent <= t.LargestShareWarningPercent) return;

        var inspectTarget = top.Targets.Count > 0
            ? $"{top.Name} — start with target {top.Targets[0].Name} ({Fmt(top.Targets[0].SelfTime)}, {CategoryLabel(top.Targets[0].Category)})"
            : $"{top.Name}";

        // Gap-to-next quantifies how concentrated the cost is on this one project. A top project at
        // 30% is a very different problem when the runner-up is at 5% (a genuine outlier worth
        // isolating) versus 27% (a broad-front cost where fixing one project barely moves the needle).
        var next = report.Projects[1];
        var nextMs = next.SelfTime.TotalMilliseconds;
        string gap, multipleStr;
        if (nextMs <= 0)
        {
            // A negligible runner-up means the cost is maximally concentrated — never "close behind".
            gap = $" The next-slowest ({next.Name}) has negligible self time, so essentially all the cost is here.";
            multipleStr = "inf";
        }
        else
        {
            var multiple = top.SelfTime.TotalMilliseconds / nextMs;
            multipleStr = $"{multiple:F1}x";
            gap = multiple >= 1.5
                ? $" That is {multiple:F1}× the next-slowest ({next.Name}, {Fmt(next.SelfTime)}) — the cost is concentrated here."
                : $" The next-slowest ({next.Name}, {Fmt(next.SelfTime)}) is close behind, so the cost is spread across several projects, not isolated to one.";
        }

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"{top.Name} dominates build time",
            Severity = top.SelfPercent > t.LargestShareCriticalPercent ? FindingSeverity.Critical : FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            Measured = $"{top.Name}: {Fmt(top.SelfTime)} ({top.SelfPercent:F1}% of total).{gap}",
            LikelyExplanation = null,
            InvestigationSuggestion = $"Inspect {inspectTarget}.",
            Evidence = $"SelfPercent={top.SelfPercent:F1}%, SelfTime={Fmt(top.SelfTime)}, NextMultiple={multipleStr}",
            ThresholdName = $"top-project-share > {t.LargestShareWarningPercent:F0}%",
            UpperBoundImpactPercent = top.SelfPercent,
        });
    }

    private static void DetectSerializedBuild(BuildReport report, List<AnalysisFinding> findings, AnalyzerThresholds t)
    {
        // Needs a known node budget to make a claim — MSBuildNodeCount is how many parallel build
        // nodes MSBuild actually ran. Without it (or with a single node) there is no capacity to
        // "under-use", so no finding.
        var available = report.Context.Parallelism;
        if (available is not > 1) return;
        if (report.Projects.Count < t.SerializedBuildMinProjects) return;

        var achieved = report.AchievedParallelism;
        if (achieved <= 0) return;

        var capacityUsed = achieved / available.Value;
        if (capacityUsed >= t.SerializedBuildParallelismRatio) return;

        // Correlate with the validated critical path to split unavoidable chain depth from schedulable
        // overhead. You can never build faster than the longest dependency chain; the gap between
        // wall-clock and that chain is the only genuinely recoverable time — that is the impact bound.
        var wall = report.TotalDuration;
        var cpAccepted = report.CriticalPathValidation.Accepted && report.CriticalPathTotal > TimeSpan.Zero;
        double? recoverablePct = null;
        string chainSentence, suggestion;

        if (cpAccepted)
        {
            var cp = report.CriticalPathTotal;
            var chainPct = wall.TotalMilliseconds > 0 ? cp.TotalMilliseconds / wall.TotalMilliseconds * 100 : 0;
            recoverablePct = wall > cp ? (wall - cp).TotalMilliseconds / wall.TotalMilliseconds * 100 : 0;
            var chainDepth = report.Graph.LongestChainProjectCount;

            if (chainPct >= 80)
            {
                chainSentence = $"The blocking chain is {chainPct:F0}% of wall-clock, so the serialization is mostly the dependency chain itself ({chainDepth} projects deep) rather than schedulable overhead.";
                suggestion = $"Shorten the longest dependency chain ({chainDepth} projects): merge thin pass-through projects or break the ProjectReference that forces the ordering. `dotnet build -graph` can also improve scheduling.";
            }
            else
            {
                chainSentence = $"The blocking chain is only {chainPct:F0}% of wall-clock, yet parallelism stayed at {achieved:F1}× — up to {recoverablePct:F0}% of wall-clock is schedulable work that never overlapped.";
                suggestion = "Confirm the build ran multi-node (`-m`/`-maxcpucount`), check whether restore or a custom target serialized the graph, then compare against `dotnet build -graph`.";
            }
        }
        else
        {
            chainSentence = "The critical path could not be validated, so the split between unavoidable chain depth and schedulable overhead is unknown.";
            suggestion = "Confirm the build ran multi-node (`-m`), then run `dotnet build -graph` to expose scheduling stalls.";
        }

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Build under-parallelised: {achieved:F1}× of {available} available build node(s)",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.Medium,
            // UpperBoundImpactPercent is contractually a share of total *self time* (it drives the
            // impact ranking, which must compare like with like). The recoverable figure here is a
            // share of *wall-clock*, a different base, so it stays in Measured/Evidence only and this
            // field is left null rather than polluting the ranking and the JSON contract.
            UpperBoundImpactPercent = null,
            Measured = $"Achieved parallelism {achieved:F1}× against {available} available node(s) ({capacityUsed * 100:F0}% of capacity). {chainSentence}",
            LikelyExplanation = "Low achieved parallelism means aggregate work did not overlap in time. Some of that is unavoidable (a project cannot start before its references finish); the rest is schedulable overhead that better scheduling or fewer serial stages could recover.",
            InvestigationSuggestion = suggestion,
            Evidence = $"Achieved={achieved:F2}x, Available={available}, CapacityUsed={capacityUsed * 100:F0}%, CriticalPathAccepted={cpAccepted}",
            ThresholdName = $"achieved/available < {t.SerializedBuildParallelismRatio:F2}",
        });
    }

    private static void DetectDependencyCycles(BuildReport report, List<AnalysisFinding> findings)
    {
        if (!report.Graph.CycleDetectionRan) return;
        if (report.Graph.Cycles.Count == 0) return;

        var first = report.Graph.Cycles[0];
        // Render the cycle as a closed loop (A → B → A) so the example reads as an actual cycle.
        var loop = first.Count > 0
            ? string.Join(" → ", first) + " → " + first[0]
            : "(unavailable)";

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Dependency cycle(s) among ProjectReferences: {report.Graph.Cycles.Count}",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            Measured = $"{report.Graph.Cycles.Count} cycle(s) detected in the ProjectReference graph. Example: {loop}.",
            LikelyExplanation = null,
            InvestigationSuggestion = $"Break the cycle — projects in a reference cycle cannot build independently and defeat incremental reuse. Start with: {loop}.",
            Evidence = $"CycleCount={report.Graph.Cycles.Count}, Example={loop}",
            ThresholdName = "any ProjectReference cycle",
        });
    }

    private static void DetectProjectCountTax(BuildReport report, List<AnalysisFinding> findings, AnalyzerThresholds t)
    {
        var tax = report.ProjectCountTax;
        if (tax.TotalProjects < t.ProjectCountTaxMinProjects) return;

        var share = tax.TotalProjects > 0
            ? (double)tax.ReferencesExceedCompileCount / tax.TotalProjects * 100
            : 0;
        if (share < t.ProjectCountTaxProjectSharePercent) return;

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Reference overhead exceeds compile in {tax.ReferencesExceedCompileCount} of {tax.TotalProjects} projects",
            Severity = FindingSeverity.Info,
            Confidence = FindingConfidence.Low,
            UpperBoundImpactPercent = null,
            Measured = $"{tax.ReferencesExceedCompileCount} of {tax.TotalProjects} projects ({share:F0}%) spend more self-time resolving references than compiling code; {tax.ReferencesMajorityCount} spend the majority of their time there.",
            LikelyExplanation = "A high reference-to-compile ratio across many projects is the signature of an over-fragmented solution: each project pays fixed per-project graph, restore, and reference overhead that can exceed the code it actually compiles. This is a structural signal about solution shape, not a defect in any single project.",
            InvestigationSuggestion = "Review whether the thinnest libraries earn a separate project boundary — consolidating trivial projects removes fixed per-project overhead. Start with the projects where reference time most exceeds compile time.",
            Evidence = $"RefsExceedCompile={tax.ReferencesExceedCompileCount}/{tax.TotalProjects}, RefsMajority={tax.ReferencesMajorityCount}",
            ThresholdName = $"refs-exceed-compile share > {t.ProjectCountTaxProjectSharePercent:F0}% over >= {t.ProjectCountTaxMinProjects} projects",
        });
    }

    private static void DetectCostlyResolvePackageAssets(BuildReport report, List<AnalysisFinding> findings, AnalyzerThresholds thr)
    {
        var costly = report.TopTargets
            .Where(t => t.Name == "ResolvePackageAssets" && t.SelfTime.TotalSeconds > thr.CostlyResolvePackageAssetsSeconds)
            .OrderByDescending(t => t.SelfTime)
            .ToList();

        if (costly.Count == 0) return;
        var max = costly[0];

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"ResolvePackageAssets expensive in {costly.Count} project(s), slowest {max.ProjectName}: {Fmt(max.SelfTime)}",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            UpperBoundImpactPercent = costly.Sum(c => c.SelfPercent),
            Measured = $"Slowest: {max.ProjectName} at {Fmt(max.SelfTime)}. {costly.Count} project(s) cross the {thr.CostlyResolvePackageAssetsSeconds:F0}s threshold.",
            LikelyExplanation = null,
            InvestigationSuggestion = $"Run `dotnet nuget why {max.ProjectName} <heavy-package>` on its largest direct packages to locate transitive chains.",
            Evidence = $"Max.SelfTime={Fmt(max.SelfTime)}, Count={costly.Count}",
            ThresholdName = $"> {thr.CostlyResolvePackageAssetsSeconds:F0}s",
        });
    }

    private static void DetectTfmNegotiationOverhead(BuildReport report, List<AnalysisFinding> findings, AnalyzerThresholds thr)
    {
        // Sourced from the orchestration-task total for _GetProjectReferenceTargetFrameworkProperties
        // (LogAnalyzer.TfmNegotiationTotal) — that work is an MSBuild task and never appears in TopTasks.
        var totalMs = report.TfmNegotiationTotal.TotalMilliseconds;
        if (totalMs < thr.TfmNegotiationAggregateSeconds * 1000) return;

        var edges = report.Graph.Health.TotalEdges;

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Reference framework lookup runs {Fmt(TimeSpan.FromMilliseconds(totalMs))} across {edges} project edges",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            Measured = $"_GetProjectReferenceTargetFrameworkProperties total: {Fmt(TimeSpan.FromMilliseconds(totalMs))} across {edges} ProjectReference edges.",
            LikelyExplanation = null,
            InvestigationSuggestion = "Add `SkipGetTargetFrameworkProperties=\"true\"` to same-TFM ProjectReferences via Directory.Build.targets, or try `dotnet build -graph`.",
            Evidence = $"TfmNegotiationTotal={Fmt(TimeSpan.FromMilliseconds(totalMs))}, Edges={edges}",
            ThresholdName = $"total > {thr.TfmNegotiationAggregateSeconds:F0}s",
        });
    }

    private static void DetectBenchmarksOnCriticalPath(BuildReport report, List<AnalysisFinding> findings)
    {
        // Name-based classification is evidence, not proof — only emit when there's a specific
        // project to point at with measurable time on the blocking chain.
        var testBench = report.CriticalPath
            .Where(p => p.KindHeuristic == ProjectKind.Test || p.KindHeuristic == ProjectKind.Benchmark)
            .Where(p => p.SelfTime.TotalSeconds >= 10)
            .OrderByDescending(p => p.SelfTime)
            .ToList();
        if (testBench.Count == 0) return;

        var totalSelf = testBench.Sum(p => p.SelfTime.TotalMilliseconds);
        var totalSelfMs = report.Projects.Sum(p => p.SelfTime.TotalMilliseconds);
        var pct = totalSelfMs > 0 ? totalSelf / totalSelfMs * 100 : 0;

        var names = string.Join(", ", testBench.Select(p => $"{p.Name} ({Fmt(p.SelfTime)})"));
        var top = testBench[0];

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Test/benchmark projects on the blocking chain: {testBench.Count} · {Fmt(TimeSpan.FromMilliseconds(totalSelf))}",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            UpperBoundImpactPercent = pct,
            Measured = $"On the blocking chain: {names}. Named classification — verify before excluding.",
            LikelyExplanation = null,
            InvestigationSuggestion = $"Check whether {top.Name} needs to be in the default solution build; if it is only for test/benchmark runs, investigate excluding it from the default configuration (measure the wall-clock delta first — the DAG determines whether the leaf was actually gating anything).",
            Evidence = $"TestBenchmarkOnPath={testBench.Count}, CombinedSelfTime={Fmt(TimeSpan.FromMilliseconds(totalSelf))}",
            ThresholdName = "on blocking chain + self >= 10s",
        });
    }

    private static void DetectWarningHeavyCriticalPath(BuildReport report, List<AnalysisFinding> findings, AnalyzerThresholds thr)
    {
        var heavy = report.CriticalPath
            .Where(p => p.WarningCount > thr.WarningsOnCriticalPathPerProject)
            .OrderByDescending(p => p.WarningCount)
            .ToList();
        if (heavy.Count == 0) return;

        var top = heavy[0];
        var lines = string.Join(", ", heavy.Take(3).Select(p => $"{p.Name} ({p.WarningCount} warnings)"));
        var topPrefix = report.WarningsByPrefix.OrderByDescending(kv => kv.Value).FirstOrDefault();
        // Warnings can be counted without a recognizable code (e.g. task-emitted <Warning Text=.../>),
        // so WarningsByPrefix may be empty even past the threshold. Never emit a bare `/warnaserror:`
        // (which MSBuild reads as "promote ALL warnings to errors") or render empty "()"/"=" fragments.
        var hasPrefix = !string.IsNullOrEmpty(topPrefix.Key);

        var prefixMeasured = hasPrefix
            ? $" Most common warning prefix solution-wide: {topPrefix.Key} ({topPrefix.Value})."
            : "";
        var suggestion = hasPrefix
            ? $"Inspect warnings of type {topPrefix.Key} in {top.Name}. `dotnet build /warnaserror:{topPrefix.Key}` on a branch forces the fix."
            : $"Inspect the warnings in {top.Name}, group them by code, and promote the dominant code with `dotnet build /warnaserror:<CODE>` on a branch to force the fix.";
        var evidence = hasPrefix
            ? $"TopProject={top.Name}({top.WarningCount}), TopPrefix={topPrefix.Key}={topPrefix.Value}"
            : $"TopProject={top.Name}({top.WarningCount})";

        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Warnings concentrated on the blocking chain: {lines}",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            Measured = $"Blocking-chain projects with >{thr.WarningsOnCriticalPathPerProject} warnings each: {lines}.{prefixMeasured}",
            LikelyExplanation = null,
            InvestigationSuggestion = suggestion,
            Evidence = evidence,
            ThresholdName = $"per-project warnings > {thr.WarningsOnCriticalPathPerProject}",
        });
    }

    private static void DetectGenLoggingOutlier(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.AnalyzerReports.Count == 0) return;

        var solutionAvgMs = ComputeAverageGeneratorTime(report, KnownGenLoggingAssemblies);

        // Find per-project Gen.Logging time
        var outliers = new List<(AnalyzerReport Report, ProjectTiming? Project, TimeSpan GenTime, double Share)>();
        foreach (var ar in report.AnalyzerReports)
        {
            var genTime = ar.Generators
                .Where(g => KnownGenLoggingAssemblies.Contains(g.AssemblyName))
                .Sum(g => g.Time.TotalMilliseconds);
            if (genTime < GenLoggingOutlierMinSeconds * 1000) continue;

            var project = report.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, ar.ProjectName, StringComparison.OrdinalIgnoreCase));
            var projectSelfMs = project?.SelfTime.TotalMilliseconds ?? 0;
            if (projectSelfMs <= 0) continue;

            var share = genTime / projectSelfMs;
            if (share < GenLoggingOutlierProjectShareThreshold) continue;

            outliers.Add((ar, project, TimeSpan.FromMilliseconds(genTime), share));
        }
        if (outliers.Count == 0) return;

        var top = outliers.OrderByDescending(x => x.GenTime).First();
        var transitiveDependents = report.Graph.Nodes
            .FirstOrDefault(n => string.Equals(n.ProjectName, top.Project?.Name, StringComparison.OrdinalIgnoreCase))
            ?.TransitiveDependentsCount ?? 0;

        var projectName = top.Project?.Name ?? top.Report.ProjectName;
        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"Gen.Logging dominates {projectName}: {Fmt(top.GenTime)} ({top.Share * 100:F0}% of its time)",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            UpperBoundImpactPercent = top.Project?.SelfPercent,
            Measured = $"{projectName}: Gen.Logging {Fmt(top.GenTime)} vs project {Fmt(top.Project?.SelfTime ?? TimeSpan.Zero)}. Solution average for Gen.Logging: ~{Fmt(TimeSpan.FromMilliseconds(solutionAvgMs))}.",
            LikelyExplanation = null,
            InvestigationSuggestion = $"Inspect [LoggerMessage] usage in {projectName}. If absent, trace which reference pulls in Microsoft.Extensions.Telemetry (e.g. `dotnet nuget why {projectName} Microsoft.Extensions.Telemetry`).",
            Evidence = $"GenLoggingTime={Fmt(top.GenTime)}, ProjectSelf={Fmt(top.Project?.SelfTime ?? TimeSpan.Zero)}, Share={top.Share * 100:F0}%",
            ThresholdName = $">{GenLoggingOutlierMinSeconds:F0}s and >{GenLoggingOutlierProjectShareThreshold * 100:F0}% of project",
        });
    }

    private static void DetectComInterfaceGeneratorWithUsages(BuildReport report, List<AnalysisFinding> findings)
    {
        // Only fire when [GeneratedComInterface] is actually used. The no-op case (ran but
        // zero usages) is unavoidable SDK cost — not a finding, just trivia.
        if (report.GeneratedComInterfaceUsages.Count == 0) return;

        var totalMs = report.AnalyzerReports
            .SelectMany(r => r.Generators)
            .Where(g => KnownComInterfaceGeneratorAssemblies.Contains(g.AssemblyName))
            .Sum(g => g.Time.TotalMilliseconds);
        if (totalMs < ComInterfaceGeneratorMinSecondsForFinding * 1000) return;

        var usages = string.Join(", ", report.GeneratedComInterfaceUsages.Take(5))
                     + (report.GeneratedComInterfaceUsages.Count > 5 ? ", …" : "");
        findings.Add(new AnalysisFinding
        {
            Number = 0,
            Title = $"ComInterfaceGenerator runs {Fmt(TimeSpan.FromMilliseconds(totalMs))} total across {report.GeneratedComInterfaceUsages.Count} project(s) with [GeneratedComInterface]",
            Severity = FindingSeverity.Warning,
            Confidence = FindingConfidence.High,
            Measured = $"Projects using [GeneratedComInterface]: {usages}. Total summed generator time: {Fmt(TimeSpan.FromMilliseconds(totalMs))}.",
            LikelyExplanation = null,
            InvestigationSuggestion = $"Verify usages are intentional in: {usages}. Emitted code can be inspected with -p:EmitCompilerGeneratedFiles=true.",
            Evidence = $"ComInterfaceGenTotal={Fmt(TimeSpan.FromMilliseconds(totalMs))}, AttributeUsages={report.GeneratedComInterfaceUsages.Count}",
            ThresholdName = $"{nameof(ComInterfaceGeneratorMinSecondsForFinding)}={ComInterfaceGeneratorMinSecondsForFinding:F0}s",
        });
    }

    private static void DetectCSharpAnalyzersInNonRoslynProject(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.AnalyzerReports.Count == 0) return;

        var totalAnalyzerMs = report.AnalyzerReports.Sum(r => r.TotalAnalyzerTime.TotalMilliseconds);

        foreach (var ar in report.AnalyzerReports)
        {
            var csTime = ar.Analyzers
                .Where(a => string.Equals(a.AssemblyName, CSharpAnalyzersAssembly, StringComparison.OrdinalIgnoreCase))
                .Sum(a => a.Time.TotalMilliseconds);
            if (csTime < CSharpAnalyzersInNonRoslynMinSeconds * 1000) continue;

            var pct = totalAnalyzerMs > 0 ? csTime / totalAnalyzerMs * 100 : 0;
            findings.Add(new AnalysisFinding
            {
                Number = 0,
                Title = $"Roslyn compiler analyzer running in {ar.ProjectName}: {Fmt(TimeSpan.FromMilliseconds(csTime))}",
                Severity = FindingSeverity.Warning,
                Confidence = FindingConfidence.Medium,
                Measured = $"{ar.ProjectName}: Microsoft.CodeAnalysis.CSharp.Analyzers runs for {Fmt(TimeSpan.FromMilliseconds(csTime))} ({pct:F1}% of solution analyzer time).",
                LikelyExplanation = "This analyzer ships with the Roslyn SDK and is expected in projects that build Roslyn analyzers/source generators. In an ordinary application project it usually arrives transitively and adds compile cost for no benefit — but this finding cannot tell the two apart from timing alone.",
                InvestigationSuggestion = $"First confirm {ar.ProjectName} is not itself a Roslyn analyzer/source-generator project. If it is application code, run `dotnet nuget why {ar.ProjectName} Microsoft.CodeAnalysis.CSharp.Analyzers` to find what pulls it in.",
                Evidence = $"CSharpAnalyzersTime={Fmt(TimeSpan.FromMilliseconds(csTime))}, ShareOfAnalyzerTime={pct:F1}%",
                ThresholdName = $"> {CSharpAnalyzersInNonRoslynMinSeconds:F0}s",
            });
        }
    }

    private static void DetectHeavyTransitivesInTestOrBenchmark(BuildReport report, List<AnalysisFinding> findings)
    {
        if (report.ProjectDiagnoses.Count == 0) return;

        foreach (var d in report.ProjectDiagnoses)
        {
            var project = report.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, d.ProjectName, StringComparison.OrdinalIgnoreCase));
            if (project is null) continue;
            if (project.KindHeuristic is not (ProjectKind.Test or ProjectKind.Benchmark)) continue;
            if (d.Packages is null) continue;

            var heavyDirect = d.Packages.DirectPackages.Where(p => p.IsKnownHeavy).ToList();
            var heavyTransitive = d.Packages.TransitivePackages.Where(p => p.IsKnownHeavy).ToList();
            var heavyAll = heavyDirect.Concat(heavyTransitive).Distinct().ToList();
            if (heavyAll.Count == 0) continue;

            var heavyList = string.Join(", ", heavyAll.Select(p => p.Version is null ? p.Id : $"{p.Id} {p.Version}"));
            var heavyTransitiveByParent = heavyTransitive
                .Where(p => p.ParentPackage is not null)
                .GroupBy(p => p.ParentPackage!)
                .Select(g => $"{g.Key} → {string.Join(", ", g.Select(p => p.Id))}");
            var introducedBy = heavyTransitiveByParent.Any()
                ? $" Introduced via: {string.Join("; ", heavyTransitiveByParent)}."
                : "";

            findings.Add(new AnalysisFinding
            {
                Number = 0,
                Title = $"{d.ProjectName} pulls in heavy production packages: {heavyAll.Count}",
                Severity = FindingSeverity.Warning,
                Confidence = FindingConfidence.High,
                Measured = $"{d.ProjectName} ({(project.KindHeuristic == ProjectKind.Test ? "test" : "benchmark")}): heavy package(s) {heavyList}.{introducedBy}",
                LikelyExplanation = null,
                InvestigationSuggestion = $"Inspect the direct ProjectReferences of {d.ProjectName} to see whether the heavy package(s) are actually needed for its test/benchmark runs.",
                Evidence = $"HeavyPackages={heavyAll.Count}, TransitiveTotal={d.Packages.TransitivePackages.Count}",
                ThresholdName = "test/benchmark + heavy package present",
            });
        }
    }

    private static double ComputeAverageGeneratorTime(BuildReport report, HashSet<string> assemblies)
    {
        var times = report.AnalyzerReports
            .SelectMany(r => r.Generators)
            .Where(g => assemblies.Contains(g.AssemblyName))
            .Select(g => g.Time.TotalMilliseconds)
            .ToList();
        return times.Count == 0 ? 0 : times.Average();
    }

    private static string Fmt(TimeSpan ts) => ConsoleReportRenderer.FormatDuration(ts);

    private static string CategoryLabel(TargetCategory category) =>
        ConsoleReportRenderer.CategoryLabel(category);
}
