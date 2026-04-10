namespace BuildTimeAnalyzer.Models;

public sealed record BuildReport
{
    public required string ProjectOrSolutionPath { get; init; }
    public required DateTime StartTime { get; init; }
    public required DateTime EndTime { get; init; }
    public TimeSpan TotalDuration => EndTime - StartTime;
    public required bool Succeeded { get; init; }
    public required int ErrorCount { get; init; }
    public required int WarningCount { get; init; }
    public required int AttributedWarningCount { get; init; }
    public int UnattributedWarningCount => WarningCount - AttributedWarningCount;

    public required IReadOnlyList<ProjectTiming> Projects { get; init; }
    public required IReadOnlyList<TargetTiming> TopTargets { get; init; }

    // Build context & incremental behavior
    public required BuildContext Context { get; init; }
    public required int ExecutedTargetCount { get; init; }
    public required int SkippedTargetCount { get; init; }

    // Categorization
    public required IReadOnlyDictionary<TargetCategory, TimeSpan> CategoryTotals { get; init; }
    public required IReadOnlyList<TargetTiming> PotentiallyCustomTargets { get; init; }

    // Reference overhead
    public required ReferenceOverheadStats? ReferenceOverhead { get; init; }

    // Span-vs-self outliers (projects waiting on the graph)
    public required IReadOnlyList<ProjectTiming> SpanOutliers { get; init; }

    // Project Count Tax indicators — how fragmented the solution is
    public required ProjectCountTaxStats ProjectCountTax { get; init; }

    // Dependency graph
    public required DependencyGraph Graph { get; init; }

    // Critical path — only populated if validation passed AND the graph is usable
    public required IReadOnlyList<ProjectTiming> CriticalPath { get; init; }
    public required TimeSpan CriticalPathTotal { get; init; }
    public required CriticalPathValidation CriticalPathValidation { get; init; }
}

public sealed record ProjectTiming
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }

    /// <summary>Sum of exclusive target durations — genuinely exclusive work for this project.</summary>
    public required TimeSpan SelfTime { get; init; }

    /// <summary>Wall-clock span from first target start to last target end. Display metric only — never used as a cost input.</summary>
    public TimeSpan Span => EndOffset - StartOffset;

    public required bool Succeeded { get; init; }
    public required int ErrorCount { get; init; }
    public required int WarningCount { get; init; }

    /// <summary>Share of total self time (0-100). Labelled "% Self" in the UI.</summary>
    public double SelfPercent { get; init; }

    public required TimeSpan StartOffset { get; init; }
    public required TimeSpan EndOffset { get; init; }

    /// <summary>Targets belonging to this project (ordered by SelfTime desc). Populated for drill-down candidates only.</summary>
    public IReadOnlyList<TargetTiming> Targets { get; init; } = [];

    /// <summary>Per-category self time sums. Populated for drill-down candidates only.</summary>
    public IReadOnlyDictionary<TargetCategory, TimeSpan> CategoryBreakdown { get; init; } =
        new Dictionary<TargetCategory, TimeSpan>();

    /// <summary>Name-based heuristic classification. Never authoritative.</summary>
    public ProjectKind KindHeuristic { get; init; } = ProjectKind.Other;
}

public sealed record TargetTiming
{
    public required string Name { get; init; }
    public required string ProjectName { get; init; }

    /// <summary>Exclusive target time (raw duration minus MSBuild/CallTarget orchestration time).</summary>
    public required TimeSpan SelfTime { get; init; }

    /// <summary>Share of total self time (0-100).</summary>
    public double SelfPercent { get; init; }

    public required TargetCategory Category { get; init; }
}

/// <summary>
/// Target categories derived from deterministic pattern-matching against a fixed SDK target list.
/// Categorization is a grouping hint, not an authoritative claim about a target's nature.
/// </summary>
public enum TargetCategory
{
    Compile,
    SourceGen,
    StaticWebAssets,
    Copy,
    Restore,
    References,
    Uncategorized,
    Other,
}

/// <summary>
/// Name-based heuristic classification of a project. Must always be presented to the user as
/// "heuristic" — the report never treats this as an authoritative claim about the project's role.
/// </summary>
public enum ProjectKind
{
    /// <summary>Name matches a test-project pattern (e.g. ends in "Tests" or "Test").</summary>
    Test,
    /// <summary>Name matches a benchmark-project pattern (e.g. contains "Benchmark").</summary>
    Benchmark,
    /// <summary>Does not match any known pattern. No claim about the project's role.</summary>
    Other,
}

public sealed record BuildContext
{
    public string? Configuration { get; init; }
    public string? SdkVersion { get; init; }
    public string? MSBuildVersion { get; init; }
    public string? OperatingSystem { get; init; }
    public int? Parallelism { get; init; }
    public bool? RestoreObserved { get; init; }
}

public sealed record ReferenceOverheadStats
{
    public required TimeSpan TotalSelfTime { get; init; }
    public required double SelfPercent { get; init; }
    public required int PayingProjectsCount { get; init; }
    public required int TotalProjectsCount { get; init; }
    public required TimeSpan MedianPerPayingProject { get; init; }
    public required IReadOnlyList<ReferenceOverheadProject> TopProjects { get; init; }
    public double PayingProjectsPercent =>
        TotalProjectsCount == 0 ? 0 : (double)PayingProjectsCount / TotalProjectsCount * 100;
}

public sealed record ReferenceOverheadProject
{
    public required string ProjectName { get; init; }
    public required TimeSpan SelfTime { get; init; }
}

/// <summary>
/// Indicators of "project count tax" — whether the solution pays graph/orchestration cost
/// disproportionate to the local work it produces.
/// </summary>
public sealed record ProjectCountTaxStats
{
    /// <summary>Count of projects where reference-category self time &gt; compile-category self time.</summary>
    public required int ReferencesExceedCompileCount { get; init; }

    /// <summary>Count of projects where reference-category self time is the majority (&gt; 50%) of the project's self time.</summary>
    public required int ReferencesMajorityCount { get; init; }

    /// <summary>Count of projects matching the span outlier rule (tiny self, huge span).</summary>
    public required int TinySelfHugeSpanCount { get; init; }

    public required int TotalProjects { get; init; }

    /// <summary>Heuristic per-kind stats. Kind classification is name-based and not authoritative.</summary>
    public required IReadOnlyList<ProjectKindStats> PerKindStats { get; init; }
}

public sealed record ProjectKindStats
{
    public required ProjectKind Kind { get; init; }
    public required int Count { get; init; }
    public required TimeSpan MedianSelfTime { get; init; }
    public required TimeSpan MedianSpan { get; init; }
    /// <summary>Median of Span/SelfTime across projects of this kind. 0 if any member has zero SelfTime.</summary>
    public required double MedianSpanToSelfRatio { get; init; }
}

/// <summary>
/// Project dependency graph extracted from ProjectReference items on ProjectStartedEventArgs.Items.
/// </summary>
public sealed record DependencyGraph
{
    public required DependencyGraphHealth Health { get; init; }
    public required IReadOnlyList<ProjectGraphNode> Nodes { get; init; }
    public required IReadOnlyList<ProjectGraphNode> TopHubs { get; init; }
    public required int LongestChainProjectCount { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Cycles { get; init; }

    /// <summary>True when cycle detection has been attempted. Use instead of inferring from Cycles.Count.</summary>
    public required bool CycleDetectionRan { get; init; }

    /// <summary>True if the graph has enough structure to trust derived analyses like critical path.</summary>
    public required bool IsUsable { get; init; }
}

public sealed record DependencyGraphHealth
{
    public required int TotalProjects { get; init; }
    public required int TotalEdges { get; init; }
    public required int IsolatedNodes { get; init; }
    public required int NodesWithOutgoing { get; init; }
    public required int NodesWithIncoming { get; init; }
}

public sealed record ProjectGraphNode
{
    public required string ProjectName { get; init; }
    public required string FullPath { get; init; }
    public required int OutgoingCount { get; init; }
    public required int IncomingCount { get; init; }
    /// <summary>Transitive dependents = all projects that reach this node via the dependency DAG.</summary>
    public required int TransitiveDependentsCount { get; init; }
    /// <summary>Transitive dependencies = all projects this node reaches via the dependency DAG.</summary>
    public required int TransitiveDependenciesCount { get; init; }
}

/// <summary>
/// Validation record for the critical path computation. Always populated so the report can
/// display an explicit accept/reject status with the underlying numbers that justified it.
/// </summary>
public sealed record CriticalPathValidation
{
    public required TimeSpan ComputedTotal { get; init; }
    public required TimeSpan WallClock { get; init; }
    public required bool Accepted { get; init; }
    public required string Reason { get; init; }
    public required bool GraphWasUsable { get; init; }
}
