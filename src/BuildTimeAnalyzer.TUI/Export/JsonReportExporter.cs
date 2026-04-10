using System.Text.Json;
using System.Text.Json.Serialization;
using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Rendering;

namespace BuildTimeAnalyzer.Export;

public static class JsonReportExporter
{
    public static void Export(BuildReport report, string outputPath, BuildAnalysis? analysis = null)
    {
        var dto = BuildDto(report, analysis);
        var json = JsonSerializer.Serialize(dto, JsonExportContext.Default.JsonReportDto);
        File.WriteAllText(outputPath, json, System.Text.Encoding.UTF8);
    }

    private static JsonReportDto BuildDto(BuildReport report, BuildAnalysis? analysis) => new()
    {
        Project = report.ProjectOrSolutionPath,
        Succeeded = report.Succeeded,
        StartTime = report.StartTime,
        EndTime = report.EndTime,
        WallClock = ConsoleReportRenderer.FormatDuration(report.TotalDuration),
        WallClockMs = (long)report.TotalDuration.TotalMilliseconds,
        ErrorCount = report.ErrorCount,
        WarningCount = report.WarningCount,
        AttributedWarningCount = report.AttributedWarningCount,
        UnattributedWarningCount = report.UnattributedWarningCount,
        Context = new JsonBuildContextDto
        {
            Configuration = report.Context.Configuration,
            BuildMode = report.Context.BuildMode,
            SdkVersion = report.Context.SdkVersion,
            MSBuildVersion = report.Context.MSBuildVersion,
            OperatingSystem = report.Context.OperatingSystem,
            Parallelism = report.Context.Parallelism,
            RestoreObserved = report.Context.RestoreObserved,
            ExecutedTargetCount = report.ExecutedTargetCount,
            SkippedTargetCount = report.SkippedTargetCount,
        },
        Projects = report.Projects.Select(ToProjectDto).ToList(),
        TopTargets = report.TopTargets.Select(ToTargetDto).ToList(),
        PotentiallyCustomTargets = report.PotentiallyCustomTargets.Select(ToTargetDto).ToList(),
        CategoryTotals = report.CategoryTotals.ToDictionary(
            kv => kv.Key.ToString(),
            kv => (long)kv.Value.TotalMilliseconds),
        ReferenceOverhead = report.ReferenceOverhead is { } o ? new JsonReferenceOverheadDto
        {
            TotalSelfTimeMs = (long)o.TotalSelfTime.TotalMilliseconds,
            TotalSelfTime = ConsoleReportRenderer.FormatDuration(o.TotalSelfTime),
            SelfPercent = Math.Round(o.SelfPercent, 2),
            PayingProjectsCount = o.PayingProjectsCount,
            TotalProjectsCount = o.TotalProjectsCount,
            PayingProjectsPercent = Math.Round(o.PayingProjectsPercent, 2),
            MedianPerPayingProjectMs = (long)o.MedianPerPayingProject.TotalMilliseconds,
            MedianPerPayingProject = ConsoleReportRenderer.FormatDuration(o.MedianPerPayingProject),
            TopProjects = o.TopProjects.Select(p => new JsonReferenceOverheadProjectDto
            {
                Name = p.ProjectName,
                SelfTimeMs = (long)p.SelfTime.TotalMilliseconds,
                SelfTime = ConsoleReportRenderer.FormatDuration(p.SelfTime),
            }).ToList(),
        } : null,
        SpanOutliers = report.SpanOutliers.Select(p => new JsonSpanOutlierDto
        {
            Name = p.Name,
            FullPath = p.FullPath,
            SpanMs = (long)p.Span.TotalMilliseconds,
            Span = ConsoleReportRenderer.FormatDuration(p.Span),
            SelfTimeMs = (long)p.SelfTime.TotalMilliseconds,
            SelfTime = ConsoleReportRenderer.FormatDuration(p.SelfTime),
            Ratio = p.SelfTime.TotalMilliseconds > 0
                ? Math.Round(p.Span.TotalMilliseconds / p.SelfTime.TotalMilliseconds, 2)
                : 0,
            KindHeuristic = p.KindHeuristic.ToString(),
        }).ToList(),
        ProjectCountTax = new JsonProjectCountTaxDto
        {
            ReferencesExceedCompileCount = report.ProjectCountTax.ReferencesExceedCompileCount,
            ReferencesMajorityCount = report.ProjectCountTax.ReferencesMajorityCount,
            TinySelfHugeSpanCount = report.ProjectCountTax.TinySelfHugeSpanCount,
            TotalProjects = report.ProjectCountTax.TotalProjects,
            PerKindStats = report.ProjectCountTax.PerKindStats.Select(s => new JsonProjectKindStatsDto
            {
                Kind = s.Kind.ToString(),
                Count = s.Count,
                MedianSelfTimeMs = (long)s.MedianSelfTime.TotalMilliseconds,
                MedianSelfTime = ConsoleReportRenderer.FormatDuration(s.MedianSelfTime),
                MedianSpanMs = (long)s.MedianSpan.TotalMilliseconds,
                MedianSpan = ConsoleReportRenderer.FormatDuration(s.MedianSpan),
                MedianSpanToSelfRatio = Math.Round(s.MedianSpanToSelfRatio, 2),
            }).ToList(),
        },
        Graph = new JsonDependencyGraphDto
        {
            Health = new JsonGraphHealthDto
            {
                TotalProjects = report.Graph.Health.TotalProjects,
                TotalEdges = report.Graph.Health.TotalEdges,
                IsolatedNodes = report.Graph.Health.IsolatedNodes,
                NodesWithOutgoing = report.Graph.Health.NodesWithOutgoing,
                NodesWithIncoming = report.Graph.Health.NodesWithIncoming,
            },
            IsUsable = report.Graph.IsUsable,
            CycleDetectionRan = report.Graph.CycleDetectionRan,
            LongestChainProjectCount = report.Graph.LongestChainProjectCount,
            TopHubs = report.Graph.TopHubs.Select(h => new JsonGraphNodeDto
            {
                Name = h.ProjectName,
                FullPath = h.FullPath,
                OutgoingCount = h.OutgoingCount,
                IncomingCount = h.IncomingCount,
                TransitiveDependentsCount = h.TransitiveDependentsCount,
                TransitiveDependenciesCount = h.TransitiveDependenciesCount,
            }).ToList(),
            Cycles = report.Graph.Cycles.Select(c => c.ToList()).ToList(),
        },
        CriticalPath = report.CriticalPath.Select(p => new JsonCriticalPathNodeDto
        {
            Name = p.Name,
            FullPath = p.FullPath,
            SelfTimeMs = (long)p.SelfTime.TotalMilliseconds,
            SelfTime = ConsoleReportRenderer.FormatDuration(p.SelfTime),
            KindHeuristic = p.KindHeuristic.ToString(),
        }).ToList(),
        CriticalPathTotalMs = (long)report.CriticalPathTotal.TotalMilliseconds,
        CriticalPathTotal = ConsoleReportRenderer.FormatDuration(report.CriticalPathTotal),
        CriticalPathValidation = new JsonCriticalPathValidationDto
        {
            ComputedTotalMs = (long)report.CriticalPathValidation.ComputedTotal.TotalMilliseconds,
            ComputedTotal = ConsoleReportRenderer.FormatDuration(report.CriticalPathValidation.ComputedTotal),
            WallClockMs = (long)report.CriticalPathValidation.WallClock.TotalMilliseconds,
            WallClock = ConsoleReportRenderer.FormatDuration(report.CriticalPathValidation.WallClock),
            Accepted = report.CriticalPathValidation.Accepted,
            Reason = report.CriticalPathValidation.Reason,
            GraphWasUsable = report.CriticalPathValidation.GraphWasUsable,
        },
        Analysis = analysis is not null
            ? new JsonAnalysisDto
            {
                Findings = analysis.Findings.Select(f => new JsonFindingDto
                {
                    Number = f.Number,
                    Severity = f.Severity.ToString().ToLowerInvariant(),
                    Title = f.Title,
                    Measured = f.Measured,
                    LikelyExplanation = f.LikelyExplanation,
                    Investigate = f.InvestigationSuggestion,
                    Evidence = f.Evidence,
                    Threshold = f.ThresholdName,
                }).ToList(),
                Recommendations = analysis.Recommendations.Select(r => new JsonRecommendationDto
                {
                    Number = r.Number,
                    Text = r.Text,
                }).ToList(),
            }
            : null,
    };

    private static JsonProjectDto ToProjectDto(ProjectTiming p) => new()
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
        KindHeuristic = p.KindHeuristic.ToString(),
        Targets = p.Targets.Count == 0 ? null : p.Targets.Select(ToTargetDto).ToList(),
        CategoryBreakdown = p.CategoryBreakdown.Count == 0 ? null : p.CategoryBreakdown.ToDictionary(
            kv => kv.Key.ToString(),
            kv => (long)kv.Value.TotalMilliseconds),
    };

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
    public required int AttributedWarningCount { get; init; }
    public required int UnattributedWarningCount { get; init; }
    public required JsonBuildContextDto Context { get; init; }
    public required List<JsonProjectDto> Projects { get; init; }
    public required List<JsonTargetDto> TopTargets { get; init; }
    public required List<JsonTargetDto> PotentiallyCustomTargets { get; init; }
    public required Dictionary<string, long> CategoryTotals { get; init; }
    public JsonReferenceOverheadDto? ReferenceOverhead { get; init; }
    public required List<JsonSpanOutlierDto> SpanOutliers { get; init; }
    public required JsonProjectCountTaxDto ProjectCountTax { get; init; }
    public required JsonDependencyGraphDto Graph { get; init; }
    public required List<JsonCriticalPathNodeDto> CriticalPath { get; init; }
    public required long CriticalPathTotalMs { get; init; }
    public required string CriticalPathTotal { get; init; }
    public required JsonCriticalPathValidationDto CriticalPathValidation { get; init; }
    public JsonAnalysisDto? Analysis { get; init; }
}

internal sealed class JsonBuildContextDto
{
    public string? Configuration { get; init; }
    public string? BuildMode { get; init; }
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
    public required string KindHeuristic { get; init; }
    public List<JsonTargetDto>? Targets { get; init; }
    public Dictionary<string, long>? CategoryBreakdown { get; init; }
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

internal sealed class JsonReferenceOverheadDto
{
    public required long TotalSelfTimeMs { get; init; }
    public required string TotalSelfTime { get; init; }
    public required double SelfPercent { get; init; }
    public required int PayingProjectsCount { get; init; }
    public required int TotalProjectsCount { get; init; }
    public required double PayingProjectsPercent { get; init; }
    public required long MedianPerPayingProjectMs { get; init; }
    public required string MedianPerPayingProject { get; init; }
    public required List<JsonReferenceOverheadProjectDto> TopProjects { get; init; }
}

internal sealed class JsonReferenceOverheadProjectDto
{
    public required string Name { get; init; }
    public required long SelfTimeMs { get; init; }
    public required string SelfTime { get; init; }
}

internal sealed class JsonSpanOutlierDto
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required long SpanMs { get; init; }
    public required string Span { get; init; }
    public required long SelfTimeMs { get; init; }
    public required string SelfTime { get; init; }
    public required double Ratio { get; init; }
    public required string KindHeuristic { get; init; }
}

internal sealed class JsonProjectCountTaxDto
{
    public required int ReferencesExceedCompileCount { get; init; }
    public required int ReferencesMajorityCount { get; init; }
    public required int TinySelfHugeSpanCount { get; init; }
    public required int TotalProjects { get; init; }
    public required List<JsonProjectKindStatsDto> PerKindStats { get; init; }
}

internal sealed class JsonProjectKindStatsDto
{
    public required string Kind { get; init; }
    public required int Count { get; init; }
    public required long MedianSelfTimeMs { get; init; }
    public required string MedianSelfTime { get; init; }
    public required long MedianSpanMs { get; init; }
    public required string MedianSpan { get; init; }
    public required double MedianSpanToSelfRatio { get; init; }
}

internal sealed class JsonDependencyGraphDto
{
    public required JsonGraphHealthDto Health { get; init; }
    public required bool IsUsable { get; init; }
    public required bool CycleDetectionRan { get; init; }
    public required int LongestChainProjectCount { get; init; }
    public required List<JsonGraphNodeDto> TopHubs { get; init; }
    public required List<List<string>> Cycles { get; init; }
}

internal sealed class JsonGraphHealthDto
{
    public required int TotalProjects { get; init; }
    public required int TotalEdges { get; init; }
    public required int IsolatedNodes { get; init; }
    public required int NodesWithOutgoing { get; init; }
    public required int NodesWithIncoming { get; init; }
}

internal sealed class JsonGraphNodeDto
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required int OutgoingCount { get; init; }
    public required int IncomingCount { get; init; }
    public required int TransitiveDependentsCount { get; init; }
    public required int TransitiveDependenciesCount { get; init; }
}

internal sealed class JsonCriticalPathNodeDto
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required long SelfTimeMs { get; init; }
    public required string SelfTime { get; init; }
    public required string KindHeuristic { get; init; }
}

internal sealed class JsonCriticalPathValidationDto
{
    public required long ComputedTotalMs { get; init; }
    public required string ComputedTotal { get; init; }
    public required long WallClockMs { get; init; }
    public required string WallClock { get; init; }
    public required bool Accepted { get; init; }
    public required string Reason { get; init; }
    public required bool GraphWasUsable { get; init; }
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
    public required string Measured { get; init; }
    public string? LikelyExplanation { get; init; }
    public required string Investigate { get; init; }
    public required string Evidence { get; init; }
    public required string Threshold { get; init; }
}

internal sealed class JsonRecommendationDto
{
    public required int Number { get; init; }
    public required string Text { get; init; }
}
