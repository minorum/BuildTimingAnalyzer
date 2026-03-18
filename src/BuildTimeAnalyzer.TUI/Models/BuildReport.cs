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
}

public sealed record ProjectTiming
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required TimeSpan Duration { get; init; }
    public required bool Succeeded { get; init; }
    public required int ErrorCount { get; init; }
    public required int WarningCount { get; init; }
    public double Percentage { get; set; }
}

public sealed record TargetTiming
{
    public required string Name { get; init; }
    public required string ProjectName { get; init; }
    public required TimeSpan Duration { get; init; }
    public double Percentage { get; set; }
}
