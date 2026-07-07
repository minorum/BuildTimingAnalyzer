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
        TimeSpan? criticalPathTotal = null,
        TimeSpan? totalSelfTime = null,
        int? parallelism = null,
        ProjectCountTaxStats? projectCountTax = null,
        DependencyGraph? graph = null,
        CriticalPathValidation? criticalPathValidation = null)
    {
        var duration = totalDuration ?? TimeSpan.FromSeconds(60);
        var start = new DateTime(2025, 1, 1, 12, 0, 0);
        var projectList = projects ?? [];
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
            Projects = projectList,
            TopTargets = targets ?? [],
            TotalSelfTime = totalSelfTime ?? TimeSpan.Zero,
            Context = new BuildContext { Parallelism = parallelism },
            CategoryTotals = new Dictionary<TargetCategory, TimeSpan>(),
            ExecutedTargetCount = 0,
            SkippedTargetCount = 0,
            PotentiallyCustomTargets = [],
            ReferenceOverhead = null,
            SpanOutliers = [],
            ProjectCountTax = projectCountTax ?? TestDefaults.EmptyTax(projectList.Count),
            TopTasks = [],
            SkipReasons = [],
            AnalyzerReports = [],
            ProjectDiagnoses = [],
            Graph = graph ?? TestDefaults.EmptyGraph(projectList.Count),
            CriticalPath = criticalPath ?? [],
            CriticalPathTotal = criticalPathTotal ?? TimeSpan.Zero,
            CriticalPathValidation = criticalPathValidation ?? TestDefaults.EmptyValidation(),
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

    // ────────────────────────── Largest-share gap ───────────────────

    [Test]
    public async Task LargestShare_ConcentratedWhenFarAheadOfNext()
    {
        var projects = new List<ProjectTiming>
        {
            CreateProject("BigProject", 30, 50),
            CreateProject("SmallProject", 5, 8.3),
        };
        var report = CreateReport(projects: projects);
        var finding = BuildAnalyzer.Analyze(report).Findings.First(f => f.Title.Contains("dominates build time"));

        await Assert.That(finding.Measured).Contains("concentrated here");
        await Assert.That(finding.Measured).Contains("6.0×");
    }

    [Test]
    public async Task LargestShare_ConcentratedWhenRunnerUpIsNegligible()
    {
        var projects = new List<ProjectTiming>
        {
            CreateProject("BigProject", 30, 60),
            CreateProject("TinyProject", 0, 0),
        };
        var report = CreateReport(projects: projects);
        var finding = BuildAnalyzer.Analyze(report).Findings.First(f => f.Title.Contains("dominates build time"));

        // A 0ms runner-up must read as concentrated, never "close behind".
        await Assert.That(finding.Measured).Contains("negligible self time");
        await Assert.That(finding.Measured).DoesNotContain("close behind");
    }

    [Test]
    public async Task LargestShare_SpreadWhenNextIsClose()
    {
        var projects = new List<ProjectTiming>
        {
            CreateProject("BigProject", 20, 30),
            CreateProject("RunnerUp", 18, 27),
        };
        var report = CreateReport(projects: projects);
        var finding = BuildAnalyzer.Analyze(report).Findings.First(f => f.Title.Contains("dominates build time"));

        await Assert.That(finding.Measured).Contains("spread across several projects");
    }

    // ────────────────────────── Serialized build ────────────────────

    private static List<ProjectTiming> ManyProjects(int count) =>
        Enumerable.Range(0, count).Select(i => CreateProject($"P{i}", 10, 100.0 / count)).ToList();

    [Test]
    public async Task SerializedBuild_DetectedWhenCapacityUnderused()
    {
        // 120s of work in 60s wall = 2× achieved against 8 available nodes = 25% of capacity.
        var report = CreateReport(
            totalDuration: TimeSpan.FromSeconds(60),
            projects: ManyProjects(6),
            parallelism: 8,
            totalSelfTime: TimeSpan.FromSeconds(120),
            criticalPathTotal: TimeSpan.FromSeconds(30),
            criticalPathValidation: new CriticalPathValidation
            {
                ComputedTotal = TimeSpan.FromSeconds(30),
                WallClock = TimeSpan.FromSeconds(60),
                Accepted = true,
                Reason = "test",
                GraphWasUsable = true,
            });
        var finding = BuildAnalyzer.Analyze(report).Findings
            .FirstOrDefault(f => f.Title.Contains("under-parallelised"));

        await Assert.That(finding).IsNotNull();
        await Assert.That(finding!.Severity).IsEqualTo(FindingSeverity.Warning);
        // Chain is 30s of 60s wall → up to 50% recoverable, reported in Measured. UpperBoundImpactPercent
        // is a self-time share by contract, so it stays null for this wall-clock-based finding.
        await Assert.That(finding.UpperBoundImpactPercent).IsNull();
        await Assert.That(finding.Measured).Contains("50%");
    }

    [Test]
    public async Task SerializedBuild_NotDetectedWhenParallelismUnknown()
    {
        var report = CreateReport(
            projects: ManyProjects(6),
            parallelism: null,
            totalSelfTime: TimeSpan.FromSeconds(120));
        await Assert.That(BuildAnalyzer.Analyze(report).Findings
            .Any(f => f.Title.Contains("under-parallelised"))).IsFalse();
    }

    [Test]
    public async Task SerializedBuild_NotDetectedWhenCapacityWellUsed()
    {
        // 300s of work in 60s = 5× achieved against 8 nodes = 62% of capacity → above the 50% floor.
        var report = CreateReport(
            projects: ManyProjects(6),
            parallelism: 8,
            totalSelfTime: TimeSpan.FromSeconds(300));
        await Assert.That(BuildAnalyzer.Analyze(report).Findings
            .Any(f => f.Title.Contains("under-parallelised"))).IsFalse();
    }

    // ────────────────────────── Dependency cycles ───────────────────

    [Test]
    public async Task DependencyCycles_DetectedAndRenderedAsLoop()
    {
        var graph = TestDefaults.EmptyGraph(3) with
        {
            Cycles = new List<IReadOnlyList<string>> { new List<string> { "A", "B", "C" } },
        };
        var report = CreateReport(projects: ManyProjects(3), graph: graph);
        var finding = BuildAnalyzer.Analyze(report).Findings
            .FirstOrDefault(f => f.Title.Contains("Dependency cycle"));

        await Assert.That(finding).IsNotNull();
        await Assert.That(finding!.Severity).IsEqualTo(FindingSeverity.Warning);
        await Assert.That(finding.Measured).Contains("A → B → C → A");
    }

    [Test]
    public async Task DependencyCycles_NotDetectedWhenNone()
    {
        var report = CreateReport(projects: ManyProjects(3));
        await Assert.That(BuildAnalyzer.Analyze(report).Findings
            .Any(f => f.Title.Contains("Dependency cycle"))).IsFalse();
    }

    // ────────────────────────── Project-count tax ───────────────────

    [Test]
    public async Task ProjectCountTax_DetectedAsInfoWhenReferencesDominate()
    {
        var tax = new ProjectCountTaxStats
        {
            ReferencesExceedCompileCount = 6,
            ReferencesMajorityCount = 3,
            TinySelfHugeSpanCount = 0,
            TotalProjects = 12,
            PerKindStats = [],
        };
        var report = CreateReport(projects: ManyProjects(12), projectCountTax: tax);
        var finding = BuildAnalyzer.Analyze(report).Findings
            .FirstOrDefault(f => f.Title.Contains("Reference overhead exceeds compile"));

        await Assert.That(finding).IsNotNull();
        await Assert.That(finding!.Severity).IsEqualTo(FindingSeverity.Info);
        await Assert.That(finding.Measured).Contains("6 of 12");
    }

    [Test]
    public async Task ProjectCountTax_NotDetectedBelowMinProjects()
    {
        var tax = new ProjectCountTaxStats
        {
            ReferencesExceedCompileCount = 4,
            ReferencesMajorityCount = 2,
            TinySelfHugeSpanCount = 0,
            TotalProjects = 5,
            PerKindStats = [],
        };
        var report = CreateReport(projects: ManyProjects(5), projectCountTax: tax);
        await Assert.That(BuildAnalyzer.Analyze(report).Findings
            .Any(f => f.Title.Contains("Reference overhead exceeds compile"))).IsFalse();
    }

    // ────────────────────────── Ranking ─────────────────────────────

    [Test]
    public async Task Findings_RankedByImpactWithinSeverity()
    {
        // Two Warning-level findings: the dominant-project one (50% impact) must outrank the
        // Info-level project-count-tax finding, and Warnings precede Info regardless of impact.
        var tax = new ProjectCountTaxStats
        {
            ReferencesExceedCompileCount = 8,
            ReferencesMajorityCount = 4,
            TinySelfHugeSpanCount = 0,
            TotalProjects = 12,
            PerKindStats = [],
        };
        var projects = new List<ProjectTiming> { CreateProject("Big", 30, 20) };
        projects.AddRange(ManyProjects(11));
        var report = CreateReport(projects: projects, projectCountTax: tax);
        var findings = BuildAnalyzer.Analyze(report).Findings;

        var infoIndex = findings.ToList().FindIndex(f => f.Severity == FindingSeverity.Info);
        var warnIndex = findings.ToList().FindIndex(f => f.Severity == FindingSeverity.Warning);
        await Assert.That(warnIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(infoIndex).IsGreaterThan(warnIndex);
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
