using System.Text.Json;
using System.Text.Json.Serialization;
using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Rendering;

namespace BuildTimeAnalyzer.Export;

public static class JsonReportExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static void Export(BuildReport report, string outputPath, BuildAnalysis? analysis = null)
    {
        var dto = new
        {
            project = report.ProjectOrSolutionPath,
            succeeded = report.Succeeded,
            startTime = report.StartTime,
            endTime = report.EndTime,
            totalDuration = ConsoleReportRenderer.FormatDuration(report.TotalDuration),
            totalDurationMs = (long)report.TotalDuration.TotalMilliseconds,
            errorCount = report.ErrorCount,
            warningCount = report.WarningCount,
            projects = report.Projects.Select(p => new
            {
                name = p.Name,
                fullPath = p.FullPath,
                durationMs = (long)p.Duration.TotalMilliseconds,
                duration = ConsoleReportRenderer.FormatDuration(p.Duration),
                percentage = Math.Round(p.Percentage, 2),
                succeeded = p.Succeeded,
                errorCount = p.ErrorCount,
                warningCount = p.WarningCount,
            }),
            topTargets = report.TopTargets.Select(t => new
            {
                name = t.Name,
                project = t.ProjectName,
                durationMs = (long)t.Duration.TotalMilliseconds,
                duration = ConsoleReportRenderer.FormatDuration(t.Duration),
                percentage = Math.Round(t.Percentage, 2),
            }),
            analysis = analysis is not null ? new
            {
                findings = analysis.Findings.Select(f => new
                {
                    number = f.Number,
                    severity = f.Severity.ToString().ToLowerInvariant(),
                    title = f.Title,
                    detail = f.Detail,
                }),
                recommendations = analysis.Recommendations.Select(r => new
                {
                    number = r.Number,
                    text = r.Text,
                }),
            } : null,
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        File.WriteAllText(outputPath, json, System.Text.Encoding.UTF8);
    }
}
