using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Tests;

public sealed class ProjectDiagnosisBuilderTests
{
    private static ProjectTiming MakeProject(string name, double selfSeconds, double spanSeconds = 0)
    {
        return new ProjectTiming
        {
            Name = name,
            FullPath = $"C:/repo/src/{name}/{name}.csproj",
            SelfTime = TimeSpan.FromSeconds(selfSeconds),
            Succeeded = true,
            ErrorCount = 0,
            WarningCount = 0,
            SelfPercent = selfSeconds * 10,
            StartOffset = TimeSpan.Zero,
            EndOffset = TimeSpan.FromSeconds(spanSeconds > 0 ? spanSeconds : selfSeconds),
            CategoryBreakdown = new Dictionary<TargetCategory, TimeSpan>
            {
                [TargetCategory.Compile] = TimeSpan.FromSeconds(selfSeconds * 0.7),
                [TargetCategory.References] = TimeSpan.FromSeconds(selfSeconds * 0.3),
            },
        };
    }

    private static AnalyzerReport MakeAnalyzerReport(string projectName, double analyzerSeconds = 0, double generatorSeconds = 0) => new()
    {
        ProjectName = projectName,
        TotalAnalyzerTime = TimeSpan.FromSeconds(analyzerSeconds),
        TotalGeneratorTime = TimeSpan.FromSeconds(generatorSeconds),
        CscWallTime = TimeSpan.FromSeconds(10),
        Analyzers = [],
        Generators = [],
    };

    [Test]
    public async Task Build_NoProjects_ReturnsEmpty()
    {
        var diagnoses = ProjectDiagnosisBuilder.Build(
            projects: [],
            analyzerReports: [],
            criticalPath: [],
            spanOutliers: [],
            topTasks: []);

        await Assert.That(diagnoses.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Build_DuplicateProjectShortNames_DoesNotThrow()
    {
        // Regression: previous ToDictionary() crashed with ArgumentException
        // when two projects had the same short name (common in monorepos).
        var reports = new[]
        {
            MakeAnalyzerReport("MyLib", analyzerSeconds: 2),
            MakeAnalyzerReport("MyLib", analyzerSeconds: 5), // same key
        };
        var projects = new[] { MakeProject("MyLib", selfSeconds: 10) };

        var diagnoses = ProjectDiagnosisBuilder.Build(projects, reports, [], [], []);

        await Assert.That(diagnoses.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Build_SpanOutlierSummary_DoesNotInterpretSpanAsCost()
    {
        // Span is documented as "display metric only — never used as a cost input."
        // The summary must not contain interpretive conclusions derived from Span.
        var project = MakeProject("SlowProject", selfSeconds: 1, spanSeconds: 30);

        var diagnoses = ProjectDiagnosisBuilder.Build(
            projects: [project],
            analyzerReports: [],
            criticalPath: [],
            spanOutliers: [project],
            topTasks: []);

        await Assert.That(diagnoses.Count).IsEqualTo(1);
        await Assert.That(diagnoses[0].IsSpanOutlier).IsTrue();
        // Summary should report span/self values factually but not draw conclusions.
        await Assert.That(diagnoses[0].Summary).DoesNotContain("not local work");
        await Assert.That(diagnoses[0].Summary).DoesNotContain("waiting");
    }

    [Test]
    public async Task Build_AnalyzerTimeAboveThreshold_AppearsInSummary()
    {
        var project = MakeProject("App", selfSeconds: 20);
        var reports = new[] { MakeAnalyzerReport("App", analyzerSeconds: 5) };

        var diagnoses = ProjectDiagnosisBuilder.Build(
            projects: [project],
            analyzerReports: reports,
            criticalPath: [],
            spanOutliers: [],
            topTasks: []);

        await Assert.That(diagnoses[0].AnalyzerTime).IsEqualTo(TimeSpan.FromSeconds(5));
        await Assert.That(diagnoses[0].Summary).Contains("Analyzer time");
    }

    [Test]
    public async Task Build_AnalyzerTimeBelowThreshold_OmittedFromSummary()
    {
        var project = MakeProject("App", selfSeconds: 20);
        var reports = new[] { MakeAnalyzerReport("App", analyzerSeconds: 0.05) }; // 50ms < 100ms threshold

        var diagnoses = ProjectDiagnosisBuilder.Build(
            projects: [project],
            analyzerReports: reports,
            criticalPath: [],
            spanOutliers: [],
            topTasks: []);

        await Assert.That(diagnoses[0].Summary).DoesNotContain("Analyzer time");
    }

    [Test]
    public async Task Build_OnCriticalPath_IsFlagged()
    {
        var project = MakeProject("Critical", selfSeconds: 5);

        var diagnoses = ProjectDiagnosisBuilder.Build(
            projects: [project],
            analyzerReports: [],
            criticalPath: [project],
            spanOutliers: [],
            topTasks: []);

        await Assert.That(diagnoses[0].OnCriticalPath).IsTrue();
        await Assert.That(diagnoses[0].Summary).Contains("critical path");
    }

    [Test]
    public async Task Build_TopNLimit_RespectsLimit()
    {
        var projects = Enumerable.Range(1, 10)
            .Select(i => MakeProject($"P{i}", selfSeconds: 10 - i))
            .ToList();

        var diagnoses = ProjectDiagnosisBuilder.Build(projects, [], [], [], [], topN: 3);

        await Assert.That(diagnoses.Count).IsEqualTo(3);
        await Assert.That(diagnoses[0].ProjectName).IsEqualTo("P1");
    }
}
