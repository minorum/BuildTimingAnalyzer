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
            SelfTime = TimeSpan.FromSeconds(12.5),
            Succeeded = true,
            ErrorCount = 0,
            WarningCount = 3,
            SelfPercent = 62.5,
            StartOffset = TimeSpan.Zero,
            EndOffset = TimeSpan.FromSeconds(12.5),
        };

        var target = new TargetTiming
        {
            Name = "CoreCompile",
            ProjectName = "MyApp",
            SelfTime = TimeSpan.FromSeconds(8.2),
            SelfPercent = 41.0,
            Category = TargetCategory.Compile,
        };

        return new BuildReport
        {
            ProjectOrSolutionPath = "C:\\src\\MyApp.sln",
            StartTime = start,
            EndTime = start + TimeSpan.FromSeconds(20),
            Succeeded = true,
            ErrorCount = 0,
            WarningCount = 3,
            AttributedWarningCount = 3,
            Projects = [project],
            TopTargets = [target],
            Context = new BuildContext { Configuration = "Debug" },
            CategoryTotals = new Dictionary<TargetCategory, TimeSpan>
            {
                [TargetCategory.Compile] = TimeSpan.FromSeconds(8.2),
            },
            ExecutedTargetCount = 5,
            SkippedTargetCount = 0,
            PotentiallyCustomTargets = [],
            ReferenceOverhead = null,
            SpanOutliers = [],
            Graph = TestDefaults.EmptyGraph(1),
            CriticalPath = [],
            CriticalPathTotal = TimeSpan.Zero,
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
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_ContainsProjectsWithSelfTime()
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
            await Assert.That(first.GetProperty("selfPercent").GetDouble()).IsEqualTo(62.5);
            await Assert.That(first.GetProperty("selfTimeMs").GetInt64()).IsEqualTo(12500);
            await Assert.That(first.GetProperty("spanMs").GetInt64()).IsEqualTo(12500);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_TargetsIncludeCategory()
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
            await Assert.That(targets[0].GetProperty("category").GetString()).IsEqualTo("Compile");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_ContainsBuildContext()
    {
        var report = CreateSampleReport();
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.json");
        try
        {
            JsonReportExporter.Export(report, path);
            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);

            var context = doc.RootElement.GetProperty("context");
            await Assert.That(context.GetProperty("configuration").GetString()).IsEqualTo("Debug");
            await Assert.That(context.GetProperty("executedTargetCount").GetInt32()).IsEqualTo(5);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_WithAnalysis_ContainsEvidenceAndThreshold()
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
                    Evidence = "SelfTime=10s",
                    ThresholdName = "BottleneckWarningPct=15%",
                },
            ],
            Recommendations =
            [
                new AnalysisRecommendation { Number = 1, Text = "Investigate" },
            ],
        };
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.json");
        try
        {
            JsonReportExporter.Export(report, path, analysis);
            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);

            var findings = doc.RootElement.GetProperty("analysis").GetProperty("findings");
            await Assert.That(findings.GetArrayLength()).IsEqualTo(1);
            await Assert.That(findings[0].GetProperty("severity").GetString()).IsEqualTo("warning");
            await Assert.That(findings[0].GetProperty("evidence").GetString()).IsEqualTo("SelfTime=10s");
            await Assert.That(findings[0].GetProperty("threshold").GetString()).Contains("BottleneckWarningPct");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Export_WithoutAnalysis_AnalysisIsOmitted()
    {
        // DefaultIgnoreCondition.WhenWritingNull — the analysis key should be absent
        var report = CreateSampleReport();
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.json");
        try
        {
            JsonReportExporter.Export(report, path);
            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);

            var hasAnalysis = doc.RootElement.TryGetProperty("analysis", out _);
            await Assert.That(hasAnalysis).IsFalse();
        }
        finally { File.Delete(path); }
    }
}
