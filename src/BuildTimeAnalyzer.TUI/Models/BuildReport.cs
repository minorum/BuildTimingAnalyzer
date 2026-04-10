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

    // Dependency graph (extracted from ProjectReference items)
    public required DependencyGraph Graph { get; init; }

    // Critical path — only populated if CPM validation passed AND the graph is usable.
    // Empty list + zero total means: do not render critical path anywhere in the report.
    public required IReadOnlyList<ProjectTiming> CriticalPath { get; init; }
    public required TimeSpan CriticalPathTotal { get; init; }
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

public sealed record BuildContext
{
    public string? Configuration { get; init; }
    public string? SdkVersion { get; init; }
    public string? MSBuildVersion { get; init; }
    public string? OperatingSystem { get; init; }
    public int? Parallelism { get; init; }
    public bool? RestoreObserved { get; init; }
}

/// <summary>
/// Aggregate reference-related cost across the whole solution. Null if the solution has no
/// reference-category work or only a single project.
/// </summary>
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
/// Project dependency graph extracted from ProjectReference items on ProjectStartedEventArgs.Items.
/// </summary>
public sealed record DependencyGraph
{
    public required DependencyGraphHealth Health { get; init; }
    public required IReadOnlyList<ProjectGraphNode> Nodes { get; init; }
    public required IReadOnlyList<ProjectGraphNode> TopHubs { get; init; }
    public required int LongestChainProjectCount { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Cycles { get; init; }

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
}
