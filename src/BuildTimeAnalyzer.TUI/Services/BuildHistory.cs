using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Services;

/// <summary>
/// Appends one compact JSON line per run to a history file (JSONL), so build performance can be
/// tracked over time. Best-effort: any I/O error is swallowed — recording history must never fail a run.
/// </summary>
public static class BuildHistory
{
    /// <summary>Appends a run summary. Returns false (without throwing) if the write failed.</summary>
    public static bool Append(string path, BuildReport report, BuildAnalysis analysis, DateTime timestampUtc)
    {
        try
        {
            var top = analysis.Findings
                .OrderBy(f => f.Severity switch
                {
                    FindingSeverity.Critical => 0,
                    FindingSeverity.Warning => 1,
                    _ => 2,
                })
                .FirstOrDefault();

            var entry = new HistoryEntry
            {
                Timestamp = timestampUtc.ToString("O", CultureInfo.InvariantCulture),
                Project = report.ProjectOrSolutionPath,
                Succeeded = report.Succeeded,
                WallClockMs = (long)report.TotalDuration.TotalMilliseconds,
                TotalSelfTimeMs = (long)report.TotalSelfTime.TotalMilliseconds,
                AchievedParallelism = Math.Round(report.AchievedParallelism, 2),
                WarningCount = report.WarningCount,
                ErrorCount = report.ErrorCount,
                FindingCount = analysis.Findings.Count,
                CriticalCount = analysis.Findings.Count(f => f.Severity == FindingSeverity.Critical),
                TopFinding = top?.Title,
            };

            var json = JsonSerializer.Serialize(entry, HistoryJsonContext.Default.HistoryEntry);
            File.AppendAllText(path, json + Environment.NewLine);
            return true;
        }
        catch
        {
            // History is a convenience, never a hard requirement — report failure, don't throw.
            return false;
        }
    }
}

internal sealed class HistoryEntry
{
    public required string Timestamp { get; init; }
    public string? Project { get; init; }
    public bool Succeeded { get; init; }
    public long WallClockMs { get; init; }
    public long TotalSelfTimeMs { get; init; }
    public double AchievedParallelism { get; init; }
    public int WarningCount { get; init; }
    public int ErrorCount { get; init; }
    public int FindingCount { get; init; }
    public int CriticalCount { get; init; }
    public string? TopFinding { get; init; }
}

[JsonSerializable(typeof(HistoryEntry))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;
