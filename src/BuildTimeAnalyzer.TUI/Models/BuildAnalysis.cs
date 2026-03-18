namespace BuildTimeAnalyzer.Models;

public enum FindingSeverity { Info, Warning, Critical }

public sealed record AnalysisFinding
{
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required FindingSeverity Severity { get; init; }
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
