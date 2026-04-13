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

    /// <summary>
    /// Per-code warning tallies (e.g. CS8600 → 42). Built from BuildWarningEventArgs.Code
    /// captured in the binlog — does not require the text build log. Empty when no warnings
    /// carried a recognizable code.
    /// </summary>
    public required IReadOnlyList<WarningCodeTally> WarningsByCode { get; init; }

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

    // Task-level timing (all tasks, not just orchestration)
    public required IReadOnlyList<TaskTiming> TopTasks { get; init; }

    // Incremental build signal — per-target skip reasons
    public required IReadOnlyList<TargetSkipInfo> SkipReasons { get; init; }

    // Analyzer / generator timing (from -p:ReportAnalyzer=true output in binlog)
    public required IReadOnlyList<AnalyzerReport> AnalyzerReports { get; init; }

    // "Why is this slow?" synthesis per top project
    public required IReadOnlyList<ProjectDiagnosis> ProjectDiagnoses { get; init; }

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
    /// <summary>
    /// How btanalyzer invoked the build. "full (--no-incremental)" for reproducible measurements,
    /// "incremental" when the user opted in to measure the dev inner loop.
    /// Null if the report was generated without BuildCommand (e.g. direct LogAnalyzer use).
    /// </summary>
    public string? BuildMode { get; init; }
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

/// <summary>Per-task timing within a target. Enables drill-down below the target level.</summary>
public sealed record TaskTiming
{
    public required string TaskName { get; init; }
    public required string TargetName { get; init; }
    public required string ProjectName { get; init; }
    public required TimeSpan SelfTime { get; init; }
    public double SelfPercent { get; init; }
}

/// <summary>
/// Why a target was skipped. Captures MSBuild's TargetSkipReason for incremental build diagnosis.
/// </summary>
public sealed record TargetSkipInfo
{
    public required string TargetName { get; init; }
    public required string ProjectName { get; init; }
    public required string SkipReason { get; init; }
    public string? Condition { get; init; }
    public string? EvaluatedCondition { get; init; }
}

/// <summary>Aggregated skip-reason counts for the summary.</summary>
public sealed record SkipReasonSummary
{
    public required string Reason { get; init; }
    public required int Count { get; init; }
}

/// <summary>
/// Per-project analyzer/generator timing extracted from ReportAnalyzer output in the binlog.
/// Populated because we always build with -p:ReportAnalyzer=true.
/// </summary>
public sealed record AnalyzerReport
{
    public required string ProjectName { get; init; }
    public required TimeSpan TotalAnalyzerTime { get; init; }
    public required TimeSpan TotalGeneratorTime { get; init; }
    public required TimeSpan CscWallTime { get; init; }
    public required IReadOnlyList<AnalyzerEntry> Analyzers { get; init; }
    public required IReadOnlyList<AnalyzerEntry> Generators { get; init; }
}

/// <summary>
/// Tally of a single warning code (e.g. "CS8600") with its prefix category.
/// Prefix groupings: CS (C# compiler), CA (Roslyn analyzers), IDE, NETSDK, NU (NuGet),
/// MSB (MSBuild). The prefix is extracted by scanning leading letters from the code.
/// </summary>
public sealed record WarningCodeTally
{
    public required string Code { get; init; }
    public required string Prefix { get; init; }
    public required int Count { get; init; }
}

public sealed record AnalyzerEntry
{
    public required string AssemblyName { get; init; }
    public required TimeSpan Time { get; init; }
    public required double Percent { get; init; }
}

/// <summary>
/// "Why is this slow?" synthesis for a single project. Pulls together task breakdown,
/// category composition, analyzer/generator cost, graph position, and span/self pattern
/// into one compact explanation.
/// </summary>
/// <summary>
/// Quality of package/reference data attached to a project. Reflects what the resolver
/// actually had access to on disk — the reader can judge the report accordingly.
/// </summary>
public enum ProjectDataQuality
{
    /// <summary>Both .csproj and obj/project.assets.json were parsed — direct + transitive data.</summary>
    Full,
    /// <summary>Only .csproj parsed — direct packages only, no transitive graph.</summary>
    CsprojOnly,
    /// <summary>No project data available. Nothing to display.</summary>
    NoCsproj,
}

public enum PackageReferenceSource { Direct, Transitive, ProjectReference }

public sealed record PackageRef
{
    public required string Id { get; init; }
    public string? Version { get; init; }
    public required PackageReferenceSource Source { get; init; }
    /// <summary>For transitive packages, the direct-reference package that pulled this in (one-level walk).</summary>
    public string? ParentPackage { get; init; }
    public bool IsKnownHeavy { get; init; }
}

public sealed record ProjectPackages
{
    public required ProjectDataQuality Quality { get; init; }
    public required IReadOnlyList<PackageRef> DirectPackages { get; init; }
    public required IReadOnlyList<PackageRef> TransitivePackages { get; init; }
    public required IReadOnlyList<string> ProjectReferences { get; init; }
}

public sealed record ProjectDiagnosis
{
    public required string ProjectName { get; init; }
    public required TimeSpan SelfTime { get; init; }
    public required double SelfPercent { get; init; }
    public required string TopCategory { get; init; }
    public required double TopCategoryPercent { get; init; }
    public required string TopTask { get; init; }
    public required TimeSpan TopTaskTime { get; init; }
    public TimeSpan? AnalyzerTime { get; init; }
    public TimeSpan? GeneratorTime { get; init; }
    public bool OnCriticalPath { get; init; }
    public bool IsSpanOutlier { get; init; }
    /// <summary>Factual one-paragraph synthesis. No interpretation beyond measured data.</summary>
    public required string Summary { get; init; }
    /// <summary>Package and project references resolved from .csproj / project.assets.json. Null when no data was available.</summary>
    public ProjectPackages? Packages { get; init; }
}
