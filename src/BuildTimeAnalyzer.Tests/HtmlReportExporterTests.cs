using BuildTimeAnalyzer.Export;
using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Tests;

public sealed class HtmlReportExporterTests
{
    private static BuildReport CreateSampleReport(bool succeeded = true)
    {
        var start = new DateTime(2025, 6, 15, 10, 0, 0);
        var project = new ProjectTiming
        {
            Name = "MyApp",
            FullPath = "C:\\src\\MyApp\\MyApp.csproj",
            Duration = TimeSpan.FromSeconds(12.5),
            Succeeded = succeeded,
            ErrorCount = succeeded ? 0 : 2,
            WarningCount = 3,
            Percentage = 62.5,
        };

        return new BuildReport
        {
            ProjectOrSolutionPath = "C:\\src\\MyApp.sln",
            StartTime = start,
            EndTime = start + TimeSpan.FromSeconds(20),
            Succeeded = succeeded,
            ErrorCount = succeeded ? 0 : 2,
            WarningCount = 3,
            Projects = [project],
            TopTargets = [],
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
        finally
        {
            File.Delete(path);
        }
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
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Export_SucceededBuild_ShowsSuccessBadge()
    {
        var report = CreateSampleReport(succeeded: true);
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");

        try
        {
            HtmlReportExporter.Export(report, path, 10);
            var html = File.ReadAllText(path);

            await Assert.That(html).Contains("Build Succeeded");
            await Assert.That(html).Contains("badge success");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Export_FailedBuild_ShowsFailBadge()
    {
        var report = CreateSampleReport(succeeded: false);
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");

        try
        {
            HtmlReportExporter.Export(report, path, 10);
            var html = File.ReadAllText(path);

            await Assert.That(html).Contains("Build Failed");
            await Assert.That(html).Contains("badge fail");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Export_EscapesHtmlSpecialCharacters()
    {
        var start = new DateTime(2025, 6, 15, 10, 0, 0);
        var project = new ProjectTiming
        {
            Name = "<script>alert('xss')</script>",
            FullPath = "C:\\src\\test.csproj",
            Duration = TimeSpan.FromSeconds(5),
            Succeeded = true,
            ErrorCount = 0,
            WarningCount = 0,
            Percentage = 100,
        };

        var report = new BuildReport
        {
            ProjectOrSolutionPath = "C:\\src\\test.sln",
            StartTime = start,
            EndTime = start + TimeSpan.FromSeconds(5),
            Succeeded = true,
            ErrorCount = 0,
            WarningCount = 0,
            Projects = [project],
            TopTargets = [],
        };

        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");

        try
        {
            HtmlReportExporter.Export(report, path, 10);
            var html = File.ReadAllText(path);

            await Assert.That(html).DoesNotContain("<script>alert");
            await Assert.That(html).Contains("&lt;script&gt;");
        }
        finally
        {
            File.Delete(path);
        }
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
                    Detail = "Test finding detail",
                    Severity = FindingSeverity.Critical,
                },
            ],
            Recommendations =
            [
                new AnalysisRecommendation { Number = 1, Text = "Fix the issue" },
            ],
        };

        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.html");

        try
        {
            HtmlReportExporter.Export(report, path, 10, analysis);
            var html = File.ReadAllText(path);

            await Assert.That(html).Contains("Test finding title");
            await Assert.That(html).Contains("Test finding detail");
            await Assert.That(html).Contains("Fix the issue");
            await Assert.That(html).Contains("severity-critical");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
