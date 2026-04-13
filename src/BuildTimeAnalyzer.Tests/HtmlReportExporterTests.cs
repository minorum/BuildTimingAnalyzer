using BuildTimeAnalyzer.Export;
using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Tests;

public sealed class HtmlReportExporterTests
{
    private static BuildReport CreateSampleReport(bool succeeded = true, ProjectTiming? overrideProject = null)
    {
        var start = new DateTime(2025, 6, 15, 10, 0, 0);
        var project = overrideProject ?? new ProjectTiming
        {
            Name = "MyApp",
            FullPath = "C:\\src\\MyApp\\MyApp.csproj",
            SelfTime = TimeSpan.FromSeconds(12.5),
            Succeeded = succeeded,
            ErrorCount = succeeded ? 0 : 2,
            WarningCount = 3,
            SelfPercent = 62.5,
            StartOffset = TimeSpan.Zero,
            EndOffset = TimeSpan.FromSeconds(12.5),
        };

        return new BuildReport
        {
            ProjectOrSolutionPath = "C:\\src\\MyApp.sln",
            StartTime = start,
            EndTime = start + TimeSpan.FromSeconds(20),
            Succeeded = succeeded,
            ErrorCount = succeeded ? 0 : 2,
            WarningCount = 3,
            AttributedWarningCount = 3,
            WarningsByCode = [],
            GeneratedComInterfaceUsages = [],
            Projects = [project],
            TopTargets = [],
            Context = new BuildContext { Configuration = "Release" },
            CategoryTotals = new Dictionary<TargetCategory, TimeSpan>(),
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

    [Test]
    public async Task Export_ProducesValidHtmlFile()
    {
        var report = CreateSampleReport();
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path, 10);
            var html = File.ReadAllText(path);
            await Assert.That(html).Contains("<!DOCTYPE html>");
            await Assert.That(html).Contains("</html>");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_ContainsProjectName()
    {
        var report = CreateSampleReport();
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path, 10);
            var html = File.ReadAllText(path);
            await Assert.That(html).Contains("MyApp");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_ContainsTimeSummary()
    {
        var report = CreateSampleReport();
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path, 10);
            var html = File.ReadAllText(path);
            // New one-line summary uses "elapsed" and the Top Consumers section uses "Time".
            await Assert.That(html).Contains("elapsed");
            await Assert.That(html).Contains("Top time consumers");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_ContainsBuildContextWhenAvailable()
    {
        var report = CreateSampleReport();
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path, 10);
            var html = File.ReadAllText(path);
            await Assert.That(html).Contains("Build Context");
            await Assert.That(html).Contains("Release");
            await Assert.That(html).Contains("2 of 12 targets skipped");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_SucceededBuild_ShowsSuccessInSummary()
    {
        var report = CreateSampleReport(succeeded: true);
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path, 10);
            var html = File.ReadAllText(path);
            // One-line summary; no decorative badge.
            await Assert.That(html).Contains("Build Succeeded");
            await Assert.That(html).Contains("summary-line");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_FailedBuild_ShowsFailureInSummary()
    {
        var report = CreateSampleReport(succeeded: false);
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path, 10);
            var html = File.ReadAllText(path);
            await Assert.That(html).Contains("Build Failed");
            await Assert.That(html).Contains("summary-line");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_EscapesHtmlSpecialCharacters()
    {
        var project = new ProjectTiming
        {
            Name = "<script>alert('xss')</script>",
            FullPath = "C:\\src\\test.csproj",
            SelfTime = TimeSpan.FromSeconds(5),
            Succeeded = true,
            ErrorCount = 0,
            WarningCount = 0,
            SelfPercent = 100,
            StartOffset = TimeSpan.Zero,
            EndOffset = TimeSpan.FromSeconds(5),
        };
        var report = CreateSampleReport(overrideProject: project);

        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path, 10);
            var html = File.ReadAllText(path);
            await Assert.That(html).DoesNotContain("<script>alert");
            await Assert.That(html).Contains("&lt;script&gt;");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_WithAnalysis_ContainsFindingsSection()
    {
        var report = CreateSampleReport();
        var analysis = new BuildAnalysis
        {
            Findings =
            [
                new AnalysisFinding
                {
                    Number = 1,
                    Title = "Test finding title",
                    Measured = "Test measured facts",
                    LikelyExplanation = "A possible heuristic reason",
                    InvestigationSuggestion = "Look into this",
                    Severity = FindingSeverity.Critical,
                    Confidence = FindingConfidence.High,
                    Evidence = "SelfTime=10s",
                    ThresholdName = "SomeThreshold=25%",
                },
            ],
            Recommendations =
            [
                new AnalysisRecommendation { Number = 1, Text = "Investigate the issue" },
            ],
        };

        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");
        try
        {
            HtmlReportExporter.Export(report, path, 10, analysis);
            var html = File.ReadAllText(path);
            // New bottleneck format: title + measured + inspect line. LikelyExplanation
            // and Recommendations section were removed per spec.
            await Assert.That(html).Contains("Test finding title");
            await Assert.That(html).Contains("Test measured facts");
            await Assert.That(html).Contains("Look into this");
            await Assert.That(html).Contains("bottleneck");
            await Assert.That(html).Contains("sev-critical");
        }
        finally { File.Delete(path); }
    }
}
