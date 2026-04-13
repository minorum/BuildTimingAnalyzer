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

    // ────────────────────────── Bottleneck ──────────────────────────

    [Test]
    public async Task SingleProject_NoBottleneckFinding()
    {
        var projects = new List<ProjectTiming> { CreateProject("OnlyProject", 30, 100) };
        var report = CreateReport(projects: projects);
        var result = BuildAnalyzer.Analyze(report);

        await Assert.That(result.Findings.Any(f => f.Title.Contains("Largest self-time share"))).IsFalse();
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

        var finding = result.Findings.FirstOrDefault(f => f.Title.Contains("Largest self-time share"));
        await Assert.That(finding).IsNotNull();
        await Assert.That(finding!.Severity).IsEqualTo(FindingSeverity.Critical);
        await Assert.That(finding.Title).Contains("BigProject");
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

        var finding = result.Findings.FirstOrDefault(f => f.Title.Contains("Largest self-time share"));
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

        var finding = result.Findings.First(f => f.Title.Contains("Largest self-time share"));
        await Assert.That(finding.Evidence).IsNotNull();
        await Assert.That(finding.Evidence.Length).IsGreaterThan(0);
        await Assert.That(finding.ThresholdName).IsNotNull();
        await Assert.That(finding.ThresholdName).Contains("LargestShareCriticalPct");
    }

    // ────────────────────────── Disproportion ──────────────────────

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

        var finding = result.Findings.FirstOrDefault(f => f.Title.Contains("Largest self-time gap"));
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

        await Assert.That(result.Findings.Any(f => f.Title.Contains("Largest self-time gap"))).IsFalse();
    }

    // ────────────────────────── Outlier targets ─────────────────────

    [Test]
    public async Task OutlierTarget_Detected()
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

        var finding = result.Findings.FirstOrDefault(f => f.Title.Contains("Target outlier"));
        await Assert.That(finding).IsNotNull();
        await Assert.That(finding!.Title).Contains("Outlier");
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

    // ────────────────────────── Warnings ────────────────────────────

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

        var finding = result.Findings.FirstOrDefault(f => f.Title.Contains("concentrated"));
        await Assert.That(finding).IsNotNull();
    }

    // ────────────────────────── Critical Path ───────────────────────

    [Test]
    public async Task CriticalPath_FindingShownWhenConcentrated()
    {
        var a = CreateProject("A", 30, 60);
        var b = CreateProject("B", 15, 30);
        var c = CreateProject("C", 5, 10);
        var report = CreateReport(
            projects: [a, b, c],
            criticalPath: [a, b],
            criticalPathTotal: TimeSpan.FromSeconds(45));

        var result = BuildAnalyzer.Analyze(report);
        var finding = result.Findings.FirstOrDefault(f => f.Title.Contains("Critical path"));
        await Assert.That(finding).IsNotNull();
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
            await Assert.That(result.Recommendations[i].Number).IsEqualTo(i + 1);
    }

    // ────────────────────────── Recommendation wording ──────────────

    [Test]
    public async Task Recommendations_DoNotContainStructuralConclusions()
    {
        // Hard rule: no "split the project", no "refactor", etc — investigation-only language
        var projects = new List<ProjectTiming>
        {
            CreateProject("Slow", 40, 80),
            CreateProject("Fast", 5, 10),
        };
        var report = CreateReport(projects: projects);
        var result = BuildAnalyzer.Analyze(report);

        foreach (var rec in result.Recommendations)
        {
            await Assert.That(rec.Text.Contains("split", StringComparison.OrdinalIgnoreCase)).IsFalse();
            await Assert.That(rec.Text.Contains("refactor", StringComparison.OrdinalIgnoreCase)).IsFalse();
        }
    }

    // ──────────────────────── Reference overhead ──────────────────

    [Test]
    public async Task SystemicReferenceOverhead_FiresWhenAllThresholdsPass()
    {
        var projects = Enumerable.Range(1, 10)
            .Select(i => CreateProject($"P{i}", 10, 10))
            .ToList();
        var report = CreateReport(projects: projects) with
        {
            ReferenceOverhead = new ReferenceOverheadStats
            {
                TotalSelfTime = TimeSpan.FromSeconds(15),    // 15% of 100s
                SelfPercent = 15.0,                          // > 10%
                PayingProjectsCount = 8,                     // 80% > 50%
                TotalProjectsCount = 10,
                MedianPerPayingProject = TimeSpan.FromMilliseconds(500), // > 250ms
                TopProjects = [],
            },
        };

        var result = BuildAnalyzer.Analyze(report);
        var finding = result.Findings.FirstOrDefault(f => f.Title.Contains("Reference-related"));
        await Assert.That(finding).IsNotNull();
        await Assert.That(finding!.Severity).IsEqualTo(FindingSeverity.Warning);
    }

    [Test]
    public async Task SystemicReferenceOverhead_DoesNotFireIfMedianTooSmall()
    {
        var projects = Enumerable.Range(1, 10)
            .Select(i => CreateProject($"P{i}", 10, 10))
            .ToList();
        var report = CreateReport(projects: projects) with
        {
            ReferenceOverhead = new ReferenceOverheadStats
            {
                TotalSelfTime = TimeSpan.FromSeconds(15),
                SelfPercent = 15.0,
                PayingProjectsCount = 8,
                TotalProjectsCount = 10,
                MedianPerPayingProject = TimeSpan.FromMilliseconds(100), // below 250ms
                TopProjects = [],
            },
        };

        var result = BuildAnalyzer.Analyze(report);
        await Assert.That(result.Findings.Any(f => f.Title.Contains("Reference-related"))).IsFalse();
    }

    [Test]
    public async Task SystemicReferenceOverhead_DoesNotFireIfConcentrated()
    {
        var projects = Enumerable.Range(1, 10)
            .Select(i => CreateProject($"P{i}", 10, 10))
            .ToList();
        var report = CreateReport(projects: projects) with
        {
            ReferenceOverhead = new ReferenceOverheadStats
            {
                TotalSelfTime = TimeSpan.FromSeconds(15),
                SelfPercent = 15.0,
                PayingProjectsCount = 3, // 30%, below 50% threshold
                TotalProjectsCount = 10,
                MedianPerPayingProject = TimeSpan.FromMilliseconds(500),
                TopProjects = [],
            },
        };

        var result = BuildAnalyzer.Analyze(report);
        await Assert.That(result.Findings.Any(f => f.Title.Contains("Reference-related"))).IsFalse();
    }

    // ──────────────────────── Span outliers ──────────────────────

    [Test]
    public async Task SpanWaitingPattern_FiresWhenEnoughOutliers()
    {
        // 3 outlier projects
        var outliers = new List<ProjectTiming>
        {
            CreateProject("O1", 1, 2) with { StartOffset = TimeSpan.Zero, EndOffset = TimeSpan.FromSeconds(10) },
            CreateProject("O2", 1, 2) with { StartOffset = TimeSpan.Zero, EndOffset = TimeSpan.FromSeconds(12) },
            CreateProject("O3", 1, 2) with { StartOffset = TimeSpan.Zero, EndOffset = TimeSpan.FromSeconds(8) },
        };
        var report = CreateReport(projects: outliers) with { SpanOutliers = outliers };

        var result = BuildAnalyzer.Analyze(report);
        var finding = result.Findings.FirstOrDefault(f => f.Title.Contains("span >> self time"));
        await Assert.That(finding).IsNotNull();
    }

    [Test]
    public async Task SpanWaitingPattern_DoesNotFireForTooFewOutliers()
    {
        var outliers = new List<ProjectTiming>
        {
            CreateProject("O1", 1, 2) with { StartOffset = TimeSpan.Zero, EndOffset = TimeSpan.FromSeconds(10) },
        };
        var report = CreateReport(projects: outliers) with { SpanOutliers = outliers };

        var result = BuildAnalyzer.Analyze(report);
        await Assert.That(result.Findings.Any(f => f.Title.Contains("span >> self time"))).IsFalse();
    }

    // ──────────────────────── Warning wording ────────────────────

    [Test]
    public async Task WarningFinding_ExplicitlyDistinguishesAttributedVsTotal()
    {
        // 30 warnings total, 25 attributed to a single project, 5 unattributed
        var projects = new List<ProjectTiming>
        {
            CreateProject("Warn1", 10, 20, warningCount: 25),
            CreateProject("Clean1", 10, 20),
            CreateProject("Clean2", 10, 20),
            CreateProject("Clean3", 10, 20),
            CreateProject("Clean4", 10, 20),
        };
        var report = CreateReport(projects: projects, warningCount: 30) with { AttributedWarningCount = 25 };

        var result = BuildAnalyzer.Analyze(report);
        var finding = result.Findings.FirstOrDefault(f => f.Title.Contains("Attributed warnings"));
        await Assert.That(finding).IsNotNull();
        await Assert.That(finding!.Measured).Contains("attributed");
        await Assert.That(finding.Measured).Contains("Unattributed: 5");
    }
}
