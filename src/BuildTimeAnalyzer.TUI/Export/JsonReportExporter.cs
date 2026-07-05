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
        TopTasks = report.TopTasks.Select(t => new JsonTaskDto
        {
            TaskName = t.TaskName,
            TargetName = t.TargetName,
            Project = t.ProjectName,
            SelfTimeMs = (long)t.SelfTime.TotalMilliseconds,
            SelfTime = ConsoleReportRenderer.FormatDuration(t.SelfTime),
            SelfPercent = Math.Round(t.SelfPercent, 2),
        }).ToList(),
        SkipReasons = report.SkipReasons.Select(s => new JsonSkipDto
        {
            TargetName = s.TargetName,
            Project = s.ProjectName,
            SkipReason = s.SkipReason,
            Condition = s.Condition,
            EvaluatedCondition = s.EvaluatedCondition,
        }).ToList(),
        WarningsByCode = report.WarningsByCode.Select(w => new JsonWarningCodeDto
        {
            Code = w.Code,
            Prefix = w.Prefix,
            Count = w.Count,
        }).ToList(),
        GeneratedComInterfaceUsages = report.GeneratedComInterfaceUsages.ToList(),
        TfmNegotiationTotalMs = (long)report.TfmNegotiationTotal.TotalMilliseconds,
        AnalyzerReports = report.AnalyzerReports.Select(ToAnalyzerReportDto).ToList(),
        ProjectDiagnoses = report.ProjectDiagnoses.Select(ToProjectDiagnosisDto).ToList(),
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
                    Confidence = f.Confidence.ToString().ToLowerInvariant(),
                    Title = f.Title,
                    Measured = f.Measured,
                    LikelyExplanation = f.LikelyExplanation,
                    Investigate = f.InvestigationSuggestion,
                    UpperBoundImpactPercent = f.UpperBoundImpactPercent.HasValue
                        ? Math.Round(f.UpperBoundImpactPercent.Value, 2)
                        : null,
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

    private static JsonAnalyzerReportDto ToAnalyzerReportDto(AnalyzerReport r) => new()
    {
        Project = r.ProjectName,
        TotalAnalyzerTimeMs = (long)r.TotalAnalyzerTime.TotalMilliseconds,
        TotalGeneratorTimeMs = (long)r.TotalGeneratorTime.TotalMilliseconds,
        CscWallTimeMs = (long)r.CscWallTime.TotalMilliseconds,
        Analyzers = r.Analyzers.Select(ToAnalyzerEntryDto).ToList(),
        Generators = r.Generators.Select(ToAnalyzerEntryDto).ToList(),
    };

    private static JsonAnalyzerEntryDto ToAnalyzerEntryDto(AnalyzerEntry e) => new()
    {
        AssemblyName = e.AssemblyName,
        TimeMs = (long)e.Time.TotalMilliseconds,
        Time = ConsoleReportRenderer.FormatDuration(e.Time),
        Percent = Math.Round(e.Percent, 2),
    };

    private static JsonProjectDiagnosisDto ToProjectDiagnosisDto(ProjectDiagnosis d) => new()
    {
        ProjectName = d.ProjectName,
        SelfTimeMs = (long)d.SelfTime.TotalMilliseconds,
        SelfPercent = Math.Round(d.SelfPercent, 2),
        TopCategory = d.TopCategory,
        TopCategoryPercent = Math.Round(d.TopCategoryPercent, 2),
        TopTask = d.TopTask,
        TopTaskTimeMs = (long)d.TopTaskTime.TotalMilliseconds,
        AnalyzerTimeMs = d.AnalyzerTime.HasValue ? (long)d.AnalyzerTime.Value.TotalMilliseconds : null,
        GeneratorTimeMs = d.GeneratorTime.HasValue ? (long)d.GeneratorTime.Value.TotalMilliseconds : null,
        OnCriticalPath = d.OnCriticalPath,
        IsSpanOutlier = d.IsSpanOutlier,
        Summary = d.Summary,
        Packages = d.Packages is { } pk ? new JsonProjectPackagesDto
        {
            Quality = pk.Quality.ToString(),
            DirectPackages = pk.DirectPackages.Select(ToPackageRefDto).ToList(),
            TransitivePackages = pk.TransitivePackages.Select(ToPackageRefDto).ToList(),
            ProjectReferences = pk.ProjectReferences.ToList(),
        } : null,
    };

    private static JsonPackageRefDto ToPackageRefDto(PackageRef p) => new()
    {
        Id = p.Id,
        Version = p.Version,
        Source = p.Source.ToString(),
        ParentPackage = p.ParentPackage,
        IsKnownHeavy = p.IsKnownHeavy,
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
    public required List<JsonTaskDto> TopTasks { get; init; }
    public required List<JsonSkipDto> SkipReasons { get; init; }
    public required List<JsonWarningCodeDto> WarningsByCode { get; init; }
    public required List<string> GeneratedComInterfaceUsages { get; init; }
    public required long TfmNegotiationTotalMs { get; init; }
    public required List<JsonAnalyzerReportDto> AnalyzerReports { get; init; }
    public required List<JsonProjectDiagnosisDto> ProjectDiagnoses { get; init; }
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
    public required string Confidence { get; init; }
    public required string Title { get; init; }
    public required string Measured { get; init; }
    public string? LikelyExplanation { get; init; }
    public required string Investigate { get; init; }
    public double? UpperBoundImpactPercent { get; init; }
    public required string Evidence { get; init; }
    public required string Threshold { get; init; }
}

internal sealed class JsonRecommendationDto
{
    public required int Number { get; init; }
    public required string Text { get; init; }
}

internal sealed class JsonTaskDto
{
    public required string TaskName { get; init; }
    public required string TargetName { get; init; }
    public required string Project { get; init; }
    public required long SelfTimeMs { get; init; }
    public required string SelfTime { get; init; }
    public required double SelfPercent { get; init; }
}

internal sealed class JsonSkipDto
{
    public required string TargetName { get; init; }
    public required string Project { get; init; }
    public required string SkipReason { get; init; }
    public string? Condition { get; init; }
    public string? EvaluatedCondition { get; init; }
}

internal sealed class JsonWarningCodeDto
{
    public required string Code { get; init; }
    public required string Prefix { get; init; }
    public required int Count { get; init; }
}

internal sealed class JsonAnalyzerEntryDto
{
    public required string AssemblyName { get; init; }
    public required long TimeMs { get; init; }
    public required string Time { get; init; }
    public required double Percent { get; init; }
}

internal sealed class JsonAnalyzerReportDto
{
    public required string Project { get; init; }
    public required long TotalAnalyzerTimeMs { get; init; }
    public required long TotalGeneratorTimeMs { get; init; }
    public required long CscWallTimeMs { get; init; }
    public required List<JsonAnalyzerEntryDto> Analyzers { get; init; }
    public required List<JsonAnalyzerEntryDto> Generators { get; init; }
}

internal sealed class JsonPackageRefDto
{
    public required string Id { get; init; }
    public string? Version { get; init; }
    public required string Source { get; init; }
    public string? ParentPackage { get; init; }
    public required bool IsKnownHeavy { get; init; }
}

internal sealed class JsonProjectPackagesDto
{
    public required string Quality { get; init; }
    public required List<JsonPackageRefDto> DirectPackages { get; init; }
    public required List<JsonPackageRefDto> TransitivePackages { get; init; }
    public required List<string> ProjectReferences { get; init; }
}

internal sealed class JsonProjectDiagnosisDto
{
    public required string ProjectName { get; init; }
    public required long SelfTimeMs { get; init; }
    public required double SelfPercent { get; init; }
    public required string TopCategory { get; init; }
    public required double TopCategoryPercent { get; init; }
    public required string TopTask { get; init; }
    public required long TopTaskTimeMs { get; init; }
    public long? AnalyzerTimeMs { get; init; }
    public long? GeneratorTimeMs { get; init; }
    public required bool OnCriticalPath { get; init; }
    public required bool IsSpanOutlier { get; init; }
    public required string Summary { get; init; }
    public JsonProjectPackagesDto? Packages { get; init; }
}
