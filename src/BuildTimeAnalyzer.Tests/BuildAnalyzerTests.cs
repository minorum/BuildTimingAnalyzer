using BuildTimeAnalyzer.Models;
using BuildTimeAnalyzer.Services;

namespace BuildTimeAnalyzer.Tests;

public sealed class BuildAnalyzerTests
{
    private static BuildReport CreateReport(
        TimeSpan? totalDuration = null,
        List<ProjectTiming>? projects = null,
        List<TargetTiming>? targets = null,
        int warningCount = 0,
        List<ProjectTiming>? criticalPath = null,
        TimeSpan? criticalPathTotal = null)
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
            AttributedWarningCount = warningCount,
            WarningsByCode = [],
            GeneratedComInterfaceUsages = [],
            Projects = projects ?? [],
            TopTargets = targets ?? [],
            Context = new BuildContext(),
            CategoryTotals = new Dictionary<TargetCategory, TimeSpan>(),
            ExecutedTargetCount = 0,
            SkippedTargetCount = 0,
            PotentiallyCustomTargets = [],
            ReferenceOverhead = null,
            SpanOutliers = [],
            ProjectCountTax = TestDefaults.EmptyTax((projects ?? []).Count),
            TopTasks = [],
            SkipReasons = [],
            AnalyzerReports = [],
            ProjectDiagnoses = [],
            Graph = TestDefaults.EmptyGraph((projects ?? []).Count),
            CriticalPath = criticalPath ?? [],
            CriticalPathTotal = criticalPathTotal ?? TimeSpan.Zero,
            CriticalPathValidation = TestDefaults.EmptyValidation(),
        };
    }

    private static ProjectTiming CreateProject(string name, double seconds, double percentage, int warningCount = 0) =>
        new()
        {
            Name = name,
            FullPath = $"C:\\src\\{name}\\{name}.csproj",
            SelfTime = TimeSpan.FromSeconds(seconds),
            Succeeded = true,
            ErrorCount = 0,
            WarningCount = warningCount,
            SelfPercent = percentage,
            StartOffset = TimeSpan.Zero,
            EndOffset = TimeSpan.FromSeconds(seconds),
        };

    private static TargetTiming CreateTarget(string name, string project, double seconds, double percentage, TargetCategory? category = null) =>
        new()
        {
            Name = name,
            ProjectName = project,
            SelfTime = TimeSpan.FromSeconds(seconds),
            SelfPercent = percentage,
            Category = category ?? TargetCategorizer.Categorize(name),
        };

    // ────────────────────────── Short build ─────────────────────────

    [Test]
    public async Task ShortBuild_ReturnsEmpty()
    {
        var report = CreateReport(totalDuration: TimeSpan.FromMilliseconds(500));
        var result = BuildAnalyzer.Analyze(report);

        await Assert.That(result.Findings.Count).IsEqualTo(0);
        await Assert.That(result.Recommendations.Count).IsEqualTo(0);
    }

    // ────────────────────────── Top project ──────────────────────────

    [Test]
    public async Task SingleProject_NoTopProjectFinding()
    {
        var projects = new List<ProjectTiming> { CreateProject("OnlyProject", 30, 100) };
        var report = CreateReport(projects: projects);
        var result = BuildAnalyzer.Analyze(report);

        await Assert.That(result.Findings.Any(f => f.Title.Contains("dominates build time"))).IsFalse();
    }

    [Test]
    public async Task TopProject_DetectedAsCritical()
    {
        var projects = new List<ProjectTiming>
        {
            CreateProject("BigProject", 30, 50),
            CreateProject("SmallProject", 5, 8.3),
        };
        var report = CreateReport(projects: projects);
        var result = BuildAnalyzer.Analyze(report);

        var finding = result.Findings.FirstOrDefault(f => f.Title.Contains("dominates build time"));
        await Assert.That(finding).IsNotNull();
        await Assert.That(finding!.Severity).IsEqualTo(FindingSeverity.Critical);
        await Assert.That(finding.Title).Contains("BigProject");
    }

    [Test]
    public async Task TopProject_WarningLevel()
    {
        var projects = new List<ProjectTiming>
        {
            CreateProject("MediumProject", 12, 20),
            CreateProject("SmallProject", 5, 8.3),
        };
        var report = CreateReport(projects: projects);
        var result = BuildAnalyzer.Analyze(report);

        var finding = result.Findings.FirstOrDefault(f => f.Title.Contains("dominates build time"));
        await Assert.That(finding).IsNotNull();
        await Assert.That(finding!.Severity).IsEqualTo(FindingSeverity.Warning);
    }

    // ────────────────────────── Evidence ─────────────────────────────

    [Test]
    public async Task Finding_IncludesEvidenceAndThreshold()
    {
        var projects = new List<ProjectTiming>
        {
            CreateProject("BigProject", 30, 50),
            CreateProject("SmallProject", 5, 8.3),
        };
        var report = CreateReport(projects: projects);
        var result = BuildAnalyzer.Analyze(report);

        var finding = result.Findings.First(f => f.Title.Contains("dominates build time"));
        await Assert.That(finding.Evidence).IsNotNull();
        await Assert.That(finding.Evidence.Length).IsGreaterThan(0);
        await Assert.That(finding.ThresholdName).IsNotNull();
        await Assert.That(finding.ThresholdName).Contains("top-project-share");
    }

    // ────────────────────────── ResolvePackageAssets ─────────────────

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

    // ────────────────────────── Numbering ───────────────────────────

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
            await Assert.That(result.Findings[i].Number).IsEqualTo(i + 1);
    }

    [Test]
    public async Task Recommendations_AlwaysEmpty()
    {
        // Recommendations section was removed; each finding carries its own inspect target.
        var projects = new List<ProjectTiming>
        {
            CreateProject("Big", 30, 50),
            CreateProject("Small", 5, 8.3),
        };
        var report = CreateReport(projects: projects);
        var result = BuildAnalyzer.Analyze(report);

        await Assert.That(result.Recommendations.Count).IsEqualTo(0);
    }
}
