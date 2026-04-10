using System.Text.Json;
using System.Text.Json.Serialization;
using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Rendering;

namespace BuildTimeAnalyzer.Export;

public static class JsonReportExporter
{
    public static void Export(BuildReport report, string outputPath, BuildAnalysis? analysis = null)
    {
        var dto = new JsonReportDto
        {
            Project = report.ProjectOrSolutionPath,
            Succeeded = report.Succeeded,
            StartTime = report.StartTime,
            EndTime = report.EndTime,
            WallClock = ConsoleReportRenderer.FormatDuration(report.TotalDuration),
            WallClockMs = (long)report.TotalDuration.TotalMilliseconds,
            ErrorCount = report.ErrorCount,
            WarningCount = report.WarningCount,
            Context = new JsonBuildContextDto
            {
                Configuration = report.Context.Configuration,
                SdkVersion = report.Context.SdkVersion,
                MSBuildVersion = report.Context.MSBuildVersion,
                OperatingSystem = report.Context.OperatingSystem,
                Parallelism = report.Context.Parallelism,
                RestoreObserved = report.Context.RestoreObserved,
                ExecutedTargetCount = report.ExecutedTargetCount,
                SkippedTargetCount = report.SkippedTargetCount,
            },
            Projects = report
                .Projects.Select(p => new JsonProjectDto
                {
                    Name = p.Name,
                    FullPath = p.FullPath,
                    SelfTimeMs = (long)p.SelfTime.TotalMilliseconds,
                    SelfTime = ConsoleReportRenderer.FormatDuration(p.SelfTime),
                    SpanMs = (long)p.Span.TotalMilliseconds,
                    Span = ConsoleReportRenderer.FormatDuration(p.Span),
                    SelfPercent = Math.Round(p.SelfPercent, 2),
                    Succeeded = p.Succeeded,
                    ErrorCount = p.ErrorCount,
                    WarningCount = p.WarningCount,
                    Targets = p.Targets.Count == 0 ? null : p.Targets.Select(ToTargetDto).ToList(),
                })
                .ToList(),
            TopTargets = report.TopTargets.Select(ToTargetDto).ToList(),
            PotentiallyCustomTargets = report.PotentiallyCustomTargets.Select(ToTargetDto).ToList(),
            CategoryTotals = report.CategoryTotals.ToDictionary(
                kv => kv.Key.ToString(),
                kv => (long)kv.Value.TotalMilliseconds),
            CriticalPath = report.CriticalPath.Select(p => new JsonCriticalPathNodeDto
            {
                Name = p.Name,
                FullPath = p.FullPath,
                SelfTimeMs = (long)p.SelfTime.TotalMilliseconds,
                SelfTime = ConsoleReportRenderer.FormatDuration(p.SelfTime),
            }).ToList(),
            CriticalPathTotalMs = (long)report.CriticalPathTotal.TotalMilliseconds,
            CriticalPathTotal = ConsoleReportRenderer.FormatDuration(report.CriticalPathTotal),
            Analysis = analysis is not null
                ? new JsonAnalysisDto
                {
                    Findings = analysis
                        .Findings.Select(f => new JsonFindingDto
                        {
                            Number = f.Number,
                            Severity = f.Severity.ToString().ToLowerInvariant(),
                            Title = f.Title,
                            Detail = f.Detail,
                            Evidence = f.Evidence,
                            Threshold = f.ThresholdName,
                        })
                        .ToList(),
                    Recommendations = analysis
                        .Recommendations.Select(r => new JsonRecommendationDto
                        {
                            Number = r.Number,
                            Text = r.Text,
                        })
                        .ToList(),
                }
                : null,
        };

        var json = JsonSerializer.Serialize(dto, JsonExportContext.Default.JsonReportDto);
        File.WriteAllText(outputPath, json, System.Text.Encoding.UTF8);
    }

    private static JsonTargetDto ToTargetDto(TargetTiming t) => new()
    {
        Name = t.Name,
        Project = t.ProjectName,
        SelfTimeMs = (long)t.SelfTime.TotalMilliseconds,
        SelfTime = ConsoleReportRenderer.FormatDuration(t.SelfTime),
        SelfPercent = Math.Round(t.SelfPercent, 2),
        Category = t.Category.ToString(),
    };
}

[JsonSerializable(typeof(JsonReportDto))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
internal sealed partial class JsonExportContext : JsonSerializerContext;

internal sealed class JsonReportDto
{
    public required string Project { get; init; }
    public required bool Succeeded { get; init; }
    public required DateTime StartTime { get; init; }
    public required DateTime EndTime { get; init; }
    public required string WallClock { get; init; }
    public required long WallClockMs { get; init; }
    public required int ErrorCount { get; init; }
    public required int WarningCount { get; init; }
    public required JsonBuildContextDto Context { get; init; }
    public required List<JsonProjectDto> Projects { get; init; }
    public required List<JsonTargetDto> TopTargets { get; init; }
    public required List<JsonTargetDto> PotentiallyCustomTargets { get; init; }
    public required Dictionary<string, long> CategoryTotals { get; init; }
    public required List<JsonCriticalPathNodeDto> CriticalPath { get; init; }
    public required long CriticalPathTotalMs { get; init; }
    public required string CriticalPathTotal { get; init; }
    public JsonAnalysisDto? Analysis { get; init; }
}

internal sealed class JsonBuildContextDto
{
    public string? Configuration { get; init; }
    public string? SdkVersion { get; init; }
    public string? MSBuildVersion { get; init; }
    public string? OperatingSystem { get; init; }
    public int? Parallelism { get; init; }
    public bool? RestoreObserved { get; init; }
    public required int ExecutedTargetCount { get; init; }
    public required int SkippedTargetCount { get; init; }
}

internal sealed class JsonProjectDto
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required long SelfTimeMs { get; init; }
    public required string SelfTime { get; init; }
    public required long SpanMs { get; init; }
    public required string Span { get; init; }
    public required double SelfPercent { get; init; }
    public required bool Succeeded { get; init; }
    public required int ErrorCount { get; init; }
    public required int WarningCount { get; init; }
    public List<JsonTargetDto>? Targets { get; init; }
}

internal sealed class JsonTargetDto
{
    public required string Name { get; init; }
    public required string Project { get; init; }
    public required long SelfTimeMs { get; init; }
    public required string SelfTime { get; init; }
    public required double SelfPercent { get; init; }
    public required string Category { get; init; }
}

internal sealed class JsonCriticalPathNodeDto
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required long SelfTimeMs { get; init; }
    public required string SelfTime { get; init; }
}

internal sealed class JsonAnalysisDto
{
    public required List<JsonFindingDto> Findings { get; init; }
    public required List<JsonRecommendationDto> Recommendations { get; init; }
}

internal sealed class JsonFindingDto
{
    public required int Number { get; init; }
    public required string Severity { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string Evidence { get; init; }
    public required string Threshold { get; init; }
}

internal sealed class JsonRecommendationDto
{
    public required int Number { get; init; }
    public required string Text { get; init; }
}
