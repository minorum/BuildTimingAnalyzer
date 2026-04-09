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
            TotalDuration = ConsoleReportRenderer.FormatDuration(report.TotalDuration),
            TotalDurationMs = (long)report.TotalDuration.TotalMilliseconds,
            ErrorCount = report.ErrorCount,
            WarningCount = report.WarningCount,
            Projects = report
                .Projects.Select(p => new JsonProjectDto
                {
                    Name = p.Name,
                    FullPath = p.FullPath,
                    DurationMs = (long)p.Duration.TotalMilliseconds,
                    Duration = ConsoleReportRenderer.FormatDuration(p.Duration),
                    Percentage = Math.Round(p.Percentage, 2),
                    Succeeded = p.Succeeded,
                    ErrorCount = p.ErrorCount,
                    WarningCount = p.WarningCount,
                })
                .ToList(),
            TopTargets = report
                .TopTargets.Select(t => new JsonTargetDto
                {
                    Name = t.Name,
                    Project = t.ProjectName,
                    DurationMs = (long)t.Duration.TotalMilliseconds,
                    Duration = ConsoleReportRenderer.FormatDuration(t.Duration),
                    Percentage = Math.Round(t.Percentage, 2),
                })
                .ToList(),
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
}

[JsonSerializable(typeof(JsonReportDto))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase
)]
internal sealed partial class JsonExportContext : JsonSerializerContext;

internal sealed class JsonReportDto
{
    public required string Project { get; init; }
    public required bool Succeeded { get; init; }
    public required DateTime StartTime { get; init; }
    public required DateTime EndTime { get; init; }
    public required string TotalDuration { get; init; }
    public required long TotalDurationMs { get; init; }
    public required int ErrorCount { get; init; }
    public required int WarningCount { get; init; }
    public required List<JsonProjectDto> Projects { get; init; }
    public required List<JsonTargetDto> TopTargets { get; init; }
    public JsonAnalysisDto? Analysis { get; init; }
}

internal sealed class JsonProjectDto
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required long DurationMs { get; init; }
    public required string Duration { get; init; }
    public required double Percentage { get; init; }
    public required bool Succeeded { get; init; }
    public required int ErrorCount { get; init; }
    public required int WarningCount { get; init; }
}

internal sealed class JsonTargetDto
{
    public required string Name { get; init; }
    public required string Project { get; init; }
    public required long DurationMs { get; init; }
    public required string Duration { get; init; }
    public required double Percentage { get; init; }
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
}

internal sealed class JsonRecommendationDto
{
    public required int Number { get; init; }
    public required string Text { get; init; }
}
