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
            .Select(p => new SnapshotProject
            {
                Name = p.Name,
                SelfTimeMs = (long)p.SelfTime.TotalMilliseconds,
                FullPath = p.FullPath,
            })
            .ToList(),
    };

    /// <summary>Read a snapshot from a JSON report previously written by <see cref="Export.JsonReportExporter"/>. Null on any error.</summary>
    public static BuildSnapshot? ReadFromJsonReport(string path)
    {
        try { return FromJson(File.ReadAllText(path)); }
        catch { return null; }
    }

    /// <summary>
    /// Read a baseline JSON report straight out of git via <c>git show &lt;revspec&gt;</c>
    /// (e.g. <c>origin/main:baseline.json</c>), so CI can compare against a committed baseline without
    /// juggling artifacts. Null if git is unavailable or the object doesn't exist.
    /// </summary>
    public static BuildSnapshot? ReadFromGit(string revSpec)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("show");
            psi.ArgumentList.Add(revSpec);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return null;
            // Drain BOTH streams concurrently before waiting: a chatty `git show` on stderr could
            // otherwise fill the pipe buffer and deadlock the process (and this method) forever.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            Task.WaitAll(stdout, stderr);
            proc.WaitForExit();
            return proc.ExitCode != 0 ? null : FromJson(stdout.GetAwaiter().GetResult());
        }
        catch
        {
            return null;
        }
    }

    private static BuildSnapshot? FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize(json, ComparisonJsonContext.Default.SnapshotDto);
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
                .Select(p => new SnapshotProject { Name = p.Name!, SelfTimeMs = p.SelfTimeMs, FullPath = p.FullPath })
                .ToList(),
        };
    }
}

public sealed record SnapshotProject
{
    public required string Name { get; init; }
    public long SelfTimeMs { get; init; }
    /// <summary>Full project path — the stable identity used to key comparisons (names can collide). Null on older reports.</summary>
    public string? FullPath { get; init; }
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
        // Key by full path so two projects that share a short name in different folders are not
        // conflated (the analyzer already dedupes projects by full path). Fall back to the display
        // name only when a snapshot predates full-path capture.
        static string Key(SnapshotProject p) => string.IsNullOrEmpty(p.FullPath) ? p.Name : p.FullPath!;

        var baseByKey = baseline.Projects
            .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var curByKey = current.Projects
            .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var deltas = new List<ProjectDelta>();
        foreach (var kv in curByKey)
        {
            if (!baseByKey.TryGetValue(kv.Key, out var basep)) continue;
            deltas.Add(new ProjectDelta { Name = kv.Value.Name, BaselineMs = basep.SelfTimeMs, CurrentMs = kv.Value.SelfTimeMs });
        }

        var regressions = deltas.Where(d => d.DeltaMs > 0)
            .OrderByDescending(d => d.DeltaMs).Take(topN).ToList();
        var improvements = deltas.Where(d => d.DeltaMs < 0)
            .OrderBy(d => d.DeltaMs).Take(topN).ToList();
        var added = DisplayNames(curByKey.Values.Where(p => !baseByKey.ContainsKey(Key(p))));
        var removed = DisplayNames(baseByKey.Values.Where(p => !curByKey.ContainsKey(Key(p))));

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

    // Project short names for display. When the same short name occurs more than once (the same name
    // in different folders), disambiguate by appending the parent directory so the added/removed
    // lists can't conflate two distinct projects.
    private static IReadOnlyList<string> DisplayNames(IEnumerable<SnapshotProject> projects)
    {
        var list = projects.ToList();
        var counts = list
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return list
            .Select(p => counts[p.Name] > 1 && ParentDir(p.FullPath) is { Length: > 0 } dir
                ? $"{p.Name} ({dir})"
                : p.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ParentDir(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return "";
        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            return string.IsNullOrEmpty(dir) ? "" : Path.GetFileName(dir) ?? "";
        }
        catch
        {
            return "";
        }
    }

    // Unrounded on purpose: --fail-on regression compares WorstRegressionPercent against the
    // threshold, so rounding here (e.g. 10.04 → 10.0) would let an at-threshold regression slip
    // through the gate. Every display site formats with "F1", so the precision is invisible in output.
    private static double Pct(long baseline, long current) =>
        baseline > 0 ? (double)(current - baseline) / baseline * 100 : 0;
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
    public string? FullPath { get; init; }
}

[JsonSerializable(typeof(SnapshotDto))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
internal sealed partial class ComparisonJsonContext : JsonSerializerContext;
