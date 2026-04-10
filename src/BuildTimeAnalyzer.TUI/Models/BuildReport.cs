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
    public required IReadOnlyList<ProjectTiming> Projects { get; init; }
    public required IReadOnlyList<TargetTiming> TopTargets { get; init; }

    // Phase 2: build context & categorization
    public required BuildContext Context { get; init; }
    public required IReadOnlyDictionary<TargetCategory, TimeSpan> CategoryTotals { get; init; }
    public required int ExecutedTargetCount { get; init; }
    public required int SkippedTargetCount { get; init; }
    public required IReadOnlyList<TargetTiming> PotentiallyCustomTargets { get; init; }

    // Phase 3: critical path (empty if not computed or failed validation)
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
    /// <summary>C# / VB compilation (CoreCompile, Compile).</summary>
    Compile,
    /// <summary>Source generators (RunResolveSourceGenerators, etc.).</summary>
    SourceGen,
    /// <summary>Static web assets pipeline (Blazor, Razor).</summary>
    StaticWebAssets,
    /// <summary>File copies and output directory work.</summary>
    Copy,
    /// <summary>NuGet restore and package resolution.</summary>
    Restore,
    /// <summary>Reference resolution and framework handling.</summary>
    References,
    /// <summary>Did not match any known SDK target pattern. May be user-defined, third-party, or an uncategorised SDK target.</summary>
    Uncategorized,
    /// <summary>Internal MSBuild-prefixed targets (underscore prefix).</summary>
    Other,
}

/// <summary>
/// Build run metadata captured from the binlog. Fields are nullable — a null field means the
/// value was not reliably captured from this binlog and must not be displayed.
/// </summary>
public sealed record BuildContext
{
    public string? Configuration { get; init; }
    public string? SdkVersion { get; init; }
    public string? MSBuildVersion { get; init; }
    public string? OperatingSystem { get; init; }
    public int? Parallelism { get; init; }
    public bool? RestoreObserved { get; init; }
}
