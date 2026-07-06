using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Tests;

/// <summary>Builders for realistic BuildReport fixtures shared across the newer test suites.</summary>
internal static class SampleReports
{
    public static BuildReport Minimal(
        double wallSeconds = 20,
        string projectName = "MyApp",
        double projectSelfSeconds = 12,
        int warningCount = 0,
        int errorCount = 0,
        bool succeeded = true)
    {
        var start = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var self = TimeSpan.FromSeconds(projectSelfSeconds);

        var project = new ProjectTiming
        {
            Name = projectName,
            FullPath = $"C:\\src\\{projectName}\\{projectName}.csproj",
            SelfTime = self,
            Succeeded = succeeded,
            ErrorCount = errorCount,
            WarningCount = warningCount,
            SelfPercent = 100.0,
            StartOffset = TimeSpan.Zero,
            EndOffset = self,
            CategoryBreakdown = new Dictionary<TargetCategory, TimeSpan>
            {
                [TargetCategory.Compile] = self,
            },
        };

        return new BuildReport
        {
            ProjectOrSolutionPath = "C:\\src\\MyApp.sln",
            StartTime = start,
            EndTime = start + TimeSpan.FromSeconds(wallSeconds),
            Succeeded = succeeded,
            ErrorCount = errorCount,
            WarningCount = warningCount,
            AttributedWarningCount = warningCount,
            WarningsByCode = [],
            GeneratedComInterfaceUsages = [],
            Projects = [project],
            TopTargets = [],
            TotalSelfTime = self,
            Context = new BuildContext { Configuration = "Release", BuildMode = "full (--no-incremental)" },
            CategoryTotals = new Dictionary<TargetCategory, TimeSpan> { [TargetCategory.Compile] = self },
            ExecutedTargetCount = 10,
            SkippedTargetCount = 2,
            PotentiallyCustomTargets = [],
            ReferenceOverhead = null,
            SpanOutliers = [],
            ProjectCountTax = TestDefaults.EmptyTax(1),
            TopTasks = [],
            SkipReasons = [],
            AnalyzerReports = [],
            ProjectDiagnoses = [],
            Graph = TestDefaults.EmptyGraph(1),
            CriticalPath = [],
            CriticalPathTotal = TimeSpan.Zero,
            CriticalPathValidation = TestDefaults.EmptyValidation(),
        };
    }
}
