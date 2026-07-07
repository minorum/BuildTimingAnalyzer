namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Tunable policy thresholds for <see cref="BuildAnalyzer"/>. Defaults reproduce the historical
/// hard-coded constants; a <c>btanalyzer.json</c> config file may override any of them so a team
/// can adapt the findings to their own build without recompiling. Only the "policy" numbers a user
/// would reasonably tune are surfaced here — generator-specific constants stay internal.
/// </summary>
public sealed record AnalyzerThresholds
{
    /// <summary>Top project self-share (%) at or below which no dominance finding fires.</summary>
    public double LargestShareWarningPercent { get; init; } = 15.0;

    /// <summary>Top project self-share (%) above which the dominance finding is Critical (else Warning).</summary>
    public double LargestShareCriticalPercent { get; init; } = 25.0;

    /// <summary>ResolvePackageAssets self time (seconds) above which it is flagged as costly.</summary>
    public double CostlyResolvePackageAssetsSeconds { get; init; } = 3.0;

    /// <summary>Aggregate reference-TFM-negotiation time (seconds) above which it is flagged.</summary>
    public double TfmNegotiationAggregateSeconds { get; init; } = 120.0;

    /// <summary>Per-project warning count on the blocking chain above which it is flagged.</summary>
    public int WarningsOnCriticalPathPerProject { get; init; } = 50;

    /// <summary>
    /// Fraction of available build nodes the achieved parallelism must fall below before the build
    /// is flagged as under-parallelised (e.g. 0.5 → using less than half the available capacity).
    /// </summary>
    public double SerializedBuildParallelismRatio { get; init; } = 0.5;

    /// <summary>Minimum project count before the serialized-build finding can fire (tiny solutions are noise).</summary>
    public int SerializedBuildMinProjects { get; init; } = 5;

    /// <summary>Minimum project count before the project-count-tax finding can fire.</summary>
    public int ProjectCountTaxMinProjects { get; init; } = 10;

    /// <summary>
    /// Share (%) of projects spending more time on references than compile above which the
    /// project-count-tax finding fires.
    /// </summary>
    public double ProjectCountTaxProjectSharePercent { get; init; } = 40.0;

    public static AnalyzerThresholds Default { get; } = new();
}
