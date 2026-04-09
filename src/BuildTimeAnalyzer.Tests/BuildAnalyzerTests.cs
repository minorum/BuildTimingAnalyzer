using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Tests;

public sealed class BuildAnalyzerTests
{
    private static BuildReport CreateReport(
        TimeSpan? totalDuration = null,
        List<ProjectTiming>? projects = null,
        List<TargetTiming>? targets = null,
        int warningCount = 0)
    {
        var duration = totalDuration ?? TimeSpan.FromSeconds(60);
        var start = new DateTime(2025, 1, 1, 12, 0, 0);
        return new BuildReport
        {
            ProjectOrSolutionPath = "Test.sln",
            StartTime = start,
            EndTime = start + duration,
            Succeeded = true,
            ErrorCount = 0,
            WarningCount = warningCount,
            Projects = projects ?? [],
            TopTargets = targets ?? [],
        };
    }

    private static ProjectTiming CreateProject(string name, double seconds, double percentage, int warningCount = 0) =>
        new()
        {
            Name = name,
            FullPath = $"C:\\src\\{name}\\{name}.csproj",
            Duration = TimeSpan.FromSeconds(seconds),
            Succeeded = true,
            ErrorCount = 0,
            WarningCount = warningCount,
            Percentage = percentage,
            StartOffset = TimeSpan.Zero,
            EndOffset = TimeSpan.FromSeconds(seconds),
        };

    private static TargetTiming CreateTarget(string name, string project, double seconds, double percentage) =>
        new()
        {
            Name = name,
            ProjectName = project,
            Duration = TimeSpan.FromSeconds(seconds),
            Percentage = percentage,
        };

    [Test]
    public async Task ShortBuild_ReturnsEmpty()
    {
        var report = CreateReport(totalDuration: TimeSpan.FromMilliseconds(500));
        var result = BuildAnalyzer.Analyze(report);

        await Assert.That(result.Findings.Count).IsEqualTo(0);
        await Assert.That(result.Recommendations.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SingleProject_NoBottleneckFinding()
    {
        var projects = new List<ProjectTiming>
        {
            CreateProject("OnlyProject", 30, 100),
        };
        var report = CreateReport(projects: projects);
        var result = BuildAnalyzer.Analyze(report);

        await Assert.That(result.Findings.Any(f => f.Title.Contains("bottleneck"))).IsFalse();
    }

    [Test]
    public async Task BottleneckProject_DetectedAsCritical()
    {
        var projects = new List<ProjectTiming>
        {
            CreateProject("BigProject", 30, 50),
            CreateProject("SmallProject", 5, 8.3),
        };
        var report = CreateReport(projects: projects);
        var result = BuildAnalyzer.Analyze(report);

        var bottleneck = result.Findings.FirstOrDefault(f => f.Title.Contains("bottleneck"));
        await Assert.That(bottleneck).IsNotNull();
        await Assert.That(bottleneck!.Severity).IsEqualTo(FindingSeverity.Critical);
        await Assert.That(bottleneck.Title).Contains("BigProject");
    }

    [Test]
    public async Task BottleneckProject_WarningLevel()
    {
        var projects = new List<ProjectTiming>
        {
            CreateProject("MediumProject", 12, 20),
            CreateProject("SmallProject", 5, 8.3),
        };
        var report = CreateReport(projects: projects);
        var result = BuildAnalyzer.Analyze(report);

        var bottleneck = result.Findings.FirstOrDefault(f => f.Title.Contains("bottleneck"));
        await Assert.That(bottleneck).IsNotNull();
        await Assert.That(bottleneck!.Severity).IsEqualTo(FindingSeverity.Warning);
    }

    [Test]
    public async Task DisproportionatelySlowProject_Detected()
    {
        var projects = new List<ProjectTiming>
        {
            CreateProject("Slow", 30, 50),
            CreateProject("Fast", 10, 16.7),
        };
        var report = CreateReport(projects: projects);
        var result = BuildAnalyzer.Analyze(report);

        var finding = result.Findings.FirstOrDefault(f => f.Title.Contains("longer than the next"));
        await Assert.That(finding).IsNotNull();
        await Assert.That(finding!.Title).Contains("Slow");
    }

    [Test]
    public async Task DisproportionatelySlowProject_NotDetectedWhenClose()
    {
        var projects = new List<ProjectTiming>
        {
            CreateProject("A", 10, 20),
            CreateProject("B", 9, 18),
        };
        var report = CreateReport(projects: projects);
        var result = BuildAnalyzer.Analyze(report);

        await Assert.That(result.Findings.Any(f => f.Title.Contains("longer than the next"))).IsFalse();
    }

    [Test]
    public async Task DominantTargetType_Detected()
    {
        var targets = new List<TargetTiming>
        {
            CreateTarget("CoreCompile", "A", 10, 16.7),
            CreateTarget("CoreCompile", "B", 8, 13.3),
            CreateTarget("CoreCompile", "C", 6, 10),
            CreateTarget("ResolveReferences", "A", 4, 6.7),
            CreateTarget("GenerateResource", "B", 2, 3.3),
        };
        var report = CreateReport(targets: targets);
        var result = BuildAnalyzer.Analyze(report);

        var finding = result.Findings.FirstOrDefault(f => f.Title.Contains("dominates"));
        await Assert.That(finding).IsNotNull();
        await Assert.That(finding!.Title).Contains("CoreCompile");
    }

    [Test]
    public async Task UnusuallySlowTarget_Detected()
    {
        var targets = new List<TargetTiming>
        {
            CreateTarget("CoreCompile", "Outlier", 25, 25),
            CreateTarget("CoreCompile", "Normal1", 5, 5),
            CreateTarget("CoreCompile", "Normal2", 5, 5),
            CreateTarget("CoreCompile", "Normal3", 4, 4),
        };
        var report = CreateReport(targets: targets);
        var result = BuildAnalyzer.Analyze(report);

        var finding = result.Findings.FirstOrDefault(f => f.Title.Contains("unusually slow"));
        await Assert.That(finding).IsNotNull();
        await Assert.That(finding!.Title).Contains("Outlier");
    }

    [Test]
    public async Task CostlyResolvePackageAssets_Detected()
    {
        var targets = new List<TargetTiming>
        {
            CreateTarget("ResolvePackageAssets", "BigLib", 5, 8.3),
            CreateTarget("CoreCompile", "BigLib", 10, 16.7),
        };
        var report = CreateReport(targets: targets);
        var result = BuildAnalyzer.Analyze(report);

        var finding = result.Findings.FirstOrDefault(f => f.Title.Contains("ResolvePackageAssets"));
        await Assert.That(finding).IsNotNull();
    }

    [Test]
    public async Task CostlyResolvePackageAssets_NotDetectedBelowThreshold()
    {
        var targets = new List<TargetTiming>
        {
            CreateTarget("ResolvePackageAssets", "SmallLib", 2, 3.3),
        };
        var report = CreateReport(targets: targets);
        var result = BuildAnalyzer.Analyze(report);

        await Assert.That(result.Findings.Any(f => f.Title.Contains("ResolvePackageAssets"))).IsFalse();
    }

    [Test]
    public async Task WarningConcentration_DetectedWhenConcentrated()
    {
        var projects = new List<ProjectTiming>
        {
            CreateProject("Warn1", 10, 20, warningCount: 30),
            CreateProject("Clean1", 10, 20),
            CreateProject("Clean2", 10, 20),
            CreateProject("Clean3", 10, 20),
            CreateProject("Clean4", 10, 20),
        };
        var report = CreateReport(projects: projects, warningCount: 30);
        var result = BuildAnalyzer.Analyze(report);

        var finding = result.Findings.FirstOrDefault(f => f.Title.Contains("warnings"));
        await Assert.That(finding).IsNotNull();
    }

    [Test]
    public async Task Findings_AreNumberedSequentially()
    {
        var projects = new List<ProjectTiming>
        {
            CreateProject("Big", 30, 50),
            CreateProject("Small", 5, 8.3),
        };
        var targets = new List<TargetTiming>
        {
            CreateTarget("ResolvePackageAssets", "Big", 5, 8.3),
        };
        var report = CreateReport(projects: projects, targets: targets);
        var result = BuildAnalyzer.Analyze(report);

        for (int i = 0; i < result.Findings.Count; i++)
        {
            await Assert.That(result.Findings[i].Number).IsEqualTo(i + 1);
        }
    }

    [Test]
    public async Task Recommendations_GeneratedForNonInfoFindings()
    {
        var projects = new List<ProjectTiming>
        {
            CreateProject("Big", 30, 50),
            CreateProject("Small", 5, 8.3),
        };
        var report = CreateReport(projects: projects);
        var result = BuildAnalyzer.Analyze(report);

        await Assert.That(result.Recommendations.Count).IsGreaterThan(0);
        for (int i = 0; i < result.Recommendations.Count; i++)
        {
            await Assert.That(result.Recommendations[i].Number).IsEqualTo(i + 1);
        }
    }

    [Test]
    public async Task ProjectCluster_Detected()
    {
        var projects = new List<ProjectTiming>
        {
            CreateProject("A", 10, 20),
            CreateProject("B", 9.5, 19),
            CreateProject("C", 9.2, 18.4),
            CreateProject("D", 9.0, 18),
        };
        var report = CreateReport(projects: projects);
        var result = BuildAnalyzer.Analyze(report);

        var finding = result.Findings.FirstOrDefault(f => f.Title.Contains("cluster"));
        await Assert.That(finding).IsNotNull();
        await Assert.That(finding!.Severity).IsEqualTo(FindingSeverity.Info);
    }
}
