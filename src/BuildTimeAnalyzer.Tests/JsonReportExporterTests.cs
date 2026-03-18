using System.Text.Json;
using BuildTimeAnalyzer.Export;
using BuildTimeAnalyzer.Models;

namespace BuildTimeAnalyzer.Tests;

public sealed class JsonReportExporterTests
{
    private static BuildReport CreateSampleReport()
    {
        var start = new DateTime(2025, 6, 15, 10, 0, 0);
        var project = new ProjectTiming
        {
            Name = "MyApp",
            FullPath = "C:\\src\\MyApp\\MyApp.csproj",
            Duration = TimeSpan.FromSeconds(12.5),
            Succeeded = true,
            ErrorCount = 0,
            WarningCount = 3,
        };
        project.Percentage = 62.5;

        var target = new TargetTiming
        {
            Name = "CoreCompile",
            ProjectName = "MyApp",
            Duration = TimeSpan.FromSeconds(8.2),
        };
        target.Percentage = 41.0;

        return new BuildReport
        {
            ProjectOrSolutionPath = "C:\\src\\MyApp.sln",
            StartTime = start,
            EndTime = start + TimeSpan.FromSeconds(20),
            Succeeded = true,
            ErrorCount = 0,
            WarningCount = 3,
            Projects = [project],
            TopTargets = [target],
        };
    }

    [Test]
    public async Task Export_ProducesValidJson()
    {
        var report = CreateSampleReport();
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.json");

        try
        {
            JsonReportExporter.Export(report, path);
            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);

            await Assert.That(doc.RootElement.GetProperty("project").GetString()).IsEqualTo("C:\\src\\MyApp.sln");
            await Assert.That(doc.RootElement.GetProperty("succeeded").GetBoolean()).IsTrue();
            await Assert.That(doc.RootElement.GetProperty("warningCount").GetInt32()).IsEqualTo(3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Export_ContainsProjects()
    {
        var report = CreateSampleReport();
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.json");

        try
        {
            JsonReportExporter.Export(report, path);
            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);

            var projects = doc.RootElement.GetProperty("projects");
            await Assert.That(projects.GetArrayLength()).IsEqualTo(1);

            var first = projects[0];
            await Assert.That(first.GetProperty("name").GetString()).IsEqualTo("MyApp");
            await Assert.That(first.GetProperty("percentage").GetDouble()).IsEqualTo(62.5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Export_ContainsTargets()
    {
        var report = CreateSampleReport();
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.json");

        try
        {
            JsonReportExporter.Export(report, path);
            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);

            var targets = doc.RootElement.GetProperty("topTargets");
            await Assert.That(targets.GetArrayLength()).IsEqualTo(1);
            await Assert.That(targets[0].GetProperty("name").GetString()).IsEqualTo("CoreCompile");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Export_WithAnalysis_ContainsFindings()
    {
        var report = CreateSampleReport();
        var analysis = new BuildAnalysis
        {
            Findings =
            [
                new AnalysisFinding
                {
                    Number = 1,
                    Title = "Test finding",
                    Detail = "Test detail",
                    Severity = FindingSeverity.Warning,
                },
            ],
            Recommendations =
            [
                new AnalysisRecommendation { Number = 1, Text = "Fix it" },
            ],
        };
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.json");

        try
        {
            JsonReportExporter.Export(report, path, analysis);
            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);

            var analysisElement = doc.RootElement.GetProperty("analysis");
            var findings = analysisElement.GetProperty("findings");
            await Assert.That(findings.GetArrayLength()).IsEqualTo(1);
            await Assert.That(findings[0].GetProperty("severity").GetString()).IsEqualTo("warning");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Export_WithoutAnalysis_AnalysisIsNull()
    {
        var report = CreateSampleReport();
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.json");

        try
        {
            JsonReportExporter.Export(report, path);
            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);

            await Assert.That(doc.RootElement.GetProperty("analysis").ValueKind).IsEqualTo(JsonValueKind.Null);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
