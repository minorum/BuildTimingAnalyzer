using System.Text.Json;
using System.Text.Json.Serialization;
using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// A compact, comparable summary of a build. Produced from a live <see cref="BuildReport"/> or read
/// back from a previously exported JSON report, so runs can be diffed for regression tracking.
/// </summary>
public sealed record BuildSnapshot
{
    public string? Project { get; init; }
    public long WallClockMs { get; init; }
    public long TotalSelfTimeMs { get; init; }
    public int WarningCount { get; init; }
    public int ErrorCount { get; init; }
    public double AchievedParallelism { get; init; }
    public bool Succeeded { get; init; }
    public IReadOnlyList<SnapshotProject> Projects { get; init; } = [];

    public static BuildSnapshot FromReport(BuildReport report) => new()
    {
        Project = report.ProjectOrSolutionPath,
        WallClockMs = (long)report.TotalDuration.TotalMilliseconds,
        TotalSelfTimeMs = (long)report.TotalSelfTime.TotalMilliseconds,
        WarningCount = report.WarningCount,
        ErrorCount = report.ErrorCount,
        AchievedParallelism = Math.Round(report.AchievedParallelism, 2),
        Succeeded = report.Succeeded,
        Projects = report.Projects
            .Select(p => new SnapshotProject { Name = p.Name, SelfTimeMs = (long)p.SelfTime.TotalMilliseconds })
            .ToList(),
    };

    /// <summary>Read a snapshot from a JSON report previously written by <see cref="Export.JsonReportExporter"/>. Null on any error.</summary>
    public static BuildSnapshot? ReadFromJsonReport(string path)
    {
        try
        {
            var dto = JsonSerializer.Deserialize(File.ReadAllText(path), ComparisonJsonContext.Default.SnapshotDto);
            if (dto is null) return null;
            return new BuildSnapshot
            {
                Project = dto.Project,
                WallClockMs = dto.WallClockMs,
                TotalSelfTimeMs = dto.TotalSelfTimeMs,
                WarningCount = dto.WarningCount,
                ErrorCount = dto.ErrorCount,
                AchievedParallelism = dto.AchievedParallelism,
                Succeeded = dto.Succeeded,
                Projects = (dto.Projects ?? [])
                    .Where(p => !string.IsNullOrEmpty(p.Name))
                    .Select(p => new SnapshotProject { Name = p.Name!, SelfTimeMs = p.SelfTimeMs })
                    .ToList(),
            };
        }
        catch
        {
            return null;
        }
    }
}

public sealed record SnapshotProject
{
    public required string Name { get; init; }
    public long SelfTimeMs { get; init; }
}

/// <summary>A per-project delta between two snapshots (positive = slower now).</summary>
public sealed record ProjectDelta
{
    public required string Name { get; init; }
    public required long BaselineMs { get; init; }
    public required long CurrentMs { get; init; }
    public long DeltaMs => CurrentMs - BaselineMs;
    public double DeltaPercent => BaselineMs > 0 ? (double)(CurrentMs - BaselineMs) / BaselineMs * 100 : 0;
}

public sealed record BuildComparisonResult
{
    public required long WallClockDeltaMs { get; init; }
    public required double WallClockDeltaPercent { get; init; }
    public required long TotalSelfTimeDeltaMs { get; init; }
    public required double TotalSelfTimeDeltaPercent { get; init; }
    public required int WarningDelta { get; init; }
    public required int ErrorDelta { get; init; }

    /// <summary>Projects that got slower, largest regression first.</summary>
    public required IReadOnlyList<ProjectDelta> Regressions { get; init; }
    /// <summary>Projects that got faster, largest improvement first.</summary>
    public required IReadOnlyList<ProjectDelta> Improvements { get; init; }
    /// <summary>Project names present now but not in the baseline.</summary>
    public required IReadOnlyList<string> AddedProjects { get; init; }
    /// <summary>Project names present in the baseline but not now.</summary>
    public required IReadOnlyList<string> RemovedProjects { get; init; }

    /// <summary>The worst per-metric regression percentage (wall-clock vs total self time), for --fail-on.</summary>
    public double WorstRegressionPercent =>
        Math.Max(WallClockDeltaPercent, TotalSelfTimeDeltaPercent);
}

public static class BuildComparison
{
    public static BuildComparisonResult Compare(BuildSnapshot baseline, BuildSnapshot current, int topN = 10)
    {
        var baseByName = baseline.Projects
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().SelfTimeMs, StringComparer.OrdinalIgnoreCase);
        var curByName = current.Projects
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().SelfTimeMs, StringComparer.OrdinalIgnoreCase);

        var deltas = new List<ProjectDelta>();
        foreach (var kv in curByName)
        {
            if (!baseByName.TryGetValue(kv.Key, out var baseMs)) continue;
            deltas.Add(new ProjectDelta { Name = kv.Key, BaselineMs = baseMs, CurrentMs = kv.Value });
        }

        var regressions = deltas.Where(d => d.DeltaMs > 0)
            .OrderByDescending(d => d.DeltaMs).Take(topN).ToList();
        var improvements = deltas.Where(d => d.DeltaMs < 0)
            .OrderBy(d => d.DeltaMs).Take(topN).ToList();
        var added = curByName.Keys.Where(k => !baseByName.ContainsKey(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = baseByName.Keys.Where(k => !curByName.ContainsKey(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        return new BuildComparisonResult
        {
            WallClockDeltaMs = current.WallClockMs - baseline.WallClockMs,
            WallClockDeltaPercent = Pct(baseline.WallClockMs, current.WallClockMs),
            TotalSelfTimeDeltaMs = current.TotalSelfTimeMs - baseline.TotalSelfTimeMs,
            TotalSelfTimeDeltaPercent = Pct(baseline.TotalSelfTimeMs, current.TotalSelfTimeMs),
            WarningDelta = current.WarningCount - baseline.WarningCount,
            ErrorDelta = current.ErrorCount - baseline.ErrorCount,
            Regressions = regressions,
            Improvements = improvements,
            AddedProjects = added,
            RemovedProjects = removed,
        };
    }

    private static double Pct(long baseline, long current) =>
        baseline > 0 ? Math.Round((double)(current - baseline) / baseline * 100, 1) : 0;
}

internal sealed class SnapshotDto
{
    public string? Project { get; init; }
    public long WallClockMs { get; init; }
    public long TotalSelfTimeMs { get; init; }
    public int WarningCount { get; init; }
    public int ErrorCount { get; init; }
    public double AchievedParallelism { get; init; }
    public bool Succeeded { get; init; }
    public List<SnapshotProjectDto>? Projects { get; init; }
}

internal sealed class SnapshotProjectDto
{
    public string? Name { get; init; }
    public long SelfTimeMs { get; init; }
}

[JsonSerializable(typeof(SnapshotDto))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
internal sealed partial class ComparisonJsonContext : JsonSerializerContext;
