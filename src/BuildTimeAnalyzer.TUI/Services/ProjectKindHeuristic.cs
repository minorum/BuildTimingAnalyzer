using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Name-based classification of a project into <see cref="ProjectKind"/>.
/// This is explicitly heuristic. The report must never present this as authoritative — it is
/// only useful as an additional lens ("this path runs through projects that look like tests").
/// </summary>
public static class ProjectKindHeuristic
{
    public static ProjectKind Classify(string projectName)
    {
        if (string.IsNullOrEmpty(projectName)) return ProjectKind.Other;

        // Test detection: name ends in "Tests" or "Test"
        if (projectName.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) ||
            projectName.EndsWith("Test", StringComparison.OrdinalIgnoreCase) ||
            projectName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectKind.Test;
        }

        // Benchmark detection: name contains "Benchmark" or ends in "Bench"
        if (projectName.Contains("Benchmark", StringComparison.OrdinalIgnoreCase) ||
            projectName.EndsWith("Bench", StringComparison.OrdinalIgnoreCase) ||
            projectName.EndsWith(".Bench", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectKind.Benchmark;
        }

        return ProjectKind.Other;
    }

    public static string Label(ProjectKind kind) => kind switch
    {
        ProjectKind.Test => "test (heuristic)",
        ProjectKind.Benchmark => "benchmark (heuristic)",
        ProjectKind.Other => "other",
        _ => "other",
    };
}
