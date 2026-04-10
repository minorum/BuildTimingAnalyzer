namespace BuildTimeAnalyzer.Models;

public enum FindingSeverity { Info, Warning, Critical }

/// <summary>
/// A finding carries three distinct layers that must not be conflated:
///   • <see cref="Measured"/>          — the raw counted facts only
///   • <see cref="LikelyExplanation"/> — a heuristic hypothesis, clearly tagged as such; may be null
///   • <see cref="InvestigationSuggestion"/> — a concrete next step, phrased as an investigation
/// <para>
/// Title and <see cref="Measured"/> must stay purely factual. Any interpretation belongs in
/// <see cref="LikelyExplanation"/>. Recommendations must never appear in Measured.
/// </para>
/// </summary>
public sealed record AnalysisFinding
{
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required FindingSeverity Severity { get; init; }

    /// <summary>Counted facts. Must be purely factual — no interpretation.</summary>
    public required string Measured { get; init; }

    /// <summary>Heuristic hypothesis. Null when the finding is purely measurement.</summary>
    public string? LikelyExplanation { get; init; }

    /// <summary>Concrete next step framed as an investigation.</summary>
    public required string InvestigationSuggestion { get; init; }

    /// <summary>Raw metric readings that triggered the finding.</summary>
    public required string Evidence { get; init; }

    /// <summary>The named threshold constant that was crossed.</summary>
    public required string ThresholdName { get; init; }
}

public sealed record AnalysisRecommendation
{
    public required int Number { get; init; }
    public required string Text { get; init; }
}

public sealed record BuildAnalysis
{
    public required IReadOnlyList<AnalysisFinding> Findings { get; init; }
    public required IReadOnlyList<AnalysisRecommendation> Recommendations { get; init; }
}
